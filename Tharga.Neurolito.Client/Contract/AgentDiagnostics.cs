using System.Text.Json.Serialization;

namespace Tharga.Neurolito.Client.Contract;

/// <summary>
/// Asks an agent what it sees on its machine: one probe from a closed list (<see cref="DiagnosticsProbes"/>), never a
/// command. The agent answers with an <see cref="AgentDiagnosticsResponse"/>. An agent that predates it does not answer.
/// </summary>
public record AgentDiagnosticsRequest
{
    /// <summary>One of <see cref="DiagnosticsProbes"/>, in any case.</summary>
    public required string Probe { get; init; }

    /// <summary>How many log lines <see cref="DiagnosticsProbes.Log"/> returns at most. The agent caps it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Lines { get; init; }
}

/// <summary>What a probe found.</summary>
public record AgentDiagnosticsResponse
{
    /// <summary>One of <see cref="DiagnosticsStatus"/>.</summary>
    public required string Status { get; init; }

    /// <summary>The probe that was run, as the agent knows it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Probe { get; init; }

    /// <summary>What the probe found, as plain text. Secrets are masked by the agent before they leave it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Text { get; init; }

    /// <summary>In a sentence: why there is no <see cref="Text"/>, or what the reader should know about it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Message { get; init; }
}

/// <summary>The probes an agent runs. Anything else is refused.</summary>
public static class DiagnosticsProbes
{
    /// <summary>The newest lines of the agent's log file.</summary>
    public const string Log = "Log";

    /// <summary>The graphics devices llama-server sees (<c>llama-server --list-devices</c>) and the device the agent is set to use.</summary>
    public const string Devices = "Devices";

    /// <summary>NVIDIA's own view of the cards: name, driver, memory in use and load. Unavailable without <c>nvidia-smi</c>.</summary>
    public const string Gpu = "Gpu";

    /// <summary>
    /// The engine's settings in effect, how llama-server was last started, its last output, and what it has loaded now.
    /// </summary>
    public const string Engine = "Engine";

    /// <summary>The agent's version, its engine's version and the machine's operating system.</summary>
    public const string Version = "Version";

    /// <summary>Every probe, for a caller that lists them.</summary>
    public static readonly IReadOnlyList<string> All = [Log, Devices, Gpu, Engine, Version];
}

/// <summary>The values of <see cref="AgentDiagnosticsResponse.Status"/>.</summary>
public static class DiagnosticsStatus
{
    /// <summary>The probe ran; <see cref="AgentDiagnosticsResponse.Text"/> holds what it found.</summary>
    public const string Ok = "Ok";

    /// <summary>The agent has no such probe.</summary>
    public const string UnknownProbe = "UnknownProbe";

    /// <summary>This agent cannot run the probe: another engine, another operating system, or the tool is missing.</summary>
    public const string Unavailable = "Unavailable";

    /// <summary>The probe failed; <see cref="AgentDiagnosticsResponse.Message"/> says how.</summary>
    public const string Failed = "Failed";
}

/// <summary>
/// Changes the engine settings an agent lets its server change, and nothing else: which graphics device llama-server uses
/// and how many layers it puts there. A value left null stays as it is. The agent answers with a
/// <see cref="ChangeEngineSettingsResponse"/>.
/// </summary>
public record ChangeEngineSettingsRequest
{
    /// <summary>
    /// The device to use, as <see cref="DiagnosticsProbes.Devices"/> lists it: its name (<c>Vulkan0</c>) or a part of its
    /// description (<c>RTX 5070</c>), which keeps working when the numbering changes. An empty string goes back to
    /// llama-server's own choice. The agent refuses a device it does not list.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Device { get; init; }

    /// <summary>
    /// Layers on the device, 0 to 999. -1 goes back to llama-server's own fitting (<c>--fit</c>), which is what the agent
    /// does by default.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? GpuLayers { get; init; }
}

/// <summary>The engine settings in effect after a <see cref="ChangeEngineSettingsRequest"/>, and whether it was kept.</summary>
public record ChangeEngineSettingsResponse
{
    /// <summary>One of <see cref="EngineSettingsStatus"/>.</summary>
    public required string Status { get; init; }

    /// <summary>The device in effect, or null for llama-server's own choice.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Device { get; init; }

    /// <summary>The layers in effect, or null for llama-server's own fitting.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? GpuLayers { get; init; }

    /// <summary>In a sentence: what was refused and why, what applies only until a restart, or what happens next.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Message { get; init; }
}

/// <summary>The values of <see cref="ChangeEngineSettingsResponse.Status"/>.</summary>
public static class EngineSettingsStatus
{
    /// <summary>The change was made and kept. The loaded model is unloaded, so the next job loads it with the new settings.</summary>
    public const string Changed = "Changed";

    /// <summary>Nothing was asked for, so nothing changed.</summary>
    public const string Unchanged = "Unchanged";

    /// <summary>The agent refused the change; <see cref="ChangeEngineSettingsResponse.Message"/> says why.</summary>
    public const string Refused = "Refused";

    /// <summary>This agent has no llama-server engine to change.</summary>
    public const string Unavailable = "Unavailable";
}
