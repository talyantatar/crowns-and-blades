// CK3xBL.exe: the Crowns & Blades bridge, a tray application.
// Usage: CK3xBL.exe [--config <path to config.json>]
//        CK3xBL.exe --print-paths    print the resolved folders, game installs and install check, then exit (starts nothing)
//        CK3xBL.exe --quit           ask a running bridge to quit gracefully (it closes its own Bannerlord) and wait up to 30 s
//        --ck3-dir <dir> --bannerlord-dir <dir>   game folders from a launcher (Melty); used when they hold the game
//        --launch-ck3                enable the CK3 mod and start CK3 once the bridge is ready (Melty's Play)
//        --melty-test <slug>         run Melty's release test when its test file is there (MeltyTest.cs)
using System.Runtime.InteropServices;
using CK3xBL.Core;
using CK3xBL.Core.Config;
using CK3xBL.Core.Diagnostics;

namespace CK3xBL;

internal static class Program
{
    private const string SingleInstanceMutex = @"Local\CK3xBL.Bridge.SingleInstance";
    /// <summary>Signalled by "CK3xBL.exe --quit" (the installer and uninstaller use it; the bridge is never killed).</summary>
    private const string QuitEventName = @"Local\CK3xBL.Bridge.Quit";
    private static readonly TimeSpan QuitWait = TimeSpan.FromSeconds(30);

    [STAThread]
    private static int Main(string[] args)
    {
        if (HasFlag(args, "--print-paths")) return PrintPaths(ConfigArgument(args));
        if (HasFlag(args, "--quit")) return SignalQuit();

        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutex, out bool firstInstance);
        // Restarted as administrator: the old copy is still quitting (it closes its Bannerlord first), so wait for it.
        if (!firstInstance && HasFlag(args, Elevation.RestartedFlag))
        {
            try { firstInstance = mutex.WaitOne(QuitWait * 2); }
            catch (AbandonedMutexException) { firstInstance = true; }
        }
        if (!firstInstance)
        {
            MessageBox.Show("Crowns & Blades is already running. Look for its icon in the notification area.", "Crowns & Blades",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }
        using var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);

        ApplicationConfiguration.Initialize();          // PerMonitorV2 DPI awareness from the project file

