namespace Tharga.Neurolito.Agent.Features.Desktop;

/// <summary>
/// The <c>Desktop</c> section: an agent on someone's Windows desktop, which works while nobody uses the machine and
/// steps aside when someone does. Off by default; the Windows installer turns it on.
/// </summary>
public record DesktopOptions
{
    public const string SectionName = "Desktop";

    /// <summary>Watch the machine and report its presence; serve the screensaver's pipe.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Work whether or not someone is using the machine. The agent still reports, so the server shows it as a desktop
    /// agent, but it is never in use.
    /// </summary>
    public bool AlwaysWork { get; set; }

    /// <summary>
    /// How long the machine must stay unused before the agent takes work again. Pausing is immediate; resuming waits, so
    /// someone stepping away for a moment does not get a job loaded under them.
    /// </summary>
    public int ResumeDelaySeconds { get; set; } = 15;

    /// <summary>Whether the agent keeps the machine from going to sleep.</summary>
    public EnergyMode EnergyMode { get; set; } = EnergyMode.Never;

    /// <summary>The local pipe the screensaver reads, <c>\\.\pipe\{PipeName}</c>.</summary>
    public string PipeName { get; set; } = "neurolito-agent";

    /// <summary>
    /// Where the settings changed from the server are kept: a JSON file whose values win over every other source, so a
    /// change made on the web outlives a restart. Empty for <c>%ProgramData%\Neurolito\desktop.json</c>.
    /// </summary>
    public string SettingsPath { get; set; }
}

/// <summary>When the agent holds off the machine's sleep. The display may still turn off; only sleep is held off.</summary>
public enum EnergyMode
{
    /// <summary>Windows sleeps as it is set to. The default: installing the agent changes nothing unasked.</summary>
    Never,

    /// <summary>A running job finishes before the machine sleeps.</summary>
    WhileWorking,

    /// <summary>The machine stays awake for as long as it can take work - through the night, if nobody uses it.</summary>
    WhileReady
}
