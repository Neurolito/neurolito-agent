using Tharga.Communication.MessageHandler;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Desktop;

/// <summary>
/// Makes the changes the server asks for to the desktop settings, and answers with the settings as they are after it.
/// An agent without the desktop has none of the services and ignores the request.
/// </summary>
internal sealed class ChangeDesktopSettingsRequestHandler : PostMessageHandlerBase<ChangeDesktopSettingsRequest>
{
    public const int MaxResumeDelaySeconds = 3600;
    public const int MinScreensaverSeconds = 60;

    private readonly ILogger<ChangeDesktopSettingsRequestHandler> _logger;
    private readonly IDesktopSettingsStore _store;
    private readonly IPowerPlan _power;
    private readonly IUserScreensavers _screensavers;
    private readonly IDesktopSettingsReporter _reporter;

    public ChangeDesktopSettingsRequestHandler(ILogger<ChangeDesktopSettingsRequestHandler> logger, IDesktopSettingsStore store = null, IPowerPlan power = null,
        IUserScreensavers screensavers = null, IDesktopSettingsReporter reporter = null)
    {
        _logger = logger;
        _store = store;
        _power = power;
        _screensavers = screensavers;
        _reporter = reporter;
    }

    public override async Task Handle(ChangeDesktopSettingsRequest message)
    {
        if (_store == null || _reporter == null)
        {
            _logger.LogWarning("The server asked to change desktop settings, but this agent does not run as a desktop agent.");
            return;
        }

        var errors = Apply(message, _store, _power, _screensavers);
        _logger.LogInformation("The server changed the desktop settings{Errors}.", errors.Count == 0 ? "" : $", in part: {string.Join(" ", errors)}");
        await _reporter.ReportAsync(errors.Count == 0 ? null : string.Join(" ", errors));
    }

    /// <summary>Makes each change that is valid; the rest are left as they are. What could not be done, in sentences.</summary>
    internal static List<string> Apply(ChangeDesktopSettingsRequest request, IDesktopSettingsStore store, IPowerPlan power, IUserScreensavers screensavers)
    {
        var errors = Validate(request);
        if (request == null) return errors;

        try
        {
            var delay = request.ResumeDelaySeconds is >= 0 and <= MaxResumeDelaySeconds ? request.ResumeDelaySeconds : null;
            var mode = request.EnergyMode is { } m ? Enum.Parse<EnergyMode>($"{m}") : (EnergyMode?)null;
            if (store.Change(mode, request.AlwaysWork, delay) is { } stored) errors.Add(stored);

            static int? Valid(int? seconds) => seconds is >= 0 ? seconds : null;
            var sleepAc = Valid(request.SleepAcSeconds);
            var sleepDc = Valid(request.SleepDcSeconds);
            var displayAc = Valid(request.DisplayAcSeconds);
            var displayDc = Valid(request.DisplayDcSeconds);
            if (sleepAc != null || sleepDc != null || displayAc != null || displayDc != null)
            {
                if (power == null) errors.Add("This machine's power plan cannot be changed by the agent.");
                else if (power.Write(sleepAc, sleepDc, displayAc, displayDc) is { } written) errors.Add(written);
            }

            foreach (var change in request.Screensavers ?? [])
            {
                var valid = change with { TimeoutSeconds = change.TimeoutSeconds is >= MinScreensaverSeconds ? change.TimeoutSeconds : null };
                if (screensavers == null) errors.Add("This agent cannot reach users' screensavers.");
                else if (screensavers.Change(valid) is { } refused) errors.Add(refused);
            }
        }
        catch (Exception e)
        {
            errors.Add($"The change failed: {e.Message}");
        }

        return errors;
    }

    /// <summary>What in the request is out of range, in sentences. Those parts are not made.</summary>
    internal static List<string> Validate(ChangeDesktopSettingsRequest request)
    {
        var errors = new List<string>();
        if (request == null) return ["The change was empty."];

        if (request.ResumeDelaySeconds is < 0 or > MaxResumeDelaySeconds) errors.Add($"The resume delay must be 0 to {MaxResumeDelaySeconds} seconds.");
        if (request.SleepAcSeconds < 0 || request.SleepDcSeconds < 0 || request.DisplayAcSeconds < 0 || request.DisplayDcSeconds < 0) errors.Add("A power timeout cannot be negative.");
        foreach (var change in request.Screensavers ?? [])
        {
            if (change.TimeoutSeconds < MinScreensaverSeconds) errors.Add($"The screensaver of {change.User} must wait at least {MinScreensaverSeconds} seconds.");
        }

        return errors;
    }
}
