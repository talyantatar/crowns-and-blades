// Win32 calls the .NET Framework does not wrap: console attach for the GUI exe, link removal, link targets, process image paths,
// the token's elevation type and the user of the Windows session.
using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CK3xBL.Setup.Util;

internal static class Native
{
    public const int ErrorCancelled = 1223;   // ERROR_CANCELLED: the user declined the UAC prompt

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachConsole(int processId);

    public const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetStdHandle(int handle);

    public const int StdOutputHandle = -11;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveDirectoryW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteFileW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags,
        IntPtr template);

    public const uint FileShareAll = 0x1 | 0x2 | 0x4;
    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    public const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    /// <summary>The executable of a running process, or null when it cannot be queried (exited, protected).</summary>
    public static string? ProcessImagePath(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returnLength);

    private const int TokenElevationTypeClass = 18;   // TOKEN_INFORMATION_CLASS.TokenElevationType
    private const int TokenElevationTypeFull = 2;     // a split (UAC) token that was elevated: "Run as administrator"

    /// <summary>True when this process runs with the elevated half of a UAC split token ("Run as administrator", or an
    /// administrator's password typed into the UAC prompt). False without UAC, where nothing is elevated on purpose.</summary>
    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return GetTokenInformation(id.Token, TokenElevationTypeClass, out int type, sizeof(int), out _) && type == TokenElevationTypeFull;
        }
        catch (System.Security.SecurityException) { return false; }
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    private const int WtsCurrentSession = -1;
    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;

    private static string? SessionString(int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, WtsCurrentSession, infoClass, out var buffer, out _)) return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { WTSFreeMemory(buffer); }
    }

    /// <summary>False when this process runs as another account than the one signed in to this Windows session (an
    /// administrator's password typed into the UAC prompt, or "Run as different user"). True when they match or it is unknown.</summary>
    public static bool RunsAsSessionUser()
    {
        try
        {
            var user = SessionString(WtsUserName);
            if (string.IsNullOrEmpty(user)) return true;
            var domain = SessionString(WtsDomainName) ?? "";
            using var id = WindowsIdentity.GetCurrent();
            try
            {
                var sid = (SecurityIdentifier)new NTAccount(domain, user).Translate(typeof(SecurityIdentifier));
                return id.User is null || sid.Equals(id.User);
            }
            // Translate throws a plain SystemException for a Win32 failure (an offline domain controller, for example).
            catch (Exception e) when (e is IdentityNotMappedException or ArgumentException || e.GetType() == typeof(SystemException))
            {
                var name = domain.Length > 0 ? domain + "\\" + user : user;
                return string.Equals(id.Name, name, StringComparison.OrdinalIgnoreCase) ||
                       id.Name.EndsWith("\\" + user, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or System.Security.SecurityException) { return true; }
    }

    /// <summary>Where a junction or symbolic link points (the final path of the opened folder), or null.</summary>
    public static string? FinalPath(string path)
    {
        using var h = CreateFileW(path, 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var sb = new StringBuilder(1024);
        uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
        if (n == 0 || n >= sb.Capacity) return null;
        var s = sb.ToString(0, (int)n);
        if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + s.Substring(8);
        return s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s.Substring(4) : s;
    }
}
