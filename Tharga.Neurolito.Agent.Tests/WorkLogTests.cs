using Microsoft.Extensions.Logging.Abstractions;
using Tharga.Neurolito.Agent.Features.Desktop;

namespace Tharga.Neurolito.Agent.Tests;

/// <summary>The screensaver's work graph: work units by hour and by local day, kept across a restart.</summary>
public sealed class WorkLogTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 11, 20, 30, 0, DateTimeKind.Utc);
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"work-log-{Guid.NewGuid():N}.json");

    [Fact]
    public void TodayCountsTheJobsAndWorkUnitsOfTheLocalDay()
    {
        var log = Log();
        log.Add(Now.AddHours(-1), 10);
        log.Add(Now, 2.5);
        log.Add(Now.AddDays(-1), 100);

        var summary = log.Summary(Now);

        Assert.Equal((12.5, 2), (summary.WorkUnitsToday, summary.JobsToday));
    }

    [Fact]
    public void TheHoursEndWithTheCurrentHourAndTheDaysWithToday()
    {
        var log = Log();
        log.Add(Now, 3);
        log.Add(Now.AddHours(-23), 4);
        log.Add(Now.AddHours(-24), 99);
        log.Add(Now.AddDays(-6), 5);

        var summary = log.Summary(Now);

        Assert.Equal(24, summary.Hours.Length);
        Assert.Equal((4d, 3d), (summary.Hours[0], summary.Hours[^1]));
        Assert.Equal(7, summary.Days.Length);
        Assert.Equal((5d, 3d), (summary.Days[0], summary.Days[^1]));
    }

    [Fact]
    public void TheLogSurvivesARestartAndDropsWhatIsOlderThanEightDays()
    {
        var first = Log();
        first.Add(Now.AddDays(-9), 50);
        first.Add(Now, 7);

        var summary = Log().Summary(Now);

        Assert.Equal(7, summary.WorkUnitsToday);
        Assert.Equal(7, summary.Days.Sum());
    }

    [Fact]
    public void ADamagedFileStartsANewLog()
    {
        File.WriteAllText(_path, "not json");

        Assert.Equal(0, Log().Summary(Now).JobsToday);
    }

    private WorkLog Log() => new(_path, TimeZoneInfo.Utc, NullLogger<WorkLog>.Instance);

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
