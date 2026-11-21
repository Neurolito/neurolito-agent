using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quilt4Net.Toolkit.Features.Health.Metrics;
using Tharga.Communication.Client;
using Tharga.Communication.Client.Communication;
using Tharga.Neurolito.Agent.Features.Chat;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Tests;

/// <summary>
/// How long the engine keeps a model loaded after a run is decided by the server: the agent's owner sets it
/// per machine, and it is applied to every run. The request's own timeout still counts, so a job that may run for
/// ten minutes never has its model unloaded halfway, and an agent the server does not configure behaves as before.
/// </summary>
public class KeepAliveTests
{
    [Theory]
    [InlineData(null, null, null)]
    [InlineData(5, null, 5)]
    [InlineData(null, 30, 30)]
    [InlineData(5, 30, 30)]
    [InlineData(60, 30, 60)]
    [InlineData(null, 0, null)]
    [InlineData(5, -3, 5)]
    public void TheLargerOfTheRequestsTimeoutAndTheServersSettingWins(int? timeout, int? server, int? expected)
    {
        Assert.Equal(expected, KeepAlive.Choose(timeout, server));
    }

    [Fact]
    public async Task TheServersSettingIsAppliedToTheRun()
    {
        var run = (await Respond(timeoutMinutes: 5, serverKeepAliveMinutes: 30)).Usage.EngineRun;

        Assert.Equal(30, run.KeepAliveMinutes);
    }

    [Fact]
    public async Task WithoutASettingTheRequestsTimeoutIsUsedAsBefore()
    {
        var run = (await Respond(timeoutMinutes: 5, serverKeepAliveMinutes: null)).Usage.EngineRun;

        Assert.Equal(5, run.KeepAliveMinutes);
    }

    [Fact]
    public void AnOlderServerSendsNoSettingAndTheRequestStillReads()
    {
        const string json = "{\"RequestId\":\"" + "00000000-0000-0000-0000-000000000001" + "\",\"Messages\":[],\"TimeoutMinutes\":5}";

        var request = System.Text.Json.JsonSerializer.Deserialize<PromptRequest>(json);

        Assert.Null(request.KeepAliveMinutes);
        Assert.DoesNotContain("KeepAliveMinutes", System.Text.Json.JsonSerializer.Serialize(request));
    }

    private static Task<AgentPromptResponse> Respond(int? timeoutMinutes, int? serverKeepAliveMinutes)
    {
        var engine = new EngineRunSnapshotTests.FakeEngine { Name = "Ollama 0.40.1" };
        var service = new EngineService(engine, Substitute.For<IClientCommunication>(), Substitute.For<IMetricsService>(), Substitute.For<IInstanceService>(), new EngineRunSnapshotTests.FakeEvents(), NullLogger<EngineService>.Instance);
        var request = new RequestDto { Model = EngineRunSnapshotTests.Model, Messages = [new Message { Role = Role.User, Content = "Hi" }], TimeoutMinutes = timeoutMinutes, KeepAliveMinutes = serverKeepAliveMinutes };

        return service.GetResponseAsync(request, respondToServer: false, CancellationToken.None);
    }
}
