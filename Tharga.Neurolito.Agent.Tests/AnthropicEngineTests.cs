using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Options = Tharga.Neurolito.Client.Contract.Options;
using NSubstitute;
using Tharga.Neurolito.Agent.Features.Anthropic;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Tests;

/// <summary>The hosted Anthropic engine, against a fake API: the request it sends, the stream it reads, and what it refuses.</summary>
public class AnthropicEngineTests
{
    private static readonly AnthropicModel Sonnet = new() { Name = "claude-sonnet-5-5", ParameterCount = 50_000_000_000 };

    [Fact]
    public void TheRequestJoinsSystemMessagesAndSendsNoSamplingOptions()
    {
        var request = new EngineChatRequest
        {
            Model = Sonnet.Name,
            Messages =
            [
                new Message { Role = Role.System, Content = "Be brief." },
                new Message { Role = Role.System, Content = "Answer in English." },
                new Message { Role = Role.User, Content = "Hi" },
                new Message { Role = Role.Assistant, Content = "Hello" },
                new Message { Role = Role.User, Content = "Capital of Sweden?" }
            ],
            Options = new Options { Temperature = 0.2f, TopK = 20, Stop = ["END"] }
        };

        var body = AnthropicEngine.Body(request, Sonnet, 32000);

        Assert.Equal("claude-sonnet-5-5", (string)body["model"]);
        Assert.Equal("Be brief.\n\nAnswer in English.", (string)body["system"]);
        Assert.Equal(32000, (int)body["max_tokens"]);
        Assert.True((bool)body["stream"]);
        Assert.Equal(["user", "assistant", "user"], body["messages"]!.AsArray().Select(x => (string)x!["role"]));
        Assert.Equal("END", (string)body["stop_sequences"]![0]);
        Assert.Null(body["temperature"]);
        Assert.Null(body["top_k"]);
        Assert.Null(body["thinking"]);
    }

    [Fact]
    public void AnswerLengthAndThinkingFollowThePrompt()
    {
        var request = new EngineChatRequest { Model = Sonnet.Name, Messages = [new Message { Role = Role.User, Content = "Hi" }], Options = new Options { NumPredict = 100 }, Think = true };

        var body = AnthropicEngine.Body(request, Sonnet, 32000);

        Assert.Equal(100, (int)body["max_tokens"]);
        Assert.Equal("adaptive", (string)body["thinking"]!["type"]);
        Assert.Equal("summarized", (string)body["thinking"]!["display"]);
        Assert.Null(AnthropicEngine.Body(request, Sonnet with { SupportsThinking = false }, 32000)["thinking"]);
    }

    [Fact]
    public async Task TheStreamGivesTextThinkingAndUsageWithCachedInputCounted()
    {
        var api = new FakeApi(HttpStatusCode.OK, Events(
            """{"type":"message_start","message":{"usage":{"input_tokens":12,"cache_creation_input_tokens":100,"cache_read_input_tokens":8,"output_tokens":1}}}""",
            """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Sweden..."}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"abc"}}""",
            """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Stock"}}""",
            """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"holm."}}""",
            """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":42}}""",
            """{"type":"message_stop"}"""));

        var chunks = await ChatAsync(Engine(api));

        Assert.Equal("Sweden...", chunks[0].Thinking);
        Assert.Equal("Stockholm.", string.Concat(chunks.Select(x => x.Content)));
        var usage = chunks[^1].Final;
        Assert.NotNull(usage);
        Assert.Equal(120, usage.InputTokens);
        Assert.Equal(42, usage.OutputTokens);
        Assert.Equal(0, usage.LoadDurationMs);
        Assert.Equal(usage.TotalDurationMs, usage.InputDurationMs + usage.OutputDurationMs);

        var sent = JsonNode.Parse(api.Bodies.Single())!;
        Assert.Equal("claude-sonnet-5-5", (string)sent["model"]);
        Assert.Equal("https://api.anthropic.com/v1/messages", api.Requests.Single().RequestUri!.ToString());
        Assert.Equal("test-key", api.Requests.Single().Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", api.Requests.Single().Headers.GetValues("anthropic-version").Single());
    }

    [Fact]
    public async Task AnErrorInTheStreamFailsWithItsStatus()
    {
        var api = new FakeApi(HttpStatusCode.OK, Events(
            """{"type":"message_start","message":{"usage":{"input_tokens":12,"output_tokens":1}}}""",
            """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""));

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => ChatAsync(Engine(api)));

        Assert.Equal((HttpStatusCode)529, e.StatusCode);
        Assert.Contains("529", e.Message);
    }

    [Fact]
    public async Task ARefusedRequestFailsWithAnthropicsReason()
    {
        var api = new FakeApi(HttpStatusCode.TooManyRequests, """{"type":"error","error":{"type":"rate_limit_error","message":"Number of request tokens has exceeded your per-minute rate limit"}}""");

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => ChatAsync(Engine(api)));

        Assert.Equal(HttpStatusCode.TooManyRequests, e.StatusCode);
        Assert.StartsWith("Response status code does not indicate success: 429", e.Message);
        Assert.Contains("Anthropic: Number of request tokens", e.Message);
    }

    [Fact]
    public async Task AStreamCutShortIsAFailureNotAnAnswer()
    {
        var api = new FakeApi(HttpStatusCode.OK, Events("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Stock"}}"""));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ChatAsync(Engine(api)));
    }

