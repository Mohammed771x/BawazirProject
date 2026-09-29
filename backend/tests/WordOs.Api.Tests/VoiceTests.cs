using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WordOs.Application.Abstractions;
using WordOs.Infrastructure.Ai;
using Xunit;

namespace WordOs.Api.Tests;

/// <summary>Stands in for Gemini's voice (ADR-108).</summary>
public sealed class StubSpeechSynthesizer : ISpeechSynthesizer
{
    public bool Fails { get; set; }
    public string? LastText { get; private set; }

    public Task<SynthesizedSpeech> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        LastText = text;
        if (Fails) throw new SpeechUnavailableException("no voice");
        return Task.FromResult(new SynthesizedSpeech(
            Convert.ToBase64String([1, 2, 3]),
            "audio/mpeg",
            1500,
            [new SpokenWord(100, 400, 0, 5), new SpokenWord(400, 900, 6, 11)],
            "aligned"));
    }
}

/// <summary><c>POST /api/speech/synthesize</c>, through the whole pipeline.</summary>
[Collection(PostgresCollection.Name)]
public sealed class VoiceEndpointTests(PostgresFixture db) : IAsyncLifetime
{
    private ApiFactory? _factory;
    private HttpClient? _client;

    public Task InitializeAsync()
    {
        if (db.IsAvailable)
        {
            _factory = new ApiFactory(db.ConnectionString);
            _client = _factory.CreateClient();
        }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    private HttpClient Client => _client!;

    private async Task SignInAsync()
    {
        var response = await Client.PostAsJsonAsync("/api/auth/register", new
        {
            email = $"voice-{Guid.NewGuid():N}@test.dev",
            password = "correct-horse-battery",
            displayName = "Learner",
            phoneCountryCode = "967",
            phoneNumber = "771234567",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString()!;

    [SkippableFact]
    public async Task A_learner_gets_audio_with_a_time_on_every_word()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var response = await Client.PostAsJsonAsync("/api/speech/synthesize",
            new { text = "Hello there" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("audio/mpeg", body.GetProperty("mimeType").GetString());
        Assert.Equal(1500, body.GetProperty("durationMs").GetInt32());
        Assert.Equal("aligned", body.GetProperty("timing").GetString());
        var second = body.GetProperty("words")[1];
        Assert.Equal(400, second.GetProperty("start").GetInt32());
        Assert.Equal(6, second.GetProperty("charStart").GetInt32());
        Assert.Equal([1, 2, 3], Convert.FromBase64String(body.GetProperty("audio").GetString()!));
    }

    [SkippableFact]
    public async Task The_text_is_passed_on_exactly_as_sent()
    {
        // The word positions are offsets into this string; trimming it here
        // would shift every one of them.
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        await Client.PostAsJsonAsync("/api/speech/synthesize", new { text = "  Hi.  " });

        Assert.Equal("  Hi.  ", _factory!.Voice.LastText);
    }

    [SkippableFact]
    public async Task The_voice_has_its_own_budget_and_never_spends_the_tutors()
    {
        // ADR-110: a Listening session asks for all its audio ahead. On the AI
        // budget, that would leave the tutor and the microphone refused for
        // the rest of the minute.
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        using var client = _factory!.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("RateLimits:ExpensivePermitsPerMinute", "2");
                builder.UseSetting("RateLimits:VoicePermitsPerMinute", "5");
            })
            .CreateClient();
        var registered = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = $"voice-budget-{Guid.NewGuid():N}@test.dev",
            password = "correct-horse-battery",
            displayName = "Learner",
            phoneCountryCode = "967",
            phoneNumber = "771234567",
        });
        registered.EnsureSuccessStatusCode();
        var token = (await registered.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        // More voice than the AI budget allows, all of it served.
        for (var i = 0; i < 5; i++)
        {
            var spoken = await client.PostAsJsonAsync("/api/speech/synthesize",
                new { text = $"Line {i}." });
            Assert.Equal(HttpStatusCode.OK, spoken.StatusCode);
        }

        // And the microphone's budget is untouched by it.
        using var audio = new ByteArrayContent(new byte[4096]);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/mp4");
        var heard = await client.PostAsync("/api/speech/transcribe", audio);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, heard.StatusCode);

        // The voice's own budget still holds.
        var sixth = await client.PostAsJsonAsync("/api/speech/synthesize",
            new { text = "One too many." });
        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
    }

    [SkippableFact]
    public async Task Nobody_signed_in_is_refused()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var response = await Client.PostAsJsonAsync("/api/speech/synthesize", new { text = "Hi" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(_factory!.Voice.LastText);
    }

    [SkippableFact]
    public async Task Empty_and_oversized_text_are_refused()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var empty = await Client.PostAsJsonAsync("/api/speech/synthesize", new { text = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("EMPTY_TEXT", await CodeAsync(empty));

        var huge = await Client.PostAsJsonAsync("/api/speech/synthesize",
            new { text = new string('a', 1501) });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
        Assert.Equal("TEXT_TOO_LONG", await CodeAsync(huge));
        Assert.Null(_factory!.Voice.LastText);
    }

    [SkippableFact]
    public async Task No_voice_is_a_503_the_app_falls_back_on()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        _factory!.Voice.Fails = true;

        var response = await Client.PostAsJsonAsync("/api/speech/synthesize", new { text = "Hi" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("VOICE_UNAVAILABLE", await CodeAsync(response));
    }
}

public sealed class HttpSpeechSynthesizerTests
{
    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Sent { get; private set; }
        public HttpRequestMessage? Last { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            Sent = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (HttpSpeechSynthesizer, Handler) Build(HttpStatusCode status, string body)
    {
        var handler = new Handler(status, body);
        var synthesizer = new HttpSpeechSynthesizer(
            new HttpClient(handler) { BaseAddress = new Uri("http://ai.test") },
            Options.Create(new AiServiceOptions { Token = "service-token" }),
            new AiCallGate(2, 1),
            NullLogger<HttpSpeechSynthesizer>.Instance);
        return (synthesizer, handler);
    }

    [Fact]
    public async Task Reads_audio_and_timings_from_the_AI_service()
    {
        var (synthesizer, handler) = Build(HttpStatusCode.OK, """
            {"audio":"AQID","mime_type":"audio/mpeg","duration_ms":900,
             "words":[{"start_ms":10,"end_ms":300,"char_start":0,"char_end":2}],
             "timing":"aligned","model":"gemini-3.8-flash-tts"}
            """);

        var speech = await synthesizer.SynthesizeAsync("Hi");

        Assert.Equal("AQID", speech.Audio);
        Assert.Equal(900, speech.DurationMs);
        Assert.Equal(new SpokenWord(10, 300, 0, 2), speech.Words.Single());
        Assert.Equal("/ai/synthesize", handler.Last!.RequestUri!.AbsolutePath);
        Assert.Equal("service-token", handler.Last.Headers.GetValues("X-Service-Token").Single());
        Assert.Contains("\"text\":\"Hi\"", handler.Sent);
    }

    [Fact]
    public async Task A_failed_voice_is_speech_unavailable()
    {
        var (synthesizer, _) = Build(HttpStatusCode.BadGateway,
            """{"error":{"code":"AI_UNAVAILABLE","message":"x"}}""");

        await Assert.ThrowsAsync<SpeechUnavailableException>(() => synthesizer.SynthesizeAsync("Hi"));
    }

    [Fact]
    public async Task An_answer_without_audio_is_speech_unavailable()
    {
        var (synthesizer, _) = Build(HttpStatusCode.OK, """{"audio":"","words":[]}""");

        await Assert.ThrowsAsync<SpeechUnavailableException>(() => synthesizer.SynthesizeAsync("Hi"));
    }

    [Fact]
    public async Task The_AI_service_at_capacity_is_capacity()
    {
        var (synthesizer, _) = Build(HttpStatusCode.ServiceUnavailable,
            """{"error":{"code":"AI_BUSY","message":"x"}}""");

        await Assert.ThrowsAsync<AiCapacityException>(() => synthesizer.SynthesizeAsync("Hi"));
    }
}
