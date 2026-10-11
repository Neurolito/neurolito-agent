using System.Text.Json.Serialization;

namespace Tharga.Neurolito.Client.Contract;

/// <summary>
/// The server's count of the queued jobs an agent may run, sent when it changes to agents that ask for it
/// (<see cref="Engine.SupportsQueueSummary"/>): those it has a model for, and those waiting for a model it lacks.
/// Counts and model names only, never a job's content.
/// </summary>
public record QueueSummaryMessage
{
    /// <summary>Jobs this agent may run and has a model for.</summary>
    public int Pickable { get; init; }

    /// <summary>Jobs this agent may run but has no model for.</summary>
    public int Unpickable { get; init; }

    /// <summary>What the <see cref="Unpickable"/> jobs ask for, the most asked first; the agent's own team's only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QueuedModelCount[] Missing { get; init; }
}

/// <summary>How many queued jobs ask for one model, family, pattern or alias.</summary>
public record QueuedModelCount(string Model, int Jobs);
