using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Tharga.Neurolito.Agent.Features.Desktop;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Tests;

/// <summary>
/// The server can ask an agent to look for a new version now, and to apply it, instead of waiting for the next check
/// hours away. The question and the timer go through one rule, so an agent behaves the same whoever asks. A check says
/// what it found and why it cannot update when it cannot; applying answers at once and goes on in the background, since
/// the agent exits to start on the new version and cannot answer after that.
/// </summary>
public class UpdateOnDemandTests
{
    [Fact]
    public async Task ACheckSaysThereIsANewerVersionWithoutInstallingIt()
    {
        var engine = new FakeEngine { Latest = "0.1.7" };
        var updater = Updater(engine);

        var reply = await updater.CheckAsync(apply: false, CancellationToken.None);

        Assert.Equal(UpdateStatus.Available, reply.Status);
        Assert.Equal("0.1.6", reply.Current);
        Assert.Equal("0.1.7", reply.Latest);
        Assert.Equal(0, engine.Downloads);
        Assert.False(engine.Applied);
    }

    [Fact]
    public async Task AnAgentOnTheNewestVersionSaysSo()
    {
        var reply = await Updater(new FakeEngine { Latest = null }).CheckAsync(false, CancellationToken.None);

        Assert.Equal(UpdateStatus.UpToDate, reply.Status);
        Assert.Equal("0.1.6", reply.Current);
        Assert.Null(reply.Latest);
    }

