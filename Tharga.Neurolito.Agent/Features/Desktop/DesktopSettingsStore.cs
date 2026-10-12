using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tharga.Neurolito.Agent.Features.Desktop;

/// <summary>
/// The agent's own desktop settings - energy mode, always work, resume delay - as they are now, changeable while the
/// agent runs.
/// </summary>
/// <remarks>
/// The installer writes these to the registry, which the service may read but not write. A change from the server is
/// kept in a file of its own instead (<see cref="DesktopOptions.SettingsPath"/>), and what that file holds wins over
/// every other source at the next start, so the change outlives a restart.
/// </remarks>
public interface IDesktopSettingsStore
{
    /// <summary>Raised after a change has been applied.</summary>
    event EventHandler Changed;

    EnergyMode EnergyMode { get; }
    bool AlwaysWork { get; }
    int ResumeDelaySeconds { get; }

    /// <summary>
    /// Applies what is not null at once and keeps it in the settings file. Null when it was kept; otherwise why it could
    /// not be - the change still applies until the agent restarts.
    /// </summary>
    string Change(EnergyMode? energyMode, bool? alwaysWork, int? resumeDelaySeconds);
}

internal sealed class DesktopSettingsStore : IDesktopSettingsStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly DesktopOptions _options;
    private readonly string _path;
    private readonly Lock _lock = new();

    public DesktopSettingsStore(DesktopOptions options)
    {
        _options = options;
        _path = PathOf(options);
    }

    public event EventHandler Changed;

    public EnergyMode EnergyMode => _options.EnergyMode;
    public bool AlwaysWork => _options.AlwaysWork;
    public int ResumeDelaySeconds => _options.ResumeDelaySeconds;

    /// <summary>The settings file: <see cref="DesktopOptions.SettingsPath"/>, or <c>desktop.json</c> in the data folder.</summary>
    public static string PathOf(DesktopOptions options) => string.IsNullOrWhiteSpace(options.SettingsPath)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Neurolito", "desktop.json")
        : options.SettingsPath;

    /// <summary>
    /// Puts what the settings file holds over the options read from configuration. A missing or unreadable file changes
    /// nothing: the installer's settings stand.
    /// </summary>
    public static void ApplySaved(DesktopOptions options)
    {
        var saved = Read(PathOf(options));
        if (saved == null) return;

        if (saved.EnergyMode is { } mode) options.EnergyMode = mode;
        if (saved.AlwaysWork is { } always) options.AlwaysWork = always;
        if (saved.ResumeDelaySeconds is { } delay) options.ResumeDelaySeconds = delay;
    }

    public string Change(EnergyMode? energyMode, bool? alwaysWork, int? resumeDelaySeconds)
    {
        if (energyMode == null && alwaysWork == null && resumeDelaySeconds == null) return null;

        string error = null;
        lock (_lock)
        {
            if (energyMode is { } mode) _options.EnergyMode = mode;
            if (alwaysWork is { } always) _options.AlwaysWork = always;
            if (resumeDelaySeconds is { } delay) _options.ResumeDelaySeconds = delay;

            //NOTE: Only what the server has set goes in the file, so a setting it never touched still follows the installer.
            var saved = Read(_path) ?? new Saved();
            saved = saved with
            {
                EnergyMode = energyMode ?? saved.EnergyMode,
                AlwaysWork = alwaysWork ?? saved.AlwaysWork,
                ResumeDelaySeconds = resumeDelaySeconds ?? saved.ResumeDelaySeconds
            };

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var partial = _path + ".partial";
                File.WriteAllText(partial, JsonSerializer.Serialize(saved, Json));
                File.Move(partial, _path, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error = $"The change applies until the agent restarts, but could not be kept in {_path}: {e.Message}";
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return error;
    }

    private static Saved Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal sealed record Saved
    {
        public EnergyMode? EnergyMode { get; init; }
        public bool? AlwaysWork { get; init; }
        public int? ResumeDelaySeconds { get; init; }
    }
}
