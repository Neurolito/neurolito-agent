using System.Text.Json;
using System.Text.Json.Serialization;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Desktop;

/// <summary>
/// The screensaver of each user logged on, as a small helper in that user's session reports it over the pipe, and the
/// way to change one.
/// </summary>
/// <remarks>
/// A service cannot read or write another account's screensaver: it lives in that user's own registry hive, and only a
/// program running as that user can change it so that Windows notices. So each session runs a helper, which says what
/// its user's screensaver is and makes the changes the server asks for. One report per user; the latest helper wins.
/// </remarks>
public interface IUserScreensavers
{
    /// <summary>Raised when a report arrives that differs from the last, or a helper goes away.</summary>
    event EventHandler Changed;

    /// <summary>The users whose helper is connected, by name.</summary>
    IReadOnlyList<UserScreensaver> Current { get; }

    /// <summary>A helper reported its user's screensaver. <paramref name="send"/> writes a line to that helper.</summary>
    void Report(Guid client, UserScreensaver report, Action<string> send);

    /// <summary>A helper's connection ended.</summary>
    void Disconnected(Guid client);

    /// <summary>Asks the user's helper to make the change. Null when it was sent; otherwise why it was not.</summary>
    string Change(ScreensaverChange change);
}

internal sealed class UserScreensavers : IUserScreensavers
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Helper> _helpers = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler Changed;

    public IReadOnlyList<UserScreensaver> Current
    {
        get
        {
            lock (_lock) return _helpers.Values.Select(x => x.Report).OrderBy(x => x.User, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public void Report(Guid client, UserScreensaver report, Action<string> send)
    {
        if (string.IsNullOrEmpty(report?.User)) return;

        bool changed;
        lock (_lock)
        {
            changed = !_helpers.TryGetValue(report.User, out var before) || before.Report != report;
            _helpers[report.User] = new Helper(client, report, send);
        }

        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Disconnected(Guid client)
    {
        bool removed;
        lock (_lock)
        {
            var users = _helpers.Where(x => x.Value.Client == client).Select(x => x.Key).ToArray();
            foreach (var user in users) _helpers.Remove(user);
            removed = users.Length > 0;
        }

        if (removed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public string Change(ScreensaverChange change)
    {
        if (change == null || string.IsNullOrEmpty(change.User)) return "A screensaver change names no user.";
        if (change.Active == null && change.TimeoutSeconds == null) return null;

        Helper helper;
        lock (_lock) _helpers.TryGetValue(change.User, out helper);

        if (helper == null) return $"{change.User} is not logged on, or the session does not report its screensaver.";
        if (!helper.Report.Neurolito) return $"The screensaver of {change.User} is not the Neurolito one, so it is left as it is.";

        helper.Send(SetLine(change));
        return null;
    }

    /// <summary>The line that asks a helper to change its user's screensaver.</summary>
    internal static string SetLine(ScreensaverChange change) => JsonSerializer.Serialize(new SetScreensaver { Active = change.Active, TimeoutSeconds = change.TimeoutSeconds }, Json);

    /// <summary>
    /// A helper's report, from its line: <c>{"type":"screensaver","neurolito":true,"active":true,"timeoutSeconds":600,
    /// "managed":false}</c>. Null when the line is not one.
    /// </summary>
    internal static UserScreensaver Parse(string line, string user)
    {
        try
        {
            var report = JsonSerializer.Deserialize<HelperReport>(line, Json);
            if (report?.Type != "screensaver" || string.IsNullOrEmpty(user)) return null;

            return new UserScreensaver
            {
                User = user,
                Neurolito = report.Neurolito,
                Active = report.Active,
                TimeoutSeconds = report.TimeoutSeconds,
                ManagedByPolicy = report.Managed
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Helper(Guid Client, UserScreensaver Report, Action<string> Send);

    private sealed record SetScreensaver
    {
        public string Type => "set-screensaver";
        public bool? Active { get; init; }
        public int? TimeoutSeconds { get; init; }
    }

    private sealed record HelperReport
    {
        public string Type { get; init; }
        public bool Neurolito { get; init; }
        public bool Active { get; init; }
        public int TimeoutSeconds { get; init; }
        public bool Managed { get; init; }
        public string Error { get; init; }
    }
}