    [Theory]
    [InlineData("claude-sonnet-5-5", true)]
    [InlineData("Claude-Sonnet-5-5", true)]
    [InlineData("claude-sonnet", false)]
    [InlineData("claude-opus-5-5", false)]
    [InlineData("", false)]
    public async Task OnlyAConfiguredModelByItsExactNameIsServed(string model, bool served)
    {
        var engine = Engine(new FakeApi(HttpStatusCode.OK, ""));

        if (served)
        {
            var info = await engine.GetModelInfoAsync(model, CancellationToken.None);
            Assert.Equal(50_000_000_000, info.ParameterCount);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.GetModelInfoAsync(model, CancellationToken.None));
        }
    }

    [Fact]
    public async Task ModelsAreTheConfiguredOnesWithTheirSizeAndNoneAreLoaded()
    {
        var engine = Engine(new FakeApi(HttpStatusCode.OK, ""));

        var models = await engine.GetModelsAsync(CancellationToken.None);

        var model = Assert.Single(models);
        Assert.Equal("claude-sonnet-5-5", model.Name);
        Assert.Equal(50_000_000_000, model.Details.ParameterCount);
        Assert.Equal("50B", model.Details.ParameterSize);
        Assert.Empty(await engine.GetLoadedModelsAsync(CancellationToken.None));
        Assert.False(engine.SupportsSelfTest);
        Assert.Equal(EngineKind.Anthropic, engine.Kind);
    }

    [Fact]
    public async Task NothingIsInstalledOrUninstalled()
    {
        var engine = Engine(new FakeApi(HttpStatusCode.OK, ""));

        Assert.Throws<InvalidOperationException>(() => engine.InstallAsync("claude-sonnet-5-5", null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.UninstallAsync("claude-sonnet-5-5", CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "Anthropic API")]
    [InlineData(HttpStatusCode.Unauthorized, null)]
    public async Task TheNameSaysWhetherTheKeyIsAccepted(HttpStatusCode status, string expected)
    {
        var api = new FakeApi(status, "{}");

        Assert.Equal(expected, await Engine(api).GetNameAsync(CancellationToken.None));
        Assert.Equal("https://api.anthropic.com/v1/models?limit=1", api.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public async Task AnAnthropicRequestIsSentToTheApiWithTheKey()
    {
        var engine = Engine(new FakeApi(HttpStatusCode.OK, ""));

        await using var target = await engine.ResolveAnthropicAsync("claude-sonnet-5-5", CancellationToken.None);

        Assert.Equal("https://api.anthropic.com/", target.Address.ToString());
        Assert.Equal("claude-sonnet-5-5", target.Model);
        Assert.Equal("test-key", target.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", target.Headers["anthropic-version"]);
    }

    [Theory]
    [InlineData("Anthropic", true)]
    [InlineData("anthropic", true)]
    [InlineData("LlamaServer", false)]
    [InlineData(null, false)]
    public void TheEngineKindChoosesAnthropic(string kind, bool expected)
    {
        Assert.Equal(expected, EngineRegistration.IsAnthropic(kind));
    }

    private static AnthropicEngine Engine(FakeApi api)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(AnthropicEngine.ClientName).Returns(_ => new HttpClient(api, false));
        var options = new AnthropicOptions { ApiKey = "test-key", Models = [Sonnet] };
        return new AnthropicEngine(Microsoft.Extensions.Options.Options.Create(options), factory, NullLogger<AnthropicEngine>.Instance);
    }

    private static async Task<List<EngineChunk>> ChatAsync(AnthropicEngine engine)
    {
        var request = new EngineChatRequest { Model = "claude-sonnet-5-5", Messages = [new Message { Role = Role.User, Content = "Capital of Sweden?" }] };
        var chunks = new List<EngineChunk>();
        await foreach (var chunk in engine.ChatAsync(request, CancellationToken.None)) chunks.Add(chunk);
        return chunks;
    }

    private static string Events(params string[] data)
    {
        return string.Concat(data.Select(x => $"event: {JsonNode.Parse(x)!["type"]}\ndata: {x}\n\n"));
    }

    private sealed class FakeApi(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content != null) Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
        }
    }
}
