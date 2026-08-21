using Microsoft.AspNetCore.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using WordOs.Api.Endpoints;
using WordOs.Application.Abstractions;
using WordOs.Domain.Common;
using WordOs.Domain.Levels;
using WordOs.Domain.Placement;
using WordOs.Domain.Users;
using WordOs.Infrastructure.Ai;
using WordOs.Infrastructure.Persistence;
using WordOs.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration & secrets ──────────────────────────────────────────────────
//
// Nothing secret is in source or in appsettings.json. Locally these come from
// user-secrets (~/.microsoft/usersecrets/…, mode 600); in deployment from
// environment variables or a secret manager. A missing value is a startup
// failure, never a silent fallback.
var connectionString =
    builder.Configuration.GetConnectionString("WordOs")
    ?? throw new InvalidOperationException(
        $"""
         ConnectionStrings:WordOs is not configured (environment: {builder.Environment.EnvironmentName}).

         Locally: user-secrets are only loaded when ASPNETCORE_ENVIRONMENT=Development.
             export ASPNETCORE_ENVIRONMENT=Development
             dotnet user-secrets set "ConnectionStrings:WordOs" "..." --project src/WordOs.Api

         In deployment: set ConnectionStrings__WordOs as an environment variable
         or supply it from the secret manager.

         Startup fails deliberately rather than falling back to a default
         database — a silent fallback is how a service ends up writing to the
         wrong instance.
         """);

// ── Capacity ─────────────────────────────────────────────────────────────────
//
// What this process is allowed to consume, so that too many learners at once
// makes the service *slower* rather than broken (ADR-051). Every value is
// configurable, because the right number depends on the machine and on how
// many instances share the database (rule R3).
var capacity = builder.Configuration.GetSection(CapacityOptions.SectionName)
    .Get<CapacityOptions>() ?? new CapacityOptions();

builder.Services.AddSingleton(capacity);

// What the socket layer will hold. Left unbounded, a burst is accepted in full
// and then starves itself: every connection gets a sliver of the machine and
// none of them finish. A ceiling means the ones that got in are served
// properly, and the rest are refused at the door — which a client can retry.
builder.WebHost.ConfigureKestrel(options =>
{
    if (capacity.MaxConcurrentConnections > 0)
        options.Limits.MaxConcurrentConnections = capacity.MaxConcurrentConnections;

    // A transcript is a few hundred characters. Anything of this size is a
    // mistake or an attempt to make the server allocate on demand.
    options.Limits.MaxRequestBodySize = capacity.MaxRequestBodyBytes;
});

// The database is a shared, finite resource: PostgreSQL accepts a fixed number
// of connections and every instance of this service draws from the same pool.
// Left to its defaults, one instance claims up to 100 — the whole of a stock
// server — and the next connection anyone asks for, from any process, fails
// with `53300: remaining connection slots are reserved`. Measured at 200
// concurrent readers before this was set: the database ran out, and learners
// got a 500.
//
// A bounded pool moves that queue inside this process, where waiting is cheap
// and ordered, instead of letting it become an error at the far end.
var dataSource = new Npgsql.NpgsqlConnectionStringBuilder(connectionString)
{
    MaxPoolSize = capacity.DatabaseConnections,
    // A connection that cannot be had is worth waiting a few seconds for; a
    // request that waits for ever is a thread held hostage.
    Timeout = capacity.DatabaseConnectionWaitSeconds,
    // Long enough for the heaviest report, short enough that a stuck query
    // does not hold a connection all day.
    CommandTimeout = capacity.DatabaseCommandTimeoutSeconds,
}.ConnectionString;

// Pooled: a DbContext is created and thrown away on every request, and at a
// thousand learners that allocation is worth avoiding. The behaviour is
// identical — the same scoped lifetime, reset between uses.
builder.Services.AddDbContextPool<WordOsDbContext>(options =>
{
    options.UseNpgsql(dataSource, npgsql =>
        // A network blip, a failover, a restarted database: transient by
        // definition, and previously a 500 in the learner's face. Retried
        // here, where the retry is invisible and safe.
        npgsql.EnableRetryOnFailure(
            maxRetryCount: capacity.DatabaseRetries,
            maxRetryDelay: TimeSpan.FromSeconds(capacity.DatabaseRetryDelaySeconds),
            errorCodesToAdd: null));

    // Parameter values carry a learner's writing and their email, so they must
    // never reach a log — off in every environment, Development included
    // (docs/07-SECURITY.md §9).
    options.EnableSensitiveDataLogging(false);
    options.EnableDetailedErrors(builder.Environment.IsDevelopment());
});

