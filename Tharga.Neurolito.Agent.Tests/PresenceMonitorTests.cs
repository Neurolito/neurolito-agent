using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Tharga.Communication.Client;
using Tharga.Communication.Client.Communication;
using Tharga.Neurolito.Agent.Features.Desktop;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Tests;

/// <summary>When a desktop agent counts its machine as free of its user, whatever platform reports the console.</summary>
public class PresenceMonitorTests
{
    private const string SomeoneThere = "someone is using the machine";

    private static PresenceMonitor Monitor(ConsoleState? console, bool alwaysWork = false, int resumeDelaySeconds = 15)
    {
        var session = Substitute.For<IConsoleSession>();
        session.Read().Returns(console);
        var options = Microsoft.Extensions.Options.Options.Create(new DesktopOptions { Enabled = true, AlwaysWork = alwaysWork, ResumeDelaySeconds = resumeDelaySeconds });
        return new PresenceMonitor(options, session, Substitute.For<IClientCommunication>(), Substitute.For<ISignalRHostedService>(), NullLogger<PresenceMonitor>.Instance);
    }

    [Fact]
    public void SomeoneLoggedOnAndUnlockedIsUsingTheMachine()
    {
        Assert.Equal((false, SomeoneThere), Monitor(new ConsoleState(true, false)).Observe());
    }

    [Fact]
    public void ALockedConsoleIsFree()
    {
        Assert.Equal((true, "the machine is locked"), Monitor(new ConsoleState(true, true)).Observe());
    }

    [Fact]
    public void NobodyLoggedOnIsFree()
    {
        Assert.Equal((true, "nobody is logged on"), Monitor(new ConsoleState(false, false)).Observe());
    }

    [Fact]
    public void AnUnreadableConsoleCountsAsSomeoneThere()
    {
        Assert.Equal((false, SomeoneThere), Monitor(null).Observe());
    }

    [Fact]
    public void AnUnreadableConsoleFallsBackOnSessionEvents()
    {
        var monitor = Monitor(null);

        monitor.SessionChanged(locked: true, loggedOn: true);
        Assert.Equal((true, "the machine is locked"), monitor.Observe());

        monitor.SessionChanged(locked: false, loggedOn: null);
        Assert.Equal((false, SomeoneThere), monitor.Observe());

        monitor.SessionChanged(locked: null, loggedOn: false);
        Assert.Equal((true, "nobody is logged on"), monitor.Observe());
    }

    [Fact]
    public void WhatTheConsoleSaysNowBeatsAnOlderSessionEvent()
    {
        var monitor = Monitor(new ConsoleState(true, false));

        monitor.SessionChanged(locked: true, loggedOn: true);

        Assert.Equal((false, SomeoneThere), monitor.Observe());
    }

    [Fact]
    public void AnUnknownLockStateFallsBackOnTheSessionEvent()
    {
        var monitor = Monitor(new ConsoleState(true, null));
        Assert.Equal((false, SomeoneThere), monitor.Observe());

        monitor.SessionChanged(locked: true, loggedOn: null);
        Assert.Equal((true, "the machine is locked"), monitor.Observe());
    }

    [Fact]
    public void ARunningScreensaverFreesTheMachineUntilItEnds()
    {
        var monitor = Monitor(new ConsoleState(true, false));

        monitor.ScreensaverRunning(true);
        Assert.Equal((true, "the screensaver is on"), monitor.Observe());

        monitor.ScreensaverRunning(false);
        Assert.Equal((false, SomeoneThere), monitor.Observe());
    }

    [Fact]
    public void MoreScreensaversEndingThanStartingNeverLeavesOneCounted()
    {
        var monitor = Monitor(new ConsoleState(true, false));

        monitor.ScreensaverRunning(false);
        monitor.ScreensaverRunning(true);
        Assert.Equal((true, "the screensaver is on"), monitor.Observe());

        monitor.ScreensaverRunning(false);
        Assert.Equal((false, SomeoneThere), monitor.Observe());
    }

    [Fact]
    public void AlwaysWorkIsFreeWhoeverIsThere()
    {
        Assert.Equal((true, "set to work all the time"), Monitor(new ConsoleState(true, false), alwaysWork: true).Observe());
    }

    [Fact]
    public void AHoldWinsOverEverythingUntilReleased()
    {
        var monitor = Monitor(new ConsoleState(false, false), alwaysWork: true);
        monitor.ScreensaverRunning(true);

        monitor.Hold("updating to v1.2.3");
        Assert.Equal((false, "updating to v1.2.3"), monitor.Observe());

        monitor.Release();
        Assert.Equal((true, "set to work all the time"), monitor.Observe());
    }

    [Fact]
    public void BecomingFreeWaitsTheResumeDelay()
    {
        var monitor = Monitor(new ConsoleState(true, false));

        monitor.ScreensaverRunning(true);

        Assert.Equal(MachinePresence.InUse, monitor.Current);
    }

    [Fact]
    public void WithNoResumeDelayTheMachineIsFreeAtOnceAndInUseAtOnce()
    {
        var monitor = Monitor(new ConsoleState(true, false), resumeDelaySeconds: 0);

        monitor.ScreensaverRunning(true);
        Assert.Equal(MachinePresence.Available, monitor.Current);

        monitor.ScreensaverRunning(false);
        Assert.Equal(MachinePresence.InUse, monitor.Current);
    }
}
