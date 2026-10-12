using NSubstitute;
using Tharga.Neurolito.Agent.Features.Desktop;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Tests;

/// <summary>The desktop settings the server can see and change: the agent's own, the power plan, the users' screensavers.</summary>
public sealed class DesktopSettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "neurolito-desktop-" + Guid.NewGuid().ToString("N"));

    private DesktopOptions Options() => new() { Enabled = true, SettingsPath = Path.Combine(_folder, "desktop.json") };

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    [Fact]
    public void AChangeAppliesAtOnceAndOutlivesARestart()
    {
        var options = Options();
        var store = new DesktopSettingsStore(options);
        var raised = 0;
        store.Changed += (_, _) => raised++;

        Assert.Null(store.Change(EnergyMode.WhileReady, null, 30));

        Assert.Equal(EnergyMode.WhileReady, options.EnergyMode);
        Assert.Equal(30, options.ResumeDelaySeconds);
        Assert.Equal(1, raised);

        //NOTE: As the next start reads it: the installer said WhileWorking and AlwaysWork; the file wins where it has a value.
        var restarted = Options();
        restarted.EnergyMode = EnergyMode.WhileWorking;
        restarted.AlwaysWork = true;
        DesktopSettingsStore.ApplySaved(restarted);

        Assert.Equal(EnergyMode.WhileReady, restarted.EnergyMode);
        Assert.Equal(30, restarted.ResumeDelaySeconds);
        Assert.True(restarted.AlwaysWork);
    }

    [Fact]
    public void WithoutASettingsFileTheInstallersSettingsStand()
    {
        var options = Options();
        options.EnergyMode = EnergyMode.WhileWorking;

        DesktopSettingsStore.ApplySaved(options);

        Assert.Equal(EnergyMode.WhileWorking, options.EnergyMode);
        Assert.Equal(15, options.ResumeDelaySeconds);
    }

    [Fact]
    public void AChangeThatCannotBeKeptStillAppliesAndSaysWhy()
    {
        Directory.CreateDirectory(_folder);
        var blocker = Path.Combine(_folder, "not-a-folder");
        File.WriteAllText(blocker, "");
        var options = new DesktopOptions { Enabled = true, SettingsPath = Path.Combine(blocker, "desktop.json") };

        var error = new DesktopSettingsStore(options).Change(null, true, null);

        Assert.True(options.AlwaysWork);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task ANewEnergyModeTakesEffectWithoutARestart()
    {
        if (!OperatingSystem.IsWindows()) return;

        var options = Options();
        var store = new DesktopSettingsStore(options);
        var presence = Substitute.For<IPresenceMonitor>();
        presence.Current.Returns(MachinePresence.Available);
        var activity = Substitute.For<IAgentActivity>();
        using var keeper = new EnergyKeeper(Microsoft.Extensions.Options.Options.Create(options), presence, activity,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EnergyKeeper>.Instance, store);

        await keeper.StartAsync(CancellationToken.None);
        Assert.False(keeper.Holding);

        store.Change(EnergyMode.WhileReady, null, null);
        Assert.True(keeper.Holding);

        store.Change(EnergyMode.Never, null, null);
        Assert.False(keeper.Holding);

        await keeper.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void ThePowerPlanIsReadFromWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var plan = new WindowsPowerPlan().Read();

        Assert.NotNull(plan);
        Assert.False(string.IsNullOrEmpty(plan.Plan));
        Assert.True(plan.SleepAcSeconds >= 0 && plan.DisplayAcSeconds >= 0);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(3600, true)]
    [InlineData(3601, false)]
    public void TheResumeDelayIsKeptInRange(int seconds, bool valid)
    {
        var errors = ChangeDesktopSettingsRequestHandler.Validate(new ChangeDesktopSettingsRequest { ResumeDelaySeconds = seconds });
        Assert.Equal(valid, errors.Count == 0);
    }

    [Fact]
    public void OnlyTheValidPartsOfAChangeAreMade()
    {
        var store = Substitute.For<IDesktopSettingsStore>();
        var power = Substitute.For<IPowerPlan>();
        var screensavers = Substitute.For<IUserScreensavers>();
        Succeeds(store, power, screensavers);
        var request = new ChangeDesktopSettingsRequest
        {
            EnergyMode = DesktopEnergyMode.WhileReady,
            ResumeDelaySeconds = -5,
            SleepAcSeconds = 0,
            DisplayAcSeconds = -1,
            Screensavers = [new ScreensaverChange { User = @"PC\ann", Active = true, TimeoutSeconds = 10 }]
        };

        var errors = ChangeDesktopSettingsRequestHandler.Apply(request, store, power, screensavers);

        Assert.Equal(3, errors.Count);
        store.Received(1).Change(EnergyMode.WhileReady, null, null);
        power.Received(1).Write(0, null, null, null);
        screensavers.Received(1).Change(Arg.Is<ScreensaverChange>(x => x.User == @"PC\ann" && x.Active == true && x.TimeoutSeconds == null));
    }

    [Fact]
    public void ARefusedChangeIsReported()
    {
        var store = Substitute.For<IDesktopSettingsStore>();
        var power = Substitute.For<IPowerPlan>();
        Succeeds(store, power, null);
        power.Write(Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>()).Returns("Access is denied.");

        var errors = ChangeDesktopSettingsRequestHandler.Apply(new ChangeDesktopSettingsRequest { SleepAcSeconds = 1800 }, store, power, null);

        Assert.Equal(["Access is denied."], errors);
    }

    /// <summary>Null is success; a substitute would answer an empty string.</summary>
    private static void Succeeds(IDesktopSettingsStore store, IPowerPlan power, IUserScreensavers screensavers)
    {
        store.Change(default, default, default).ReturnsForAnyArgs((string)null);
        power.Write(default, default, default, default).ReturnsForAnyArgs((string)null);
        screensavers?.Change(default!).ReturnsForAnyArgs((string)null);
    }

    [Fact]
    public void TheSameSettingsInAnotherOrderAreNotSentAgain()
    {
        UserScreensaver Ann() => new() { User = @"PC\ann", Neurolito = true, Active = true, TimeoutSeconds = 600 };
        UserScreensaver Bob() => new() { User = @"PC\bob", TimeoutSeconds = 300 };
        var power = new PowerTimeouts { Plan = "Balanced", SleepAcSeconds = 1800, DisplayAcSeconds = 600 };

        var one = new DesktopSettings { EnergyMode = DesktopEnergyMode.WhileReady, Power = power, Screensavers = [Ann(), Bob()] };
        var other = new DesktopSettings { EnergyMode = DesktopEnergyMode.WhileReady, Power = power with { }, Screensavers = [Bob(), Ann()] };
        var changed = other with { Power = power with { SleepAcSeconds = 0 } };

        Assert.Equal(DesktopSettingsReporter.Fingerprint(one), DesktopSettingsReporter.Fingerprint(other));
        Assert.NotEqual(DesktopSettingsReporter.Fingerprint(one), DesktopSettingsReporter.Fingerprint(changed));
    }

    [Fact]
    public void TheAgentsModeIsSentInTheContractsWords()
    {
        var store = Substitute.For<IDesktopSettingsStore>();
        store.EnergyMode.Returns(EnergyMode.WhileWorking);
        store.ResumeDelaySeconds.Returns(15);

        var settings = DesktopSettingsReporter.Compose(store, null, [], null);

        Assert.Equal(DesktopEnergyMode.WhileWorking, settings.EnergyMode);
        Assert.Equal(15, settings.ResumeDelaySeconds);
    }

    [Fact]
    public void AScreensaverChangeGoesOnlyToThatUsersHelperAndOnlyWhenTheScreensaverIsOurs()
    {
        var screensavers = new UserScreensavers();
        var ann = new List<string>();
        var bob = new List<string>();
        screensavers.Report(Guid.NewGuid(), UserScreensavers.Parse("""{"type":"screensaver","neurolito":true,"active":true,"timeoutSeconds":600}""", @"PC\ann")!, ann.Add);
        screensavers.Report(Guid.NewGuid(), UserScreensavers.Parse("""{"type":"screensaver","neurolito":false,"active":true,"timeoutSeconds":300}""", @"PC\bob")!, bob.Add);

        Assert.Null(screensavers.Change(new ScreensaverChange { User = @"pc\ANN", TimeoutSeconds = 900 }));
        Assert.NotNull(screensavers.Change(new ScreensaverChange { User = @"PC\bob", TimeoutSeconds = 900 }));
        Assert.NotNull(screensavers.Change(new ScreensaverChange { User = @"PC\carl", Active = false }));

        Assert.Equal(["""{"type":"set-screensaver","timeoutSeconds":900}"""], ann);
        Assert.Empty(bob);
    }

    [Fact]
    public void AHelperThatGoesAwayTakesItsUserWithIt()
    {
        var screensavers = new UserScreensavers();
        var raised = 0;
        screensavers.Changed += (_, _) => raised++;
        var client = Guid.NewGuid();
        var report = UserScreensavers.Parse("""{"type":"screensaver","neurolito":true,"active":true,"timeoutSeconds":600}""", @"PC\ann")!;

        screensavers.Report(client, report, _ => { });
        screensavers.Report(client, report with { }, _ => { });
        Assert.Single(screensavers.Current);
        Assert.Equal(1, raised);

        screensavers.Disconnected(client);
        Assert.Empty(screensavers.Current);
        Assert.Equal(2, raised);
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("""{"type":"other"}""")]
    [InlineData("{not json")]
    public void OnlyAScreensaverLineIsAReport(string line)
    {
        Assert.Null(UserScreensavers.Parse(line, @"PC\ann"));
    }
}
