using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Tests;

/// <summary>
/// One line per request says what the run was, and a run that is slower than it should be says why it probably is:
/// the model partly on the CPU, or a sudden drop in tokens per second, so the log of an agent tells the story without
/// anyone logging in to the machine.
/// </summary>
public class RunReportTests
{
    private const string Model = "gemma4:12b";

    [Fact]
    public void TheLineSaysWhatTheRunWas()
    {
        var run = new EngineRun { Engine = "Ollama 0.40.1", SizeBytes = 100, VramBytes = 100, ContextLength = 4096, KeepAliveMinutes = 5 };

        var line = RunReport.Describe(Model, outputTokens: 120, outputMs: 3000, loadMs: 20, run);

        Assert.Contains(Model, line);
        Assert.Contains("120 tokens", line);
        Assert.Contains("40.0 tokens/s", line);
        Assert.Contains("100% in video memory", line);
        Assert.Contains("Ollama 0.40.1", line);
        Assert.Contains("keep-alive 5 min", line);
    }

    [Fact]
    public void TheLineStillReadsWithoutASnapshot()
    {
        var line = RunReport.Describe(Model, 10, 1000, 0, null);

        Assert.Contains("10.0 tokens/s", line);
        Assert.DoesNotContain("video memory", line);
    }

    [Fact]
    public void AModelPartlyOnTheCpuIsWarnedAbout()
    {
        var warning = RunReport.PartialCpuWarning(Model, new EngineRun { SizeBytes = 100, VramBytes = 60 });

        Assert.Contains("60%", warning);
        Assert.Contains("CPU", warning);
    }

    [Fact]
    public void AModelEntirelyInVideoMemoryOrUnknownIsNotWarnedAbout()
    {
        Assert.Null(RunReport.PartialCpuWarning(Model, new EngineRun { SizeBytes = 100, VramBytes = 100 }));
        Assert.Null(RunReport.PartialCpuWarning(Model, new EngineRun { SizeBytes = 100 }));
        Assert.Null(RunReport.PartialCpuWarning(Model, null));
    }

    [Fact]
    public void ASuddenDropInSpeedIsWarnedAbout()
    {
        var pace = new PaceWatch();
        pace.Observe(Model, 100m, loadMs: 0);
        pace.Observe(Model, 110m, loadMs: 0);

        var warning = pace.Observe(Model, 30m, loadMs: 0);

        Assert.NotNull(warning);
        Assert.Contains("30.0", warning);
        Assert.Contains("110.0", warning);
    }

    [Fact]
    public void ASteadySpeedIsNotWarnedAbout()
    {
        var pace = new PaceWatch();
        pace.Observe(Model, 100m, 0);

        Assert.Null(pace.Observe(Model, 80m, 0));
    }

    [Fact]
    public void TheFirstRunAfterALoadIsNeitherComparedNorUsedAsTheYardstick()
    {
        var pace = new PaceWatch();
        pace.Observe(Model, 100m, 0);

        Assert.Null(pace.Observe(Model, 2m, loadMs: 20_000));
        Assert.Null(pace.Observe(Model, 90m, 0));
    }

    [Fact]
    public void EachModelHasItsOwnYardstick()
    {
        var pace = new PaceWatch();
        pace.Observe("fast:1b", 200m, 0);

        Assert.Null(pace.Observe("slow:70b", 5m, 0));
    }

    [Fact]
    public void ARunWithNoOutputSpeedIsIgnored()
    {
        var pace = new PaceWatch();
        pace.Observe(Model, 100m, 0);

        Assert.Null(pace.Observe(Model, 0m, 0));
    }
}
