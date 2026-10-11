using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Tharga.Neurolito.Agent.Framework;

/// <summary>
/// Settings from a registry key, one value per setting, named as in configuration: <c>Engine:Kind</c>,
/// <c>Desktop:EnergyMode</c>. The Windows installer writes the agent's settings there - all but the team key, which
/// goes to a file only the service can read - because an installer can write a registry key without code of its own.
/// </summary>
/// <remarks>Read once at startup; a changed setting takes effect when the service restarts.</remarks>
[SupportedOSPlatform("windows")]
internal sealed class RegistryConfigurationSource(string key) : IConfigurationSource
{
    /// <summary>Where the Windows installer writes the agent's settings, under HKEY_LOCAL_MACHINE.</summary>
    public const string AgentKey = @"SOFTWARE\Neurolito\Agent";

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new Provider(key);

    private sealed class Provider(string key) : ConfigurationProvider
    {
        public override void Load()
        {
            var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var registry = Registry.LocalMachine.OpenSubKey(key);
                if (registry != null)
                {
                    foreach (var name in registry.GetValueNames().Where(x => !string.IsNullOrEmpty(x)))
                    {
                        if (registry.GetValue(name) is { } value) data[name] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Not readable by this account: the settings come from elsewhere.
            }

            Data = data;
        }
    }
}
