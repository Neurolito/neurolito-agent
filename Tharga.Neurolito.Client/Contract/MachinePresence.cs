using System.Text.Json.Serialization;

namespace Tharga.Neurolito.Client.Contract;

/// <summary>Whether the machine an agent runs on can take work now, as the agent sees it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MachinePresence
{
    /// <summary>Nobody is using the machine, or the agent does not watch for that. The default.</summary>
    Available,

    /// <summary>Someone is using the machine. The agent finishes a running job and takes no new one.</summary>
    InUse,

    /// <summary>The machine is going to sleep. Sent just before the connection drops.</summary>
    Asleep
}

/// <summary>
/// Sent by an agent that watches its machine - a desktop agent with a screensaver - whenever its presence changes, and
/// once after it connects. An agent that never sends it is always <see cref="MachinePresence.Available"/>.
/// </summary>
public record MachinePresenceMessage
{
    public required MachinePresence Presence { get; init; }

    /// <summary>Why, in a few words for the agent card, for example "someone is using the machine".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Reason { get; init; }
}
