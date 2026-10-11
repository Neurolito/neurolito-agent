using Tharga.Neurolito.Agent.Features.Desktop;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Tests;

/// <summary>The desktop agent's pure parts: the shape of a job, and when the machine is kept awake.</summary>
public class DesktopAgentTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("  ", 0)]
    [InlineData("Say hello in five words.", 5)]
    [InlineData("one\ntwo\tthree  four", 4)]
    public void TheScreenIsToldHowManyWordsNeverWhich(string text, int words)
    {
        Assert.Equal(words, AgentActivity.WordsIn(text));
    }

    [Fact]
    public void RunningJobsAreCountedAndNeverBelowZero()
    {
        var activity = new AgentActivity();
        var kinds = new List<string>();
        activity.Changed += (_, e) => kinds.Add(e.Kind);

        activity.JobStarted("qwen3:1.7b", 5);
        Assert.Equal(1, activity.Running);
        activity.Tokens(1);
        activity.JobEnded(4, 1200);
        activity.JobEnded(0, 0);

        Assert.Equal(0, activity.Running);
        Assert.Equal(["job-start", "tokens", "job-end", "job-end"], kinds);
    }

    [Theory]
    [InlineData(EnergyMode.Never, true, MachinePresence.Available, false)]
    [InlineData(EnergyMode.WhileWorking, false, MachinePresence.Available, false)]
    [InlineData(EnergyMode.WhileWorking, true, MachinePresence.InUse, true)]
    [InlineData(EnergyMode.WhileReady, false, MachinePresence.Available, true)]
    [InlineData(EnergyMode.WhileReady, false, MachinePresence.InUse, false)]
    [InlineData(EnergyMode.WhileReady, true, MachinePresence.InUse, true)]
    [InlineData(EnergyMode.WhileReady, true, MachinePresence.Asleep, false)]
    public void TheMachineIsKeptAwakeOnlyAsTheModeSays(EnergyMode mode, bool working, MachinePresence presence, bool hold)
    {
        Assert.Equal(hold, EnergyKeeper.ShouldHold(mode, working, presence));
    }

    [Theory]
    [InlineData("https://github.com/Neurolito/neurolito-agent-windows", "GithubSource")]
    [InlineData("https://updates.example.com/neurolito/", "SimpleWebSource")]
    [InlineData(@"C:\ProgramData\NeurolitoFeed", "SimpleFileSource")]
    [InlineData("", null)]
    public void UpdatesComeFromGitHubAWebFolderOrADisk(string source, string kind)
    {
        Assert.Equal(kind, AgentUpdater.SourceOf(source)?.GetType().Name);
    }
}
