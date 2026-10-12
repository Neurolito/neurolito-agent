using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Desktop;

/// <summary>The machine's power plan: when it sleeps and when it turns the display off.</summary>
public interface IPowerPlan
{
    /// <summary>The active plan's timeouts, or null when they cannot be read.</summary>
    PowerTimeouts Read();

    /// <summary>Sets what is not null on the active plan, in seconds (0 is never). Null when done; otherwise why not.</summary>
    string Write(int? sleepAcSeconds, int? sleepDcSeconds, int? displayAcSeconds, int? displayDcSeconds);
}

/// <summary>
/// The active power plan, read and written through Windows' power API. Users may change the plan's timeouts without
/// elevation, and so may the service's own account.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPowerPlan : IPowerPlan
{
    private static readonly Guid SleepGroup = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid SleepIdle = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    private static readonly Guid VideoGroup = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid VideoIdle = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Power\PowerSettings";
    private const byte NoBattery = 128;
    private const byte UnknownBattery = 255;

    public PowerTimeouts Read()
    {
        if (!TryActiveScheme(out var scheme)) return null;

        var battery = HasBattery();
        return new PowerTimeouts
        {
            Plan = NameOf(scheme),
            SleepAcSeconds = ReadValue(scheme, SleepGroup, SleepIdle, ac: true) ?? 0,
            DisplayAcSeconds = ReadValue(scheme, VideoGroup, VideoIdle, ac: true) ?? 0,
            SleepDcSeconds = battery ? ReadValue(scheme, SleepGroup, SleepIdle, ac: false) : null,
            DisplayDcSeconds = battery ? ReadValue(scheme, VideoGroup, VideoIdle, ac: false) : null,
            ManagedByPolicy = IsPolicy(SleepIdle) || IsPolicy(VideoIdle)
        };
    }

    public string Write(int? sleepAcSeconds, int? sleepDcSeconds, int? displayAcSeconds, int? displayDcSeconds)
    {
        if (sleepAcSeconds == null && sleepDcSeconds == null && displayAcSeconds == null && displayDcSeconds == null) return null;
        if (!TryActiveScheme(out var scheme)) return "The active power plan could not be read.";

        var errors = new List<string>();
        Set(scheme, SleepGroup, SleepIdle, true, sleepAcSeconds, "sleep on mains power", errors);
        Set(scheme, SleepGroup, SleepIdle, false, sleepDcSeconds, "sleep on battery", errors);
        Set(scheme, VideoGroup, VideoIdle, true, displayAcSeconds, "display off on mains power", errors);
        Set(scheme, VideoGroup, VideoIdle, false, displayDcSeconds, "display off on battery", errors);

        //NOTE: The values are written to the plan; making it active again is what puts them into effect.
        var applied = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (applied != 0) errors.Add($"the plan could not be applied ({Describe(applied)})");

        return errors.Count == 0 ? null : $"The power plan was not fully changed: {string.Join("; ", errors)}.";
    }

    private static void Set(Guid scheme, Guid group, Guid setting, bool ac, int? seconds, string what, List<string> errors)
    {
        if (seconds is not { } value) return;
        var result = ac
            ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref group, ref setting, (uint)value)
            : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref group, ref setting, (uint)value);
        if (result != 0) errors.Add($"{what} ({Describe(result)})");
    }

    private static string Describe(uint code) => new Win32Exception((int)code).Message;

    private static bool TryActiveScheme(out Guid scheme)
    {
        scheme = Guid.Empty;
        if (PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0 || pointer == IntPtr.Zero) return false;
        try
        {
            scheme = Marshal.PtrToStructure<Guid>(pointer);
            return true;
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    private static int? ReadValue(Guid scheme, Guid group, Guid setting, bool ac)
    {
        var result = ac
            ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref group, ref setting, out var value)
            : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref group, ref setting, out value);
        return result == 0 ? (int)value : null;
    }

    private static string NameOf(Guid scheme)
    {
        uint size = 0;
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size) != 0 || size == 0) return null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            return PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0 ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool HasBattery() => GetSystemPowerStatus(out var status) && status.BatteryFlag != NoBattery && status.BatteryFlag != UnknownBattery;

    private static bool IsPolicy(Guid setting)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{PolicyKey}\{setting:D}");
            return key?.GetValue("ACSettingIndex") != null || key?.GetValue("DCSettingIndex") != null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr rootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupGuid, IntPtr settingGuid, IntPtr buffer, ref uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
