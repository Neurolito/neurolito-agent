using System.Text.Json.Serialization;

namespace Tharga.Neurolito.Client.Contract;

/// <summary>When a desktop agent holds off its machine's sleep. The display may still turn off; only sleep is held off.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DesktopEnergyMode
{
    /// <summary>Windows sleeps as it is set to.</summary>
    Never,

    /// <summary>A running job finishes before the machine sleeps.</summary>
    WhileWorking,

    /// <summary>The machine stays awake for as long as it can take work.</summary>
    WhileReady
}

/// <summary>
/// Sent by a desktop agent once after it connects, whenever one of its settings changes, and in answer to a
/// <see cref="ChangeDesktopSettingsRequest"/>. An agent that never sends it has no desktop settings to show.
/// </summary>
public record DesktopSettingsMessage
{
    public required DesktopSettings Settings { get; init; }
}

/// <summary>
/// What decides when a desktop agent works: its own settings, the machine's power plan, and the screensaver of each
/// user logged on.
/// </summary>
public record DesktopSettings
{
    public DesktopEnergyMode EnergyMode { get; init; }

    /// <summary>Work whether or not someone is using the machine.</summary>
    public bool AlwaysWork { get; init; }

    /// <summary>How long the machine must stay unused before the agent takes work again.</summary>
    public int ResumeDelaySeconds { get; init; }

    /// <summary>The machine's power plan; null when it could not be read.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PowerTimeouts Power { get; init; }

    /// <summary>One for each user logged on whose session reports its screensaver.</summary>
    public UserScreensaver[] Screensavers { get; init; } = [];

    /// <summary>Why the last change asked for could not be made, in a sentence; null when it was.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string LastError { get; init; }
}

/// <summary>The active power plan's timeouts, in seconds; 0 is never.</summary>
public record PowerTimeouts
{
    /// <summary>The active plan's name, for example "Balanced".</summary>
    public string Plan { get; init; }

    public int SleepAcSeconds { get; init; }
    public int DisplayAcSeconds { get; init; }

    /// <summary>On battery; null on a machine without one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SleepDcSeconds { get; init; }

    /// <summary>On battery; null on a machine without one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DisplayDcSeconds { get; init; }

    /// <summary>Group Policy sets these timeouts, so a change to the plan does not take effect.</summary>
    public bool ManagedByPolicy { get; init; }
}

/// <summary>One user's screensaver, as their own session reads it.</summary>
public record UserScreensaver
{
    /// <summary>The account, <c>DOMAIN\name</c>.</summary>
    public required string User { get; init; }

    /// <summary>Whether the user's screensaver is the Neurolito one. Only then may it be changed from the server.</summary>
    public bool Neurolito { get; init; }

    /// <summary>Whether the screensaver is on.</summary>
    public bool Active { get; init; }

    /// <summary>How long the machine is idle before the screensaver starts.</summary>
    public int TimeoutSeconds { get; init; }

    /// <summary>Group Policy sets the screensaver for this user, so a change does not take effect.</summary>
    public bool ManagedByPolicy { get; init; }
}

/// <summary>
/// Sent to a desktop agent to change its settings. A null field is left as it is. The agent answers with a
/// <see cref="DesktopSettingsMessage"/>, whose <see cref="DesktopSettings.LastError"/> says what it could not do.
/// </summary>
public record ChangeDesktopSettingsRequest
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DesktopEnergyMode? EnergyMode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AlwaysWork { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ResumeDelaySeconds { get; init; }

    /// <summary>Seconds; 0 is never.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SleepAcSeconds { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SleepDcSeconds { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DisplayAcSeconds { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DisplayDcSeconds { get; init; }

    /// <summary>Changes to users' screensavers; each is made only where that user's screensaver is the Neurolito one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScreensaverChange[] Screensavers { get; init; }
}

/// <summary>A change to one user's screensaver. A null field is left as it is.</summary>
public record ScreensaverChange
{
    /// <summary>The account, as <see cref="UserScreensaver.User"/> named it.</summary>
    public required string User { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Active { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TimeoutSeconds { get; init; }
}
