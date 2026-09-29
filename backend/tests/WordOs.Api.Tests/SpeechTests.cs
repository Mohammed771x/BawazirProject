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

/// <summary>
/// Stands in for the AI service's transcription (ADR-107).
/// </summary>
public sealed class StubSpeechTranscriber : ISpeechTranscriber
{
    /// <summary>What the next recording "said".</summary>
    public string Text { get; set; } = "I go to the market yesterday.";

    /// <summary>When true, every engine is down.</summary>
    public bool Fails { get; set; }

    public byte[]? LastAudio { get; private set; }
    public string? LastMimeType { get; private set; }

    public Task<string> TranscribeAsync(
        byte[] audio, string mimeType, CancellationToken ct = default)
    {
        LastAudio = audio;
        LastMimeType = mimeType;
        if (Fails) throw new SpeechUnavailableException("both engines down");
        return Task.FromResult(Text);
    }
}

/// <summary>
/// <c>POST /api/speech/transcribe</c>, through the whole pipeline.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SpeechEndpointTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly byte[] Recording =
        Encoding.ASCII.GetBytes("\0\0\0\u0018ftypM4A a fake recording");

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
            email = $"speech-{Guid.NewGuid():N}@test.dev",
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

    private Task<HttpResponseMessage> PostAsync(byte[] audio, string type = "audio/mp4")
    {
        var content = new ByteArrayContent(audio);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return Client.PostAsync("/api/speech/transcribe", content);
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetProperty("code").GetString()!;

    [SkippableFact]
    public async Task A_signed_in_learner_gets_the_transcript_back()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        _factory!.Speech.Text = "He don't like coffee.";

        var response = await PostAsync(Recording);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Verbatim, grammar mistake and all: this is the learner's draft.
        Assert.Equal("He don't like coffee.", body.GetProperty("text").GetString());
        Assert.Equal(Recording, _factory.Speech.LastAudio);
        Assert.Equal("audio/mp4", _factory.Speech.LastMimeType);
    }

    [SkippableFact]
    public async Task Nobody_signed_in_is_refused_before_any_engine_is_asked()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var response = await PostAsync(Recording);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(_factory!.Speech.LastAudio);
    }

    [SkippableFact]
    public async Task A_recording_larger_than_a_transcript_is_accepted()
    {
        // The global body limit is sized for text (256 KB). A real turn is
        // bigger than that, and this endpoint alone is allowed the difference.
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var minute = new byte[600 * 1024];
        var response = await PostAsync(minute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(minute.Length, _factory!.Speech.LastAudio!.Length);
    }

    [SkippableFact]
    public async Task A_recording_past_the_audio_limit_is_refused()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var response = await PostAsync(new byte[9 * 1024 * 1024]);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Null(_factory!.Speech.LastAudio);
    }

    [SkippableFact]
    public async Task Something_that_is_not_audio_is_refused()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var response = await PostAsync(Recording, "application/json");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("UNSUPPORTED_AUDIO", await CodeAsync(response));
    }

    [SkippableFact]
    public async Task An_empty_recording_is_refused()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var response = await PostAsync([]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("EMPTY_AUDIO", await CodeAsync(response));
    }

    [SkippableFact]
    public async Task Every_engine_down_is_a_503_the_app_can_offer_typing_on()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        _factory!.Speech.Fails = true;

        var response = await PostAsync(Recording);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("SPEECH_UNAVAILABLE", await CodeAsync(response));
    }
}

/// <summary>
/// The HTTP client that forwards a recording to the AI service. No database,
/// no network: the handler answers.
/// </summary>
public sealed class HttpSpeechTranscriberTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer)
        : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public byte[]? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            Body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct);
            return answer(request);
        }
    }

    private static (HttpSpeechTranscriber, Handler) Build(
        HttpStatusCode status, string body, int slots = 4)
    {
        var handler = new Handler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ai.test") };
        var options = Options.Create(new AiServiceOptions { Token = "service-token" });
        var transcriber = new HttpSpeechTranscriber(
            http, options, new AiCallGate(slots, 1),
            NullLogger<HttpSpeechTranscriber>.Instance);
        return (transcriber, handler);
    }

    [Fact]
    public async Task Forwards_the_audio_with_its_type_and_the_service_token()
    {
        var (transcriber, handler) = Build(HttpStatusCode.OK,
            """{"text":"  I like tea.  ","engine":"gemini","fallback_reason":null}""");
        var audio = new byte[] { 1, 2, 3, 4 };

        var text = await transcriber.TranscribeAsync(audio, "audio/mp4");

        Assert.Equal("I like tea.", text);
        Assert.Equal("/ai/transcribe", handler.Last!.RequestUri!.AbsolutePath);
        Assert.Equal("service-token", handler.Last.Headers.GetValues("X-Service-Token").Single());
        Assert.Equal("audio/mp4", handler.Last.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(audio, handler.Body);
    }

    [Fact]
    public async Task Silence_is_an_empty_transcript_not_an_error()
    {
        var (transcriber, _) = Build(HttpStatusCode.OK,
            """{"text":"","engine":"gemini"}""");

        Assert.Equal("", await transcriber.TranscribeAsync([1], "audio/mp4"));
    }

    [Fact]
    public async Task Both_engines_down_is_speech_unavailable()
    {
        var (transcriber, _) = Build(HttpStatusCode.BadGateway,
            """{"error":{"code":"AI_UNAVAILABLE","message":"x"}}""");

        await Assert.ThrowsAsync<SpeechUnavailableException>(
            () => transcriber.TranscribeAsync([1], "audio/mp4"));
    }

    [Fact]
    public async Task No_engine_configured_is_speech_unavailable()
    {
        var (transcriber, _) = Build(HttpStatusCode.ServiceUnavailable,
            """{"error":{"code":"SPEECH_UNAVAILABLE","message":"x"}}""");

        await Assert.ThrowsAsync<SpeechUnavailableException>(
            () => transcriber.TranscribeAsync([1], "audio/mp4"));
    }

    [Fact]
    public async Task The_AI_service_at_capacity_is_capacity_not_failure()
    {
        // "Try again in a moment", not "we couldn't listen" (ADR-051).
        var (transcriber, _) = Build(HttpStatusCode.ServiceUnavailable,
            """{"error":{"code":"AI_BUSY","message":"x"}}""");

        await Assert.ThrowsAsync<AiCapacityException>(
            () => transcriber.TranscribeAsync([1], "audio/mp4"));
    }

    [Fact]
    public async Task An_unreachable_AI_service_is_speech_unavailable()
    {
        var handler = new Handler(_ => throw new HttpRequestException("refused"));
        var transcriber = new HttpSpeechTranscriber(
            new HttpClient(handler) { BaseAddress = new Uri("http://ai.test") },
            Options.Create(new AiServiceOptions()),
            new AiCallGate(1, 1),
            NullLogger<HttpSpeechTranscriber>.Instance);

        await Assert.ThrowsAsync<SpeechUnavailableException>(
            () => transcriber.TranscribeAsync([1], "audio/mp4"));
    }
}