// Validated on first use: a missing or short signing key stops the process
// rather than producing forgeable tokens.
builder.Services
    .AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
// Bound from configuration rather than constructed with its defaults —
// rule R3 says nothing tunable is hard-coded, and until now none of these
// values could actually be changed without a rebuild. The record's own
// defaults remain the documented production values, so an absent section
// behaves exactly as before.
builder.Services.AddSingleton(
    builder.Configuration.GetSection("WordOs").Get<WordOsConfiguration>()
    ?? new WordOsConfiguration());
// Bounded rather than unlimited: Argon2id costs 19 MiB and a core per
// verification, so an unthrottled sign-in queue is a way to exhaust the machine
// (ADR-051). A hash waits its turn; a wait too long becomes a "busy, try
// again", never a wrong-password answer.
builder.Services.AddSingleton<IPasswordHasher>(_ =>
    new ThrottledPasswordHasher(
        new Argon2PasswordHasher(),
        capacity.EffectivePasswordHashes,
        capacity.PasswordHashWaitSeconds));
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddScoped<LevelEngine>();
builder.Services.AddSingleton<IFreeResponseScorer, HeuristicFreeResponseScorer>();
builder.Services.AddSingleton<PlacementEngine>();

// ── AI service ───────────────────────────────────────────────────────────────
//
// The backend is the only thing that talks to the Python service, and the
// Python service is the only thing that talks to Gemini. Neither the service
// token nor the Gemini key exists anywhere the Flutter client can reach.
builder.Services
    .AddOptions<AiServiceOptions>()
    .Bind(builder.Configuration.GetSection(AiServiceOptions.SectionName))
    .ValidateDataAnnotations();

builder.Services.AddHttpClient<HttpAiContentService>((provider, client) =>
{
    var aiOptions = provider
        .GetRequiredService<IOptions<AiServiceOptions>>().Value;
    client.BaseAddress = new Uri(aiOptions.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(aiOptions.TimeoutSeconds);
});

// Registered through the resilient wrapper, never directly: a Gemini outage
// must degrade a session, not end it.
//
// The throttle is a singleton and the wrapper is not — the ceiling has to be
// shared by every request in the process, which is the whole point of it, while
// the decorator below it stays per-request as it always was (ADR-051).
builder.Services.AddSingleton(_ => new AiCallGate(
    capacity.ConcurrentAiCalls, capacity.AiCallWaitSeconds));

builder.Services.AddScoped<IAiContentService>(provider =>
    provider.GetRequiredService<AiCallGate>().Wrap(
        new ResilientAiContentService(
            provider.GetRequiredService<HttpAiContentService>(),
            provider.GetRequiredService<ILogger<ResilientAiContentService>>())));

// ── Authentication ───────────────────────────────────────────────────────────
var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
var signingKey = jwtSection["SigningKey"]
    ?? throw new InvalidOperationException(
        "Jwt:SigningKey is not configured. Generate one with " +
        "`openssl rand -base64 48` and store it in user-secrets or the " +
        "environment. It must never be committed.");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            // Every one of these is checked. Skipping any of them is how a
            // token from another environment ends up being accepted.
            ValidateIssuer = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidateAudience = true,
            ValidAudience = jwtSection["Audience"],
            ValidateIssuerSigningKey = true,
            IssuerSigningKey =
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ValidateLifetime = true,
            // Default is five minutes, which would keep expired tokens working.
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.OwnerOnly, policy =>
        policy.RequireRole(nameof(UserRole.Owner)));

// ── Rate limiting (docs/07-SECURITY.md §6) ───────────────────────────────────
//
// The budgets are configuration, not constants (rule R3): a local run that
// registers a dozen throwaway learners must not need the production limit
// relaxed in code. The defaults below ARE the production values — an
// environment that wants something looser has to say so explicitly, so nothing
// is quietly weakened by omission.
var limits = builder.Configuration.GetSection(RateLimitOptions.SectionName)
    .Get<RateLimitOptions>() ?? new RateLimitOptions();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Authentication is the credential-stuffing surface, so it is partitioned
    // by IP rather than by user — an attacker trying many accounts would
    // otherwise get a fresh budget for each one.
    options.AddPolicy(RateLimitPolicies.Authentication, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limits.AuthenticationPermits,
                Window = TimeSpan.FromMinutes(limits.AuthenticationWindowMinutes),
            }));

    // Lookup fires on every keystroke, so the budget is generous but bounded.
    options.AddPolicy(RateLimitPolicies.Lookup, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            PartitionKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limits.LookupPermitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
            }));

    // Anything that will cost an AI call.
    options.AddPolicy(RateLimitPolicies.Expensive, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            PartitionKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limits.ExpensivePermitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
            }));

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
        context => RateLimitPartition.GetFixedWindowLimiter(
            PartitionKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limits.GlobalPermitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
            }));

    static string PartitionKey(HttpContext context) =>
        context.User.UserId()?.ToString()
        ?? context.Connection.RemoteIpAddress?.ToString()
        ?? "unknown";
});

