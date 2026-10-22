// "Start with Windows" (tray menu): the per-user Run key, HKCU\Software\Microsoft\Windows\CurrentVersion\Run\CK3xBL. Off unless the
// player turns it on; it names the running executable, so a source checkout registers its own build.
using Microsoft.Win32;

namespace CK3xBL;

internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CK3xBL";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string { Length: > 0 };
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("The path of CK3xBL.exe is unknown.");
            key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
        }
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
