using System.Text.Json.Serialization;

namespace Tharga.Neurolito.Client.Contract;

/// <summary>
/// What a "get ready" call did: which machine loads the model a request is expected for soon, or why none does.
/// </summary>
public record GetReadyResponse
{
    /// <summary>A machine is loading the model, or has it loaded already.</summary>
    public bool Success { get; init; }

    /// <summary>Why not, when <see cref="Success"/> is false: the same codes a prompt is refused with.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Code { get; init; }

    public string Message { get; init; }

    /// <summary>The machine the request would most likely land on.</summary>
    public string Machine { get; init; }

    /// <summary>The model it loads, the request narrowed to one of its own.</summary>
    public string Model { get; init; }

    /// <summary>The machine had it loaded already, so nothing was sent.</summary>
    public bool AlreadyLoaded { get; init; }

    /// <summary>How long a cold start on that machine is expected to take for a short prompt, from measured speeds; null until measured.</summary>
    public double? ExpectedSeconds { get; init; }
}
