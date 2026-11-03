using System.Text.Json.Serialization;

namespace Tharga.Neurolito.Client.Contract;

/// <summary>
/// Asks an agent to load a model into memory now, so a request expected soon does not wait for the load. Nothing is
/// generated. Only sent to agents whose <see cref="Engine.SupportsLoad"/> is set.
/// </summary>
public record LoadModelRequest
{
    /// <summary>The model to load: a name or a family, as a prompt names it.</summary>
    public required string Model { get; init; }

    /// <summary>How long the engine keeps it loaded when nothing uses it, in minutes, for an engine that decides that per request. Null: the engine's own.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? KeepAliveMinutes { get; init; }
}

/// <summary>What an agent did with <see cref="LoadModelRequest"/>.</summary>
public record LoadModelResponse
{
    /// <summary>The model it loaded, the request narrowed to an installed one.</summary>
    public string Model { get; init; }

    public required bool Success { get; init; }

    /// <summary>Whether the model was loaded already, so nothing had to be read from disk.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AlreadyLoaded { get; init; }

    /// <summary>How long the load took, as the engine measured it.</summary>
    public double LoadDurationMs { get; init; }

    /// <summary>Why it failed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Error { get; init; }
}
