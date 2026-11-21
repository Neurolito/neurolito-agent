using System.Text.Json.Serialization;

namespace Tharga.Neurolito.Client.Contract;

/// <summary>
/// Asks an agent to look for a new version now instead of at its next check, and optionally to update. The agent answers
/// with a <see cref="CheckForUpdateResponse"/>. An agent that predates it does not answer; the server says so.
/// </summary>
public record CheckForUpdateRequest
{
    /// <summary>
    /// Update when a newer version is found: the agent downloads it, takes no new job, lets the running job and model
    /// installs finish, and restarts on the new version. The answer comes at once, since the agent then exits.
    /// </summary>
    public bool Apply { get; init; }
}

/// <summary>What the agent found, and what it is doing about it.</summary>
public record CheckForUpdateResponse
{
    /// <summary>One of <see cref="UpdateStatus"/>.</summary>
    public required string Status { get; init; }

    /// <summary>The version the agent runs.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Current { get; init; }

    /// <summary>The newest version available; null when the agent is up to date or could not look.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Latest { get; init; }

    /// <summary>In a sentence: why it cannot update, what failed, or what it is doing.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Message { get; init; }
}

/// <summary>The values of <see cref="CheckForUpdateResponse.Status"/>.</summary>
public static class UpdateStatus
{
    /// <summary>The agent runs the newest version.</summary>
    public const string UpToDate = "UpToDate";

    /// <summary>A newer version is out; the agent was only asked to look.</summary>
    public const string Available = "Available";

    /// <summary>The agent is downloading, waiting for its running job, or applying the update.</summary>
    public const string Updating = "Updating";

    /// <summary>Not installed by the Windows installer (a container, a developer run), so it cannot update itself.</summary>
    public const string NotInstalled = "NotInstalled";

    /// <summary>Updates are off for this agent, or it has no update source.</summary>
    public const string Disabled = "Disabled";

    /// <summary>The look or the update failed; <see cref="CheckForUpdateResponse.Message"/> says how.</summary>
    public const string Failed = "Failed";
}
