using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tharga.Neurolito.Agent.Features.Desktop;

/// <summary>
/// Holds off the machine's sleep through a Windows power request, which works from a service and shows, with its
/// reason, in <c>powercfg /requests</c>. It holds off the idle timeout only: a sleep the user asks for - the Start
/// menu, a closed lid - still happens. The display may still turn off.
/// </summary>
internal sealed class SleepBlocker : IDisposable
{
    private const int SystemRequired = 1; // PowerRequestSystemRequired
    private const uint SimpleString = 0x1; // POWER_REQUEST_CONTEXT_SIMPLE_STRING
    private const uint ContextVersion = 0; // POWER_REQUEST_CONTEXT_VERSION

    private readonly Lock _lock = new();
    private SafeFileHandle _request;
    private bool _held;

    public bool Held
    {
        get { lock (_lock) return _held; }
    }

    /// <summary>Holds or releases. False when Windows refused, or not on Windows.</summary>
    public bool Set(bool hold, string reason)
    {
        if (!OperatingSystem.IsWindows()) return false;

        lock (_lock)
        {
            if (hold == _held) return true;

            if (hold)
            {
                _request ??= Create(reason);
                if (_request is not { IsInvalid: false } || !PowerSetRequest(_request, SystemRequired)) return false;
            }
            else if (_request is { IsInvalid: false } && !PowerClearRequest(_request, SystemRequired))
            {
                return false;
            }

            _held = hold;
            return true;
        }
    }

    private static SafeFileHandle Create(string reason)
    {
        var context = new ReasonContext { Version = ContextVersion, Flags = SimpleString, Reason = reason };
        return PowerCreateRequest(ref context);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_held && _request is { IsInvalid: false }) PowerClearRequest(_request, SystemRequired);
            _held = false;
            _request?.Dispose();
            _request = null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string Reason;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(SafeFileHandle request, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(SafeFileHandle request, int requestType);
}
