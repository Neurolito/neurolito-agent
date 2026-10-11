using System.Text.Json;

namespace Tharga.Neurolito.Agent.Features.Desktop;

/// <summary>
/// The work this machine has done, by the hour, for the screensaver's graph: jobs and work units, never anything of
/// what the jobs said. Kept for eight days in a small file, so the graph survives the service restarting.
/// </summary>
public interface IWorkLog
{
    event EventHandler Changed;

    /// <summary>Today and the hours and days before it, in the machine's local time.</summary>
    WorkSummary Summary(DateTime utcNow);
}

/// <summary>
/// <see cref="Hours"/> is the work units of the last 24 hours, oldest first and ending with the current hour;
/// <see cref="Days"/> the last 7 days the same way.
/// </summary>
public record WorkSummary(double WorkUnitsToday, int JobsToday, double[] Hours, double[] Days);

internal sealed class WorkLog : IWorkLog
{
    private static readonly TimeSpan Kept = TimeSpan.FromDays(8);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly TimeZoneInfo _zone;
    private readonly ILogger<WorkLog> _logger;
    private readonly Lock _lock = new();
    private readonly SortedDictionary<DateTime, Bucket> _hours = new();

    public WorkLog(IAgentActivity activity, ILogger<WorkLog> logger) : this(DefaultPath(), TimeZoneInfo.Local, logger)
    {
        activity.Changed += (_, e) =>
        {
            if (e.Kind == "job-end") Add(DateTime.UtcNow, e.WorkUnits);
        };
    }

    internal WorkLog(string path, TimeZoneInfo zone, ILogger<WorkLog> logger)
    {
        _path = path;
        _zone = zone;
        _logger = logger;
        Load();
    }

    public event EventHandler Changed;

    internal static string DefaultPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Neurolito", "work-log.json");

    internal void Add(DateTime utcNow, double? workUnits)
    {
        var hour = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, 0, 0, DateTimeKind.Utc);
        lock (_lock)
        {
            var bucket = _hours.GetValueOrDefault(hour) ?? new Bucket();
            _hours[hour] = bucket with { Jobs = bucket.Jobs + 1, WorkUnits = bucket.WorkUnits + Math.Max(0, workUnits ?? 0) };
            foreach (var old in _hours.Keys.Where(x => x < utcNow - Kept).ToArray()) _hours.Remove(old);
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public WorkSummary Summary(DateTime utcNow)
    {
        KeyValuePair<DateTime, Bucket>[] hours;
        lock (_lock) hours = _hours.ToArray();

        var thisHour = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, 0, 0, DateTimeKind.Utc);
        var today = TimeZoneInfo.ConvertTimeFromUtc(utcNow, _zone).Date;
        DateTime LocalDay(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, _zone).Date;

        var last24 = Enumerable.Range(0, 24).Select(i => thisHour.AddHours(i - 23))
            .Select(h => hours.Where(x => x.Key == h).Sum(x => x.Value.WorkUnits)).ToArray();
        var last7 = Enumerable.Range(0, 7).Select(i => today.AddDays(i - 6))
            .Select(d => hours.Where(x => LocalDay(x.Key) == d).Sum(x => x.Value.WorkUnits)).ToArray();
        var todays = hours.Where(x => LocalDay(x.Key) == today).ToArray();

        return new WorkSummary(todays.Sum(x => x.Value.WorkUnits), todays.Sum(x => x.Value.Jobs), last24, last7);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var stored = JsonSerializer.Deserialize<Dictionary<DateTime, Bucket>>(File.ReadAllText(_path), Json);
            lock (_lock)
            {
                foreach (var (hour, bucket) in stored ?? []) _hours[DateTime.SpecifyKind(hour.ToUniversalTime(), DateTimeKind.Utc)] = bucket;
            }
        }
        catch (Exception e)
        {
            //NOTE: A graph is not worth stopping for; a damaged file starts the log again.
            _logger.LogWarning(e, "Could not read the work log {Path}; starting a new one.", _path);
        }
    }

    private void Save()
    {
        try
        {
            Dictionary<DateTime, Bucket> copy;
            lock (_lock) copy = new Dictionary<DateTime, Bucket>(_hours);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var partial = _path + ".partial";
            File.WriteAllText(partial, JsonSerializer.Serialize(copy, Json));
            File.Move(partial, _path, true);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not write the work log {Path}.", _path);
        }
    }

    internal sealed record Bucket
    {
        public int Jobs { get; init; }
        public double WorkUnits { get; init; }
    }
}
