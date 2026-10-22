// Frees a process's physical memory for another one (PROTOCOL §24): EmptyWorkingSet moves the pages of its working set to the
// standby list (or the page file), from where the process faults them back when it touches them again. Nothing is lost and no
// limit is set: CK3, paused behind Bannerlord, simply gives its RAM to the battle and takes it back over its first seconds after it.
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CK3xBL.Core.Native;

public static class ProcessMemory
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, PROCESS_SET_QUOTA = 0x0100;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(nint process);

    /// <summary>
    /// Trims the working set of <paramref name="pid"/>. Returns the working set before and after in MB, or an error text; never throws.
    /// </summary>
    public static (long BeforeMb, long AfterMb, string? Error) Trim(int pid)
    {
        if (pid <= 0) return (0, 0, "no process");
        long before = WorkingSetMb(pid);
        nint h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, false, (uint)pid);
        if (h == 0) return (before, before, $"OpenProcess failed ({Marshal.GetLastWin32Error()})");
        try
        {
            if (!EmptyWorkingSet(h)) return (before, before, $"EmptyWorkingSet failed ({Marshal.GetLastWin32Error()})");
        }
        finally
        {
            CloseHandle(h);
        }
        return (before, WorkingSetMb(pid), null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    /// <summary>Free physical RAM in MB, or null when it cannot be read.</summary>
    public static long? FreeRamMb()
    {
        var s = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref s) ? (long)(s.AvailPhys / (1024 * 1024)) : null;
    }

    /// <summary>The memory Windows can still promise to programs (commit limit minus commit charge: RAM plus page file) in MB, or
    /// null when it cannot be read. When it runs out, an allocation fails and Bannerlord crashes (PROTOCOL §31).</summary>
    public static long? FreeCommitMb()
    {
        var s = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref s)) return null;
        long free = (long)(s.AvailPageFile / (1024 * 1024));
        // A page file Windows manages (or one with room up to its maximum) grows when commit runs short: live 2026-10-18 a battle of
        // 1000 ran fine with 1.1 GB free before the growth, so that room counts as free.
        try { free += PageFileGrowthMb((long)(s.TotalPhys / (1024 * 1024)), PagingFilesSetting()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return free;
    }

    /// <summary>How many MB the page files may still grow: a system-managed file (sizes "0 0", or "?:" for every drive) up to
    /// max(3 × RAM, 4 GB), a sized one up to its maximum, each within its drive's free space (2 GB kept free).</summary>
    public static long PageFileGrowthMb(long ramMb, IEnumerable<string> pagingFiles, Func<string, long?>? fileMb = null, Func<string, long?>? driveFreeMb = null)
    {
        fileMb ??= path => File.Exists(path) ? new FileInfo(path).Length / (1024 * 1024) : 0;
        driveFreeMb ??= root => new DriveInfo(root).AvailableFreeSpace / (1024 * 1024);
        long growth = 0;
        foreach (string entry in pagingFiles)
        {
            string[] parts = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0].Length < 3 || parts[0][0] == '?') continue;   // "?:" (automatic on all drives): unknown drive
            long max = parts.Length >= 3 && long.TryParse(parts[2], out long m) && m > 0 ? m : Math.Max(3 * ramMb, 4096);
            string path = parts[0], root = path[..3];
            long current = fileMb(path) ?? 0, room = (driveFreeMb(root) ?? 0) - 2048;
            growth += Math.Max(0, Math.Min(max - current, room));
        }
        return growth;
    }

    private static string[] PagingFilesSetting()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
        return key?.GetValue("PagingFiles") as string[] ?? Array.Empty<string>();
    }

    /// <summary>The memory <paramref name="pid"/> has committed (private bytes) in MB; 0 when it cannot be read.</summary>
    public static long CommittedMb(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.PrivateMemorySize64 / (1024 * 1024);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    public static long WorkingSetMb(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.WorkingSet64 / (1024 * 1024);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }
}
