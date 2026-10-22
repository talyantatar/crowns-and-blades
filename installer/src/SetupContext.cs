// Every place setup reads or writes, resolved once. In sandbox mode (/target-root) all of them live under one folder, the
// registry goes to a test key, games are never auto-detected and no real process is ever asked to quit or started.
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using CK3xBL.Setup.Util;

namespace CK3xBL.Setup;

internal sealed class SetupContext
{
    public bool Sandbox;
    public string? SandboxRoot;
    /// <summary>False when any target is given on the command line: a test can never fall through to a real install.</summary>
    public bool DetectionAllowed;

    public string LocalAppData = "";
    public string RoamingAppData = "";
    public string Documents = "";
    public string Desktop = "";
    public string StartMenuPrograms = "";
    public string TempDir = "";

    public string InstallDir = "";
    public string SharedDir = "";
    public string Ck3UserDir = "";
    /// <summary>The CK3 user folder was chosen by the player (command line or wizard), not the Documents default.</summary>
    public bool Ck3UserDirCustom;
    /// <summary>Set by an upgrade that moved from the recorded CK3 user folder to the current Documents one: the old folder.</summary>
    public string? Ck3UserDirMovedFrom;
    public string? Ck3Dir;
    public string? BlDir;

    /// <summary>The running setup exe (the stub copied as the uninstaller and re-launched for elevation).</summary>
    public string SelfExe = "";

    /// <summary>Setup runs with an elevated administrator token ("Run as administrator"). It then never starts the bridge.</summary>
    public bool Elevated;

    /// <summary>Registry path of the HKCU stand-in: "" for the real HKCU, or Software\CK3xBL-Sandbox\&lt;id&gt; in sandbox mode.</summary>
    public string RegistryPrefix = "";

    public string ManifestPath => Path.Combine(InstallDir, Product.ManifestFile);
    public string ModRoot => Path.Combine(Ck3UserDir, "mod");
    public string ModDir => Path.Combine(ModRoot, Product.ModFolder);
    public string ModDescriptorPath => Path.Combine(ModRoot, Product.ModDescriptor);
    public string? ModulesDir => BlDir is null ? null : Path.Combine(BlDir, "Modules");
    public string? ModuleDir => ModulesDir is null ? null : Path.Combine(ModulesDir, Product.ModuleFolder);
    public string BackupRoot => Path.Combine(SharedDir, Product.BackupFolder);
    /// <summary>Where CK3 keeps its user data: DocumentsParadox InteractiveCrusader Kings III.</summary>
    public string DefaultCk3UserDir => Path.Combine(Documents, "Paradox Interactive", "Crusader Kings III");
    public string DefaultLogPath => Path.Combine(SharedDir, "setup.log");

    public string RegistryDisplay(string subKey) =>
        "HKCU\\" + (RegistryPrefix.Length > 0 ? RegistryPrefix + "\\" : "") + subKey;

    /// <summary>Opens HKCU\<paramref name="subKey"/> (or its sandbox stand-in); null when missing and not created.</summary>
    public RegistryKey? OpenUserKey(string subKey, bool writable, bool create = false)
    {
        var path = RegistryPrefix.Length > 0 ? RegistryPrefix + "\\" + subKey : subKey;
        return create ? Registry.CurrentUser.CreateSubKey(path, writable) : Registry.CurrentUser.OpenSubKey(path, writable);
    }