    [Fact]
    public async Task ApplyingAnswersAtOnceThenDownloadsDrainsAppliesAndExits()
    {
        var engine = new FakeEngine { Latest = "0.1.7" };
        var exited = new TaskCompletionSource();
        var presence = Substitute.For<IPresenceMonitor>();
        var updater = Updater(engine, presence: presence, exit: () => exited.TrySetResult());

        var reply = await updater.CheckAsync(apply: true, CancellationToken.None);

        Assert.Equal(UpdateStatus.Updating, reply.Status);
        Assert.Equal("0.1.7", reply.Latest);
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, engine.Downloads);
        Assert.True(engine.Applied);
        presence.Received().Hold(Arg.Is<string>(x => x.Contains("0.1.7")));
    }

    [Fact]
    public async Task ARunningJobIsWaitedForBeforeTheAgentUpdates()
    {
        var engine = new FakeEngine { Latest = "0.1.7" };
        var activity = Substitute.For<IAgentActivity>();
        var running = 1;
        activity.Running.Returns(_ => Volatile.Read(ref running));
        var exited = new TaskCompletionSource();
        var updater = Updater(engine, activity: activity, exit: () => exited.TrySetResult());

        await updater.CheckAsync(true, CancellationToken.None);
        await Task.Delay(200);
        Assert.False(exited.Task.IsCompleted);

        Volatile.Write(ref running, 0);
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(engine.Applied);
    }

    [Fact]
    public async Task AskingAgainWhileUpdatingDoesNotStartASecondUpdate()
    {
        var engine = new FakeEngine { Latest = "0.1.7", DownloadGate = new TaskCompletionSource() };
        var exited = new TaskCompletionSource();
        var updater = Updater(engine, exit: () => exited.TrySetResult());

        await updater.CheckAsync(true, CancellationToken.None);
        var second = await updater.CheckAsync(true, CancellationToken.None);

        Assert.Equal(UpdateStatus.Updating, second.Status);
        engine.DownloadGate.SetResult();
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, engine.Downloads);
    }

    [Fact]
    public async Task AnAgentNotInstalledByTheInstallerSaysSoAndDoesNothing()
    {
        var engine = new FakeEngine { Installed = false, Latest = "0.1.7" };

        var reply = await Updater(engine).CheckAsync(true, CancellationToken.None);

        Assert.Equal(UpdateStatus.NotInstalled, reply.Status);
        Assert.Equal(0, engine.Downloads);
    }

    [Fact]
    public async Task AFailedCheckIsReportedNotThrown()
    {
        var reply = await Updater(new FakeEngine { CheckError = new HttpRequestException("github unreachable") }).CheckAsync(false, CancellationToken.None);

        Assert.Equal(UpdateStatus.Failed, reply.Status);
        Assert.Contains("github unreachable", reply.Message);
    }

    [Fact]
    public async Task AFailedUpdateLetsTheAgentGoOnAndAskAgain()
    {
        var engine = new FakeEngine { Latest = "0.1.7", DownloadError = new IOException("disk full") };
        var presence = Substitute.For<IPresenceMonitor>();
        var updater = Updater(engine, presence: presence);

        await updater.CheckAsync(true, CancellationToken.None);
        await WaitUntil(() => presence.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IPresenceMonitor.Release)));

        engine.DownloadError = null;
        var again = await updater.CheckAsync(false, CancellationToken.None);
        Assert.Equal(UpdateStatus.Available, again.Status);
    }

    [Fact]
    public async Task WithNoUpdaterTheHandlerSaysUpdatesAreOff()
    {
        var handler = new CheckForUpdateRequestHandler();

        var reply = await handler.Handle(new CheckForUpdateRequest { Apply = true });

        Assert.Equal(UpdateStatus.Disabled, reply.Status);
    }

    [Fact]
    public async Task TheHandlerPassesTheRequestToTheUpdater()
    {
        var service = Substitute.For<IAgentUpdateService>();
        service.CheckAsync(true, Arg.Any<CancellationToken>()).Returns(new CheckForUpdateResponse { Status = UpdateStatus.Updating, Current = "0.1.6", Latest = "0.1.7" });

        var reply = await new CheckForUpdateRequestHandler(service).Handle(new CheckForUpdateRequest { Apply = true });

        Assert.Equal(UpdateStatus.Updating, reply.Status);
    }

    [Fact]
    public void TheMessagesReadFromJsonAnOlderPeerWouldSend()
    {
        Assert.False(JsonSerializer.Deserialize<CheckForUpdateRequest>("{}").Apply);

        var reply = JsonSerializer.Deserialize<CheckForUpdateResponse>("{\"Status\":\"UpToDate\",\"Current\":\"0.1.6\"}");
        Assert.Equal(UpdateStatus.UpToDate, reply.Status);
        Assert.Null(reply.Latest);
    }

    private static AgentUpdater Updater(FakeEngine engine, IPresenceMonitor presence = null, IAgentActivity activity = null, Action exit = null)
    {
        activity ??= Substitute.For<IAgentActivity>();
        return new AgentUpdater(Microsoft.Extensions.Options.Options.Create(new UpdateOptions { Enabled = true, Source = "https://github.com/Neurolito/neurolito-agent-windows" }),
            presence ?? Substitute.For<IPresenceMonitor>(), activity, NullLogger<AgentUpdater>.Instance, installs: null, engine: engine, exit: exit ?? (() => { }), handover: TimeSpan.Zero, drainPoll: TimeSpan.FromMilliseconds(20));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(condition());
    }

    private sealed class FakeEngine : IUpdateEngine
    {
        public bool Installed { get; init; } = true;
        public string Latest { get; init; }
        public Exception CheckError { get; init; }
        public Exception DownloadError { get; set; }
        public TaskCompletionSource DownloadGate { get; init; }
        public int Downloads { get; private set; }
        public bool Applied { get; private set; }

        public bool IsInstalled => Installed;
        public string CurrentVersion => "0.1.6";
        public Task<string> CheckAsync() => CheckError != null ? Task.FromException<string>(CheckError) : Task.FromResult(Latest);

        public async Task DownloadAsync(CancellationToken cancellationToken)
        {
            Downloads++;
            if (DownloadGate != null) await DownloadGate.Task;
            if (DownloadError != null) throw DownloadError;
        }

        public void ApplyAfterExit() => Applied = true;
    }
}
