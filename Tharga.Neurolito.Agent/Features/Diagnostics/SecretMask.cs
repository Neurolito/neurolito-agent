using System.Text.RegularExpressions;

namespace Tharga.Neurolito.Agent.Features.Diagnostics;

/// <summary>
/// Hides what must not leave the machine in text the server asks for: a value that follows a word like <c>key</c>,
/// <c>token</c> or <c>password</c>, any value the agent was configured with as a secret, and long unbroken tokens.
/// </summary>
internal static partial class SecretMask
{
    public const string Mask = "***";

    [GeneratedRegex(@"(?<name>\b(?:x-)?api[-_ ]?key|\btoken|\bsecret|\bpassword|\bauthorization|\bbearer)(?<separator>[""']?\s*[:=]\s*[""']?|\s+)(?<value>(?:(?:bearer|basic)\s+)?[^\s""',;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Named();

    [GeneratedRegex(@"[A-Za-z0-9_\-]{48,}")]
    private static partial Regex Long();

    public static string Apply(string text, params string[] secrets)
    {
        if (string.IsNullOrEmpty(text)) return text;

        foreach (var secret in secrets.Where(x => !string.IsNullOrWhiteSpace(x) && x.Length >= 4))
        {
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        }

        text = Named().Replace(text, x => $"{x.Groups["name"].Value}{x.Groups["separator"].Value}{Mask}");
        return Long().Replace(text, Mask);
    }
}
