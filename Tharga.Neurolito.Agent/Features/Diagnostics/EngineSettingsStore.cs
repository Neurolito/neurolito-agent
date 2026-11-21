using System.Text.Json;
using System.Text.Json.Serialization;
using Tharga.Neurolito.Agent.Features.LlamaServer;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Diagnostics;

/// <summary>The engine settings the server may change, and nothing beyond them.</summary>
public interface IEngineSettingsStore
{
    Task<ChangeEngineSettingsResponse> ChangeAsync(ChangeEngineSettingsRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Changes which device llama-server uses and how many layers it puts there, while the agent runs, and keeps the change in
/// <c>engine.json</c>, which wins over every other source at the next start. The service may read the registry the
/// installer writes but not change it, so a change from the server goes in a file of its own, as the desktop's does.
/// </summary>
/// <remarks>
/// Free-form arguments cannot be set this way: llama-server has flags that read and write files, and the server is not
/// trusted with them. A device must be one llama-server lists, so a typo does not stop every job from loading.
/// </remarks>
internal sealed class EngineSettingsStore : IEngineSettingsStore
{
    public const int MaxGpuLayers = 999;
    public const int AutomaticLayers = -1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly LlamaServerOptions _options;
    private readonly IEngineControl _host;
    private readonly string _path;
    private readonly ILogger<EngineSettingsStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EngineSettingsStore(LlamaServerOptions options, IEngineControl host, string path, ILogger<EngineSettingsStore> logger)
    {
        _options = options;
        _host = host;
        _path = path;
        _logger = logger;
    }

    /// <summary>The settings file: <c>engine.json</c> in the data folder.</summary>
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Neurolito", "engine.json");

    /// <summary>Puts what the settings file holds over the options read from configuration. A missing or unreadable file changes nothing.</summary>
    public static void ApplySaved(LlamaServerOptions options, string path)
    {
        var saved = Read(path);
        if (saved == null) return;

        if (saved.Device != null) options.Device = saved.Device.Length == 0 ? null : saved.Device;
        if (saved.GpuLayers is { } layers) options.GpuLayers = layers == AutomaticLayers ? null : layers;
    }

    public async Task<ChangeEngineSettingsResponse> ChangeAsync(ChangeEngineSettingsRequest request, CancellationToken cancellationToken)
    {
        if (request == null || (request.Device == null && request.GpuLayers == null)) return Reply(EngineSettingsStatus.Unchanged, "Nothing was asked for.");

        if (request.GpuLayers is { } layers && layers != AutomaticLayers && (layers < 0 || layers > MaxGpuLayers))
            return Reply(EngineSettingsStatus.Refused, $"GPU layers is -1 (automatic) or 0 to {MaxGpuLayers}, not {layers}.");

        var device = request.Device?.Trim();
        if (!string.IsNullOrEmpty(device))
        {
            var listed = DeviceList.Parse(await _host.ListDevicesAsync(cancellationToken));
            if (listed.Count == 0) return Reply(EngineSettingsStatus.Refused, "llama-server listed no devices, so the device cannot be checked. Nothing was changed.");
            if (DeviceList.Resolve(device, listed) == null)
                return Reply(EngineSettingsStatus.Refused, $"'{device}' is not exactly one of the devices llama-server lists: {string.Join("; ", listed.Select(x => $"{x.Name} = {x.Description}"))}. Nothing was changed.");
        }

        string kept = null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (device != null) _options.Device = device.Length == 0 ? null : device;
            if (request.GpuLayers is { } l) _options.GpuLayers = l == AutomaticLayers ? null : l;

            var current = Read(_path) ?? new Saved();
            var saved = current with { Device = device ?? current.Device, GpuLayers = request.GpuLayers ?? current.GpuLayers };

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var partial = _path + ".partial";
                await File.WriteAllTextAsync(partial, JsonSerializer.Serialize(saved, Json), cancellationToken);
                File.Move(partial, _path, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                kept = $"It applies until the agent restarts, but could not be kept in {_path}: {e.Message}";
            }
        }
        finally
        {
            _gate.Release();
        }

        _logger.LogInformation("The server changed the engine settings: device {Device}, GPU layers {Layers}.", _options.Device ?? "automatic", _options.GpuLayers?.ToString() ?? "automatic");
        await _host.UnloadAsync(null, cancellationToken);

        return Reply(EngineSettingsStatus.Changed, $"The loaded model was unloaded; the next job loads it with the new settings.{(kept == null ? "" : " " + kept)}");
    }

    private ChangeEngineSettingsResponse Reply(string status, string message) => new()
    {
        Status = status,
        Device = _options.Device,
        GpuLayers = _options.GpuLayers,
        Message = message
    };

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
        public string Device { get; init; }
        public int? GpuLayers { get; init; }
    }
}
