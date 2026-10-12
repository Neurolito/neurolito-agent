using System.Runtime.InteropServices;

namespace Tharga.Neurolito.Agent.Features.Desktop;

/// <param name="UserLoggedOn">Whether anyone is logged on at the machine's own screen and keyboard.</param>
/// <param name="Locked">Null when the platform would not say.</param>
public readonly record struct ConsoleState(bool UserLoggedOn, bool? Locked);

/// <summary>The machine's console session as the platform reports it, so presence can be decided the same way everywhere.</summary>
public interface IConsoleSession
{
    /// <summary>
    /// The console's state, or null when it cannot be read. A failed read is never taken for an empty console, since
    /// that would hand a machine someone is using to the queue.
    /// </summary>
    ConsoleState? Read();
}

/// <summary>The console session on Windows, read through <see cref="ConsoleSession"/>.</summary>
internal sealed class WindowsConsoleSession : IConsoleSession
{
    public ConsoleState? Read() => ConsoleSession.Read();
}

/// <summary>The machine's console session: is anyone logged on at the screen and keyboard, and is it locked.</summary>
/// <remarks>
/// Asked of Windows rather than learned from session events, so it is right from the moment the agent starts, and the
/// same whether the agent runs as a service in session 0 or in a console during development. A locked console counts
/// as nobody at the machine: Windows does not run the user's screensaver on the lock screen, so without this a locked
/// machine would never be idle.
/// </remarks>
internal static class ConsoleSession
{
    private const uint NoSession = 0xFFFFFFFF;
    private const int SessionInfoEx = 25;
    private const int UserName = 5;
    private const int DomainName = 7;
    private const int LockFlag = 0; // WTS_SESSIONSTATE_LOCK
    private const int UnlockFlag = 1; // WTS_SESSIONSTATE_UNLOCK

    /// <summary>
    /// The console's state, or null when it cannot be read: not Windows, or the account is not allowed to ask. A failed
    /// read is never taken for an empty console, since that would hand a machine someone is using to the queue.
    /// </summary>
    public static ConsoleState? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;

        var session = WTSGetActiveConsoleSessionId();
        if (session == NoSession) return new ConsoleState(false, false);

        if (!TryQueryString(session, UserName, out var user)) return null;
        if (string.IsNullOrEmpty(user)) return new ConsoleState(false, false);

        return LockState(session) switch
        {
            LockFlag => new ConsoleState(true, true),
            UnlockFlag => new ConsoleState(true, false),
            _ => new ConsoleState(true, null)
        };
    }

    /// <summary>The account logged on to a session, <c>DOMAIN\name</c>; null when nobody is, or Windows would not say.</summary>
    public static string UserOf(uint session)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!TryQueryString(session, UserName, out var user) || string.IsNullOrEmpty(user)) return null;
        return TryQueryString(session, DomainName, out var domain) && !string.IsNullOrEmpty(domain) ? $@"{domain}\{user}" : user;
    }

    private static int? LockState(uint session)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, SessionInfoEx, out var buffer, out _)) return null;
        try
        {
            //NOTE: WTSINFOEXW is Level (DWORD) and then WTSINFOEX_LEVEL1_W, which holds LARGE_INTEGERs and so starts at
            //offset 8: SessionId at 8, SessionState at 12, SessionFlags at 16. Windows 7 and Server 2008 R2 report the
            //flag inverted; neither is supported.
            var level = Marshal.ReadInt32(buffer);
            if (level != 1) return null;
            var flags = Marshal.ReadInt32(buffer, 16);
            return flags is LockFlag or UnlockFlag ? flags : null;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static bool TryQueryString(uint session, int infoClass, out string value)
    {
        value = null;
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, infoClass, out var buffer, out _)) return false;
        try
        {
            value = Marshal.PtrToStringUni(buffer);
            return true;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
