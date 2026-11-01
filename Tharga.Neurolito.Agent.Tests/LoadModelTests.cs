using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quilt4Net.Toolkit.Features.Health.Metrics;
using Tharga.Communication.Client;
using Tharga.Communication.Client.Communication;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Agent.Features.Ollama;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Tests;

/// <summary>Loading a model ahead of a request the server expects soon: nothing is generated.</summary>
public class LoadModelTests
{
    [Fact]
    public async Task AFamilyIsLoadedAsTheInstalledModelItStandsFor()
    {
        var engine = new FakeEngine(["gemma4:12b"], loaded: []) { LoadTime = TimeSpan.FromSeconds(9.5) };

        var response = await Service(engine).LoadModelAsync("gemma4", 10, CancellationToken.None);

        Assert.True(response.Success);
        Assert.Equal("gemma4:12b", response.Model);
        Assert.False(response.AlreadyLoaded);
        Assert.Equal(9500, response.LoadDurationMs);
        Assert.Equal(("gemma4:12b", 10), engine.Loads.Single());
        Assert.Equal(0, engine.Chats);
    }

    [Fact]
    public async Task AModelLoadedAlreadyIsSaidToBe()
    {
        var response = await Service(new FakeEngine(["gemma4:12b"], loaded: ["gemma4:12b"])).LoadModelAsync("gemma4:12b", null, CancellationToken.None);

        Assert.True(response.Success);
        Assert.True(response.AlreadyLoaded);
    }

    [Fact]
    public async Task AModelNotInstalledIsAFailureNotAnException()
    {
        var response = await Service(new FakeEngine(["llama3.2:3b"], loaded: [])).LoadModelAsync("gemma4:12b", null, CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains("gemma4:12b", response.Error);
    }

    [Fact]
    public async Task AnEngineThatFailsToLoadIsAFailure()
    {
        var engine = new FakeEngine(["gemma4:12b"], loaded: []) { LoadError = new InvalidOperationException("out of memory") };

        var response = await Service(engine).LoadModelAsync("gemma4:12b", null, CancellationToken.None);

        Assert.False(response.Success);
        Assert.Equal("out of memory", response.Error);
    }

    private static EngineService Service(IEngine engine)
    {
        return new EngineService(engine, Substitute.For<IClientCommunication>(), Substitute.For<IMetricsService>(), Substitute.For<IInstanceService>(), new FakeEvents(), NullLogger<EngineService>.Instance);
    }

    private sealed class FakeEvents : IEventService
    {
        public event EventHandler<EventArgs> CancelEvent;
        public void OnCancel() => CancelEvent?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeEngine(string[] installed, string[] loaded) : IEngine
    {
        public TimeSpan LoadTime { get; init; }
        public Exception LoadError { get; init; }
        public List<(string Model, int? KeepAlive)> Loads { get; } = [];
        public int Chats { get; private set; }

        public string Kind => EngineKind.Ollama;
        public int? AnthropicContextLength => null;
        public Task<string> GetNameAsync(CancellationToken cancellationToken) => Task.FromResult("Fake 1.0");
        public Task<string[]> GetModelNamesAsync(CancellationToken cancellationToken) => Task.FromResult(installed);
        public Task<LLModel[]> GetModelsAsync(CancellationToken cancellationToken) => Task.FromResult(installed.Select(x => new LLModel { Name = x, Loaded = loaded.Contains(x), Details = null }).ToArray());
        public Task<LoadedModel[]> GetLoadedModelsAsync(CancellationToken cancellationToken) => Task.FromResult(loaded.Select(x => new LoadedModel { Name = x }).ToArray());
        public Task<EngineModelInfo> GetModelInfoAsync(string model, CancellationToken cancellationToken) => Task.FromResult(new EngineModelInfo(null, false));
        public IAsyncEnumerable<double> InstallAsync(string model, ModelSource source, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UninstallAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AnthropicTarget> ResolveAnthropicAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TimeSpan> LoadAsync(string model, int? keepAliveMinutes, CancellationToken cancellationToken)
        {
            Loads.Add((model, keepAliveMinutes));
            return LoadError != null ? Task.FromException<TimeSpan>(LoadError) : Task.FromResult(LoadTime);
        }

        public IAsyncEnumerable<EngineChunk> ChatAsync(EngineChatRequest request, CancellationToken cancellationToken)
        {
            Chats++;
            return AsyncEnumerable.Empty<EngineChunk>();
        }
    }
}
