using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quilt4Net.Toolkit.Features.Health.Metrics;
using Tharga.Communication.Client;
using Tharga.Communication.Client.Communication;
using Tharga.Neurolito.Agent.Features.Chat;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Agent.Features.Ollama;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Tests;

/// <summary>
/// Every answer says what the engine was doing for it: which engine, and how the model sat in memory when the answer was
/// done. A model running partly on the CPU, or an engine updated since last week, then shows on the row of the run it
/// slowed down, without anyone logging in to the machine.
/// </summary>
public class EngineRunSnapshotTests
{
    private const string Model = "llama3.1:8b-instruct-q4_K_M";

    [Fact]
    public async Task AnAnswerCarriesWhatTheEngineWasDoingWithTheModel()
    {
        var expires = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        var engine = new FakeEngine { Name = "Ollama 0.40.1", Loaded = [new LoadedModel { Name = Model, SizeBytes = 9_000_000_000, VramBytes = 6_000_000_000, ContextLength = 4096, ExpiresUtc = expires }] };

        var response = await Respond(engine, timeoutMinutes: 5);

        var run = response.Usage.EngineRun;
        Assert.Equal("Ollama 0.40.1", run.Engine);
        Assert.Equal(9_000_000_000, run.SizeBytes);
        Assert.Equal(6_000_000_000, run.VramBytes);
        Assert.Equal(4096, run.ContextLength);
        Assert.Equal(expires, run.ExpiresUtc);
        Assert.Equal(5, run.KeepAliveMinutes);
        Assert.Equal(2m / 3m, run.VramShare);
    }

    [Fact]
    public async Task AModelNotInMemoryAfterTheAnswerGivesOnlyTheEngine()
    {
        var response = await Respond(new FakeEngine { Name = "Ollama 0.40.1", Loaded = [] });

        Assert.Equal("Ollama 0.40.1", response.Usage.EngineRun.Engine);
        Assert.Null(response.Usage.EngineRun.SizeBytes);
        Assert.Null(response.Usage.EngineRun.VramShare);
    }

    [Fact]
    public async Task AnEngineThatCannotBeAskedLeavesTheSnapshotOutButTheAnswerStands()
    {
        var response = await Respond(new FakeEngine { Name = "Ollama 0.40.1", LoadedError = new HttpRequestException("gone") });

        Assert.Equal("Hello", response.Output);
        Assert.Null(response.Usage.Exception);
        Assert.Null(response.Usage.EngineRun);
    }

    [Fact]
    public async Task TheModelIsFoundWhateverTheCaseOfItsName()
    {
        var engine = new FakeEngine { Name = "Ollama 0.40.1", Loaded = [new LoadedModel { Name = Model.ToUpperInvariant(), SizeBytes = 100, VramBytes = 100 }] };

        var response = await Respond(engine);

        Assert.Equal(100, response.Usage.EngineRun.SizeBytes);
        Assert.Equal(1m, response.Usage.EngineRun.VramShare);
    }

    [Fact]
    public void AllOfTheModelInVideoMemoryIsAShareOfOneNeverMore()
    {
        Assert.Equal(1m, new EngineRun { SizeBytes = 100, VramBytes = 140 }.VramShare);
        Assert.Null(new EngineRun { SizeBytes = 0, VramBytes = 0 }.VramShare);
        Assert.Null(new EngineRun { SizeBytes = 100 }.VramShare);
    }

    [Fact]
    public void AnOlderServerNeverSeesTheFieldWhenThereIsNoSnapshot()
    {
        var usage = new Usage { InputTokens = 1, InputDurationMs = 1, OutputTokens = 1, OutputDurationMs = 1, LoadDurationMs = 1, TotalDurationMs = 1 };

        var json = JsonSerializer.Serialize(usage);

        Assert.DoesNotContain("EngineRun", json);
    }

    [Fact]
    public void AnOlderAgentsUsageStillReads()
    {
        const string json = "{\"InputTokens\":1,\"InputDurationMs\":1,\"OutputTokens\":2,\"OutputDurationMs\":1,\"LoadDurationMs\":1,\"TotalDurationMs\":1}";

        var usage = JsonSerializer.Deserialize<Usage>(json);

        Assert.Equal(2, usage.OutputTokens);
        Assert.Null(usage.EngineRun);
    }

    private static Task<AgentPromptResponse> Respond(FakeEngine engine, int? timeoutMinutes = null)
    {
        var service = new EngineService(engine, Substitute.For<IClientCommunication>(), Substitute.For<IMetricsService>(), Substitute.For<IInstanceService>(), new FakeEvents(), NullLogger<EngineService>.Instance);
        var request = new RequestDto { Model = Model, Messages = [new Message { Role = Role.User, Content = "Tell me a joke" }], TimeoutMinutes = timeoutMinutes };

        return service.GetResponseAsync(request, respondToServer: false, CancellationToken.None);
    }

    private sealed class FakeEvents : IEventService
    {
        public event EventHandler<EventArgs> CancelEvent;
        public void OnCancel() => CancelEvent?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeEngine : IEngine
    {
        public string Name { get; init; } = "Fake 1.0";
        public LoadedModel[] Loaded { get; init; } = [];
        public Exception LoadedError { get; init; }

        public string Kind => EngineKind.Ollama;
        public int? AnthropicContextLength => null;
        public Task<string> GetNameAsync(CancellationToken cancellationToken) => Task.FromResult(Name);
        public Task<string[]> GetModelNamesAsync(CancellationToken cancellationToken) => Task.FromResult<string[]>([Model]);
        public Task<LLModel[]> GetModelsAsync(CancellationToken cancellationToken) => Task.FromResult<LLModel[]>([new LLModel { Name = Model, Loaded = true, Details = null }]);
        public Task<LoadedModel[]> GetLoadedModelsAsync(CancellationToken cancellationToken) => LoadedError != null ? Task.FromException<LoadedModel[]>(LoadedError) : Task.FromResult(Loaded);
        public Task<EngineModelInfo> GetModelInfoAsync(string model, CancellationToken cancellationToken) => Task.FromResult(new EngineModelInfo(null, false));
        public IAsyncEnumerable<double> InstallAsync(string model, ModelSource source, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UninstallAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AnthropicTarget> ResolveAnthropicAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TimeSpan> LoadAsync(string model, int? keepAliveMinutes, CancellationToken cancellationToken) => Task.FromResult(TimeSpan.Zero);

        public async IAsyncEnumerable<EngineChunk> ChatAsync(EngineChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new EngineChunk { Content = "Hello" };
            yield return new EngineChunk { Final = new EngineUsage { InputTokens = 5, OutputTokens = 1, InputDurationMs = 10, OutputDurationMs = 10, LoadDurationMs = 0, TotalDurationMs = 20 } };
            await Task.CompletedTask;
        }
    }
}