    public static string SandboxRegistryPrefix(string root)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(FileOps.Full(root).ToLowerInvariant()));
        return @"Software\CK3xBL-Sandbox\" + FileOps.Hex(hash).Substring(0, 16);
    }

    public static SetupContext Create(Options o)
    {
        var c = new SetupContext
        {
            SelfExe = Process.GetCurrentProcess().MainModule?.FileName ?? System.Reflection.Assembly.GetEntryAssembly()!.Location,
            DetectionAllowed = !o.HasTargetOverride,
        };

        if (o.TargetRoot is not null)
        {
            var root = FileOps.Full(o.TargetRoot);
            c.Sandbox = true;
            c.SandboxRoot = root;
            c.LocalAppData = Path.Combine(root, "LocalAppData");
            c.RoamingAppData = Path.Combine(root, "RoamingAppData");
            c.Documents = Path.Combine(root, "Documents");
            c.Desktop = Path.Combine(root, "Desktop");
            c.StartMenuPrograms = Path.Combine(c.RoamingAppData, "Microsoft", "Windows", "Start Menu", "Programs");
            c.TempDir = Path.Combine(root, "Temp");
            c.Ck3Dir = Path.Combine(root, "Games", "Crusader Kings III");
            c.BlDir = Path.Combine(root, "Games", "Mount & Blade II Bannerlord");
            c.RegistryPrefix = SandboxRegistryPrefix(root);
        }
        else
        {
            c.LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            c.RoamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            // The real Documents folder, wherever it is (OneDrive and other redirections included); CK3 uses the same one.
            c.Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            c.Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            c.StartMenuPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            c.TempDir = Path.GetTempPath();
        }

        c.InstallDir = Path.Combine(c.LocalAppData, "Programs", Product.InstallFolder);
        c.SharedDir = Path.Combine(c.LocalAppData, Product.SharedFolder);
        c.Ck3UserDir = c.DefaultCk3UserDir;
        c.Elevated = Native.IsElevated();

        // Install: a folder that holds other files gets its own CK3xBL folder. Uninstall: the folder named is the install.
        if (o.InstallDir is not null) c.InstallDir = o.Uninstall ? FileOps.Full(o.InstallDir) : InstallFolderFor(o.InstallDir);
        else if (c.ExistingInstallDir() is { } existing) c.InstallDir = existing;
        if (o.Ck3UserDir is not null)
        {
            c.Ck3UserDir = FileOps.Full(o.Ck3UserDir);
            c.Ck3UserDirCustom = true;
        }
        if (o.Ck3Dir is not null) c.Ck3Dir = FileOps.Full(o.Ck3Dir);
        if (o.BlDir is not null) c.BlDir = FileOps.Full(o.BlDir);

        if (c.DetectionAllowed)
        {
            c.Ck3Dir ??= GameDetector.FindCk3();
            c.BlDir ??= GameDetector.FindBannerlord();
        }
        return c;
    }

    /// <summary>The folder setup installs to when the player names <paramref name="chosen"/>: that folder when it is new, empty,
    /// an earlier install (it holds our manifest) or already called CK3xBL; otherwise (a drive root, or a folder holding other
    /// programs' files such as "D:\Games") a CK3xBL folder inside it, as Browse does. So setup never treats a shared folder as its own.</summary>
    public static string InstallFolderFor(string chosen)
    {
        var dir = FileOps.Full(chosen);
        if (FileOps.IsRoot(dir)) return Path.Combine(dir, Product.InstallFolder);
        if (string.Equals(Path.GetFileName(dir), Product.InstallFolder, StringComparison.OrdinalIgnoreCase)) return dir;
        try
        {
            if (!Directory.Exists(dir) || File.Exists(Path.Combine(dir, Product.ManifestFile))) return dir;
            using var entries = Directory.EnumerateFileSystemEntries(dir).GetEnumerator();
            return entries.MoveNext() ? Path.Combine(dir, Product.InstallFolder) : dir;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Path.Combine(dir, Product.InstallFolder); }
    }

    /// <summary>The folder an earlier setup installed to, from its Add/Remove Programs entry, when it still holds our manifest.</summary>
    public string? ExistingInstallDir()
    {
        try
        {
            using var key = OpenUserKey(Product.UninstallKey, writable: false);
            if (key?.GetValue("InstallLocation") is string dir && dir.Length > 0 && File.Exists(Path.Combine(dir, Product.ManifestFile)))
                return FileOps.Full(dir);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return null;
    }
}
