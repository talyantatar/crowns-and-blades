// Shortcuts (IShellLinkW) and the Add/Remove Programs entry.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace CK3xBL.Setup;

internal static class Shell
{
    public static void CreateShortcut(string lnkPath, string target, string workingDir, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
        var link = (IShellLinkW)new CShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(workingDir);
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(lnkPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>Writes HKCU\...\Uninstall\CK3xBL (or its sandbox stand-in). Returns the key's display path.</summary>
    public static string RegisterUninstall(SetupContext ctx, string version, long estimatedSizeKb)
    {
        var uninstaller = Path.Combine(ctx.InstallDir, Product.UninstallerExe);
        var bridge = Path.Combine(ctx.InstallDir, Product.BridgeExe);
        var extra = ctx.Sandbox ? $" /target-root=\"{ctx.SandboxRoot}\"" : "";
        using var key = ctx.OpenUserKey(Product.UninstallKey, writable: true, create: true)
                        ?? throw new IOException("could not create the uninstall registry key");
        key.SetValue("DisplayName", Product.Name);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", Product.Publisher);
        key.SetValue("InstallLocation", ctx.InstallDir);
        key.SetValue("DisplayIcon", $"\"{bridge}\",0");
        key.SetValue("UninstallString", $"\"{uninstaller}\" /uninstall{extra}");
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" /uninstall /silent{extra}");
        key.SetValue("URLInfoAbout", Product.RepoUrl);
        key.SetValue("HelpLink", Product.IssuesUrl);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, Math.Max(1, estimatedSizeKb)), RegistryValueKind.DWord);
        return ctx.RegistryDisplay(Product.UninstallKey);
    }

    public static void UnregisterUninstall(SetupContext ctx)
    {
        var path = ctx.RegistryPrefix.Length > 0 ? ctx.RegistryPrefix + "\\" + Product.UninstallKey : Product.UninstallKey;
        Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        if (ctx.RegistryPrefix.Length == 0) return;
        // Sandbox: the whole stand-in goes, and its parent once no other sandbox uses it.
        Registry.CurrentUser.DeleteSubKeyTree(ctx.RegistryPrefix, throwOnMissingSubKey: false);
        var parent = ctx.RegistryPrefix.Substring(0, ctx.RegistryPrefix.LastIndexOf('\\'));
        using (var key = Registry.CurrentUser.OpenSubKey(parent))
            if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0) return;
        Registry.CurrentUser.DeleteSubKey(parent, throwOnMissingSubKey: false);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idl);
        void SetIDList(IntPtr idl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
