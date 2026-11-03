namespace Tharga.Neurolito.Client.Contract;

public record QueueRequestV2
{
    public string Model { get; init; }
    public required Message[] Messages { get; init; }
    public Options Options { get; init; }
    public Dictionary<string, string> Tags { get; init; }
    public ResponseInstruction ResponseInstruction { get; init; }
    public int? MaxProcessTimeSeconds { get; init; }

    /// <summary>
    /// Whether this job may run on another team's agent. Null takes the team's default.
    /// </summary>
    /// <remarks>
    /// Nullable so "did not say" is distinct from "said Any". A caller that omits it gets its team's
    /// setting; one that sends <see cref="AgentReach.TeamOnly"/> overrides a team default of
    /// <see cref="AgentReach.Any"/> for this job alone, which is the point of having it per job.
    /// </remarks>
    public AgentReach? Reach { get; init; }

    /// <summary>
    /// Models to fall back on, in order of preference, when no machine takes <see cref="Model"/> in time. Each
    /// is what <see cref="Model"/> can be - a model, a family, a pattern or an alias - or a hosted model by its exact
    /// name, used only when the team has turned on hosted models.
    /// </summary>
    public string[] FallbackModels { get; init; }

    /// <summary>
    /// How long each entry has before the next may be used: the first fallback may take the job this long after it was
    /// queued, the second twice as long, and so on. 0 lets any entry take it at once. Null is 30 seconds.
    /// </summary>
    public int? FallbackAfterSeconds { get; init; }
}