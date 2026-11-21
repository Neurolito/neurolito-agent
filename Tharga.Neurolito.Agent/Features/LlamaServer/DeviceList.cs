using System.Text.RegularExpressions;

namespace Tharga.Neurolito.Agent.Features.LlamaServer;

/// <summary>One line of <c>llama-server --list-devices</c>: <c>Vulkan0: NVIDIA GeForce RTX 5070 (12227 MiB, 11000 MiB free)</c>.</summary>
internal sealed record ListedDevice(string Name, string Description, int? TotalMiB, int? FreeMiB);

/// <summary>Reads the device list llama-server prints, and finds the device a setting names.</summary>
internal static partial class DeviceList
{
    [GeneratedRegex(@"^\s*(?<name>[A-Za-z]+\d+):\s*(?<description>.*?)(?:\s*\((?<total>\d+)\s*MiB(?:,\s*(?<free>\d+)\s*MiB free)?\))?\s*$")]
    private static partial Regex Line();

    public static IReadOnlyList<ListedDevice> Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];

        return output.Split('\n')
            .Select(x => Line().Match(x.TrimEnd('\r')))
            .Where(x => x.Success)
            .Select(x => new ListedDevice(
                x.Groups["name"].Value,
                x.Groups["description"].Value.Trim(),
                x.Groups["total"].Success ? int.Parse(x.Groups["total"].Value) : null,
                x.Groups["free"].Success ? int.Parse(x.Groups["free"].Value) : null))
            .ToArray();
    }

    /// <summary>
    /// The name of the device <paramref name="setting"/> stands for: a device's own name, or a part of its description
    /// when that picks exactly one. Null when it matches none or several, so a setting is never guessed at.
    /// </summary>
    public static string Resolve(string setting, IReadOnlyList<ListedDevice> devices)
    {
        if (string.IsNullOrWhiteSpace(setting)) return null;

        var wanted = setting.Trim();
        var byName = devices.FirstOrDefault(x => string.Equals(x.Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (byName != null) return byName.Name;

        var byDescription = devices.Where(x => x.Description.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToArray();
        return byDescription.Length == 1 ? byDescription[0].Name : null;
    }
}