// ── CORS ─────────────────────────────────────────────────────────────────────
//
// Closed by default. The client is a mobile app, which is not subject to CORS
// at all; an origin is allowed only when one is explicitly configured, and
// never `AllowAnyOrigin` together with credentials (docs/07-SECURITY.md §11).
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length == 0) return;
        policy.WithOrigins(allowedOrigins)
            .AllowCredentials()
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
            .WithHeaders("Authorization", "Content-Type");
    }));

builder.Services.AddProblemDetails();

var app = builder.Build();

// The schema is deliberately NOT migrated here (ADR-062).
//
// Tried, and the database refused it — correctly. This service connects as
// `wordos_app`, which holds no DDL rights at all; owning the schema is
// `wordos_migrator`'s job and nothing else's (docs/07-SECURITY.md §10). Making
// startup migration work would mean handing the running service a credential
// that can drop every table, to save a command that is run once per release.
//
// Migrations are applied from a developer's machine before the deploy:
//
//     ConnectionStrings__WordOs="…migrator…" dotnet ef database update \
//       --project src/WordOs.Infrastructure --startup-project src/WordOs.Api
//
// See docs/09-DEPLOYMENT.md.

// A malformed query or route value is the caller's mistake, not the server's.
// ASP.NET raises it as an exception *after* the endpoint filter chain, so
// without this every `?days=abc` — anything typed into a numeric field, or any
// stale link — answered 500 and was logged as a server fault.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    ExceptionHandler = async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerFeature>();

        var (status, code, message) = feature?.Error switch
        {
            BadHttpRequestException => (
                StatusCodes.Status400BadRequest,
                "INVALID_PARAMETER",
                "One of the values in that request could not be read."),
            // Overload, not failure: the work was never attempted, so the
            // honest answer is "ask again shortly" rather than a wrong-password
            // or a generic fault (ADR-051).
            PasswordHashingBusyException => (
                StatusCodes.Status503ServiceUnavailable,
                "SERVER_BUSY",
                "The server is busy right now. Try again in a moment."),
            AiCapacityException => (
                StatusCodes.Status503ServiceUnavailable,
                "AI_BUSY",
                "The lesson service is busy right now. Try again in a moment."),
            _ => (
                StatusCodes.Status500InternalServerError,
                "INTERNAL_ERROR",
                // Deliberately says nothing about what failed: the details are
                // in the log, where an attacker cannot read them
                // (docs/07-SECURITY.md §8).
                "Something went wrong. Please try again."),
        };

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(
            new { error = new { code, message } });
    },
});

// HSTS and HTTPS redirection outside development, where the local backend runs
// on plain loopback.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// After authentication, deliberately. The per-user policies partition on the
// caller's id, and before this point `context.User` is still anonymous — so
// running the limiter first silently turns every per-user budget into a
// per-IP one, and a school or office behind one NAT would share a single
// learner's allowance. The authentication policy is IP-partitioned by design
// and is unaffected by the move.
app.UseRateLimiter();

// Liveness does not touch the database; readiness does. Keeping them separate
// means "the app is up" and "the app can serve" stay distinguishable.
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));

app.MapGet("/health/ready", async (
    WordOsDbContext db, AiCallGate gate, CancellationToken ct) =>
{
    var canConnect = await db.Database.CanConnectAsync(ct);
    return canConnect
        // How much headroom is left, so a load balancer or a person watching a
        // dashboard can see saturation coming instead of discovering it from
        // the learners (ADR-051).
        ? Results.Ok(new
        {
            status = "ok",
            database = "connected",
            aiSlotsFree = gate.Available,
        })
        // No exception detail: a probe must not disclose the host, the database
        // name or the credentials.
        : Results.Problem(
            title: "Database unavailable",
            statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapAuthEndpoints();
app.MapOnboardingEndpoints();
app.MapSettingsEndpoints();
app.MapHubEndpoints();
app.MapPlacementEndpoints();
app.MapSessionEndpoints();
app.MapWeeklyReviewEndpoints();
app.MapWordEndpoints();
app.MapAdminEndpoints();
app.MapFeedbackEndpoints();

app.Run();

/// <summary>Exposed so the integration tests can drive the real app.</summary>
public partial class Program;