        var paths = BridgePaths.Detect(ConfigArgument(args));
        if (!paths.HasData)
        {
            MessageBox.Show($"The data folder next to CK3xBL.exe is missing:\n{paths.DataDir}\n\nReinstall Crowns & Blades.", "Crowns & Blades",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
        Directory.CreateDirectory(paths.SharedDir);
        var journal = new FileJournal(paths.JournalFile);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => journal.Error("app", "unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => journal.Error("app", "unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            journal.Error("app", "unobserved task exception", e.Exception);
            e.SetObserved();
        };

        var config = BridgeConfigStore.LoadOrCreate(paths.ConfigFile, CreateDefaultConfig, out var configProblem);
        // Melty (and other launchers) pass the game folders they know: they win over detection when they hold the game.
        if (Argument(args, "--ck3-dir") is { } ck3Dir && GameLocator.IsCk3Dir(ck3Dir)) config.Ck3.GameDir = Path.GetFullPath(ck3Dir);
        if (Argument(args, "--bannerlord-dir") is { } blDir && GameLocator.IsBannerlordDir(blDir)) config.Bannerlord.GameDir = Path.GetFullPath(blDir);
        GameLocator.Resolve(config);
        var startup = new StartupActions
        {
            LaunchCk3 = HasFlag(args, "--launch-ck3"),
            MeltyTestSlug = Argument(args, "--melty-test"),
            Args = args,
        };
        if (Elevation.IsElevated) journal.Info("app", "running as administrator");
        journal.Info("app", $"CK3xBL {BridgeVersion.Full} starting: mode {config.Mode}, {(paths.IsDevTree ? "source checkout" : "installed")} " +
                            $"in {paths.InstallDir}, config {paths.ConfigFile}, CK3 {config.Ck3.GameDir}, Bannerlord {config.Bannerlord.GameDir}, " +
                            $"shared {paths.SharedDir}");

        using var tray = new TrayContext(paths, config, journal, configProblem, quit, startup);
        Application.Run(tray);
        journal.Info("app", "CK3xBL exited");
        return 0;
    }

    private static BridgeConfig CreateDefaultConfig()
    {
        var config = new BridgeConfig();
        GameLocator.Resolve(config);
        return config;
    }

    private static string? ConfigArgument(string[] args) => Argument(args, "--config");

    private static string? Argument(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static bool HasFlag(string[] args, string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------------------------------------------------ --quit

    /// <summary>Signals the running bridge and waits until it has released its single-instance mutex. 0 = not running or quit,
    /// 1 = still running after 30 s.</summary>
    private static int SignalQuit()
    {
        if (!EventWaitHandle.TryOpenExisting(QuitEventName, out var quit)) return 0;
        using (quit) quit.Set();
        var deadline = DateTime.UtcNow + QuitWait;
        while (DateTime.UtcNow < deadline)
        {
            if (!Mutex.TryOpenExisting(SingleInstanceMutex, out var mutex)) return 0;
            mutex.Dispose();
            Thread.Sleep(250);
        }
        return 1;
    }

    // ------------------------------------------------------------------------------------------------------------ --print-paths

    /// <summary>Prints what the bridge would use, without writing anything or starting a game. Exit code 2 when data/ is missing.</summary>
    private static int PrintPaths(string? explicitConfig)
    {
        AttachToParentConsole();
        var paths = BridgePaths.Detect(explicitConfig);
        var config = BridgeConfigStore.TryLoad(paths.ConfigFile, out var problem) ?? new BridgeConfig();
        GameLocator.Resolve(config);
        var checks = InstallCheck.Run(new InstallTargets
        {
            Ck3Dir = config.Ck3.GameDir,
            Ck3UserDir = config.Ck3.UserDir,
            BannerlordDir = config.Bannerlord.GameDir,
            XboxBannerlordDir = GameLocator.IsBannerlordDir(config.Bannerlord.GameDir) ? null : GameLocator.FindXboxBannerlord(),
        });

        var o = Console.Out;
        o.WriteLine($"{BridgeVersion.ProductName} {BridgeVersion.Full}");
        o.WriteLine($"install     {paths.InstallDir}");
        o.WriteLine($"root        {paths.BridgeRoot}");
        o.WriteLine($"layout      {(paths.IsDevTree ? "source checkout" : "installed")}");
        o.WriteLine($"config      {paths.ConfigFile} ({(problem ?? (File.Exists(paths.ConfigFile) ? "exists" : "not created yet"))})");
        o.WriteLine($"data        {paths.DataDir} ({(paths.HasData ? "ok" : "MISSING")})");
        o.WriteLine($"shared      {paths.SharedDir}");
        o.WriteLine($"ck3         {Describe(config.Ck3.GameDir)}");
        o.WriteLine($"ck3 user    {config.Ck3.UserDir}");
        o.WriteLine($"bannerlord  {Describe(config.Bannerlord.GameDir)}");
        foreach (var item in checks)
            o.WriteLine($"check       [{(item.IsWarning ? "!!" : "ok")}] {item.Title}{(item.IsWarning ? ": " + item.Text : "")}");
        o.Flush();
        return paths.HasData ? 0 : 2;
    }

    private static string Describe(string dir) => string.IsNullOrWhiteSpace(dir) ? "(not found)" : dir;

    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int stdHandle);

    /// <summary>A WinExe has no console. Output redirected to a pipe or file is used as it is; otherwise the text goes to the console of
    /// the shell that started it.</summary>
    private static void AttachToParentConsole()
    {
        if (GetStdHandle(StdOutputHandle) is 0 or -1) AttachConsole(AttachParentProcess);
    }
}
