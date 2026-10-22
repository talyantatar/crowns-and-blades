// The tray UI: notification-area icon, menu, balloons (INotifier) and the abort hotkey.
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using CK3xBL.Core;
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Ck3;
using CK3xBL.Core.Config;
using CK3xBL.Core.Diagnostics;
using CK3xBL.Core.Json;
using CK3xBL.Core.Orchestration;

namespace CK3xBL;

internal sealed class TrayContext : ApplicationContext, INotifier
{
    /// <summary>Windows cuts balloon text at 255 characters.</summary>
    private const int BalloonTextLimit = 250;
    /// <summary>A CK3 start this soon after the tray launched it was the tray's own (its mod list was checked before the launch).</summary>
    private static readonly TimeSpan OwnLaunchWindow = TimeSpan.FromMinutes(3);

    private readonly BridgePaths _paths;
    private readonly BridgeConfig _config;
    private readonly IJournal _journal;
    private readonly BridgeHost _host;
    private readonly HotkeyWindow _hotkey;
    private readonly Control _invoker;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _bannerlordItem, _startBannerlord, _closeBannerlord, _battleItem, _autostart;
    private readonly System.Windows.Forms.Timer _refresh;
    private readonly Icon _icon;
    private readonly RegisteredWaitHandle? _quitWait;
    /// <summary>Battles CK3 is fighting out after Bannerlord (battle clock), latest update last (UI thread only).</summary>
    private readonly OrderedDictionary<int, SteeringProgress> _steering = new();
    private Action? _balloonAction;              // what a click on the balloon on screen does (UI thread only)
    private AboutForm? _about;
    private bool? _ck3WasRunning;                // null until the first status refresh
    private DateTime _ck3LaunchedByTray = DateTime.MinValue;
    private bool _quitting;

    private readonly StartupActions _startup;

    public TrayContext(BridgePaths paths, BridgeConfig config, IJournal journal, string? configProblem, WaitHandle? quitSignal = null,
        StartupActions? startup = null)
    {
        _startup = startup ?? new StartupActions();
        _paths = paths;
        _config = config;
        _journal = journal;

        _invoker = new Control();
        _ = _invoker.Handle;                                   // create the handle so BeginInvoke works from any thread

        _host = new BridgeHost(paths, config, journal, this);
        _hotkey = new HotkeyWindow();
        _hotkey.Pressed += _host.AbortHotkey;
        if (!_hotkey.Registered)
            journal.Warn("app", "Ctrl+Alt+Shift+End is taken by another application; the abort hotkey is unavailable");

        _bannerlordItem = new ToolStripMenuItem("Bannerlord: starting");
        _startBannerlord = new ToolStripMenuItem("Start Bannerlord", null, (_, _) => _ = _host.StartBannerlordAsync());
        _closeBannerlord = new ToolStripMenuItem("Close Bannerlord", null, (_, _) => _ = _host.StopBannerlordAsync());
        _bannerlordItem.DropDownItems.AddRange(new ToolStripItem[] { _startBannerlord, _closeBannerlord });
        _battleItem = new ToolStripMenuItem("No battle in progress") { Enabled = false };
        _autostart = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart());

        var launch = new ToolStripMenuItem("Launch Crusader Kings III");
        launch.DropDownItems.Add(new ToolStripMenuItem("Normal (achievements)", null, (_, _) => LaunchCk3(BridgeMode.Normal)));
        launch.DropDownItems.Add(new ToolStripMenuItem("Dev mode (debug, console)", null, (_, _) => LaunchCk3(BridgeMode.Dev)));

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            launch,
            _bannerlordItem,
            _battleItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Open battle log folder", null, (_, _) => OpenFolder(_paths.SharedDir)),
            new ToolStripMenuItem("Settings", null, (_, _) => OpenSettings()),
            new ToolStripMenuItem("Set game folders...", null, (_, _) => SetGameFolders()),
            _autostart,
            new ToolStripMenuItem($"About {BridgeVersion.ProductName.Replace("&", "&&")}...", null, (_, _) => ShowAbout()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Quit", null, async (_, _) => await QuitAsync()),
        });
        _menu.Opening += (_, _) =>
        {
            RefreshStatus();
            _autostart.Checked = Autostart.IsEnabled();
        };

        _icon = DrawIcon();
        _tray = new NotifyIcon { Icon = _icon, ContextMenuStrip = _menu, Text = BridgeVersion.ProductName, Visible = true };
        _tray.DoubleClick += (_, _) => OpenFolder(_paths.SharedDir);
        _tray.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonAction;
            _balloonAction = null;
            action?.Invoke();
        };
        _tray.BalloonTipClosed += (_, _) => _balloonAction = null;

        _host.Orchestrator.PhaseChanged += (_, _) => Post(RefreshStatus);
        _host.Orchestrator.SteeringChanged += progress => Post(() =>
        {
            _steering.Remove(progress.Bid);
            if (progress.State != SteeringState.Closed) _steering.Add(progress.Bid, progress);
            RefreshStatus();
        });
        _refresh = new System.Windows.Forms.Timer { Interval = 1000 };
        _refresh.Tick += (_, _) => RefreshStatus();
        _refresh.Start();

        // "CK3xBL.exe --quit" (installer, uninstaller): the same graceful quit as the menu item.
        if (quitSignal is not null)
            _quitWait = ThreadPool.RegisterWaitForSingleObject(quitSignal, (_, _) =>
            {
                journal.Info("app", "quit requested by another process (--quit)");
                Post(() => _ = QuitAsync());
            }, null, Timeout.Infinite, executeOnlyOnce: true);

        _ = StartAsync(configProblem);
    }

    // ------------------------------------------------------------------------------------------------------------ INotifier

    public void Notify(string title, string text, NoticeKind kind = NoticeKind.Info) => Notify(title, text, kind, onClick: null);

    /// <summary>A balloon; <paramref name="onClick"/> runs (on the UI thread) when the player clicks it.</summary>
    private void Notify(string title, string text, NoticeKind kind, Action? onClick)
    {
        _journal.Write(kind == NoticeKind.Info ? JournalLevel.Info : JournalLevel.Warn, "notice", $"{title}: {text.Replace('\n', ' ')}");
        if (kind == NoticeKind.Info && !_config.Ux.Notifications) return;
        var icon = kind switch { NoticeKind.Error => ToolTipIcon.Error, NoticeKind.Warning => ToolTipIcon.Warning, _ => ToolTipIcon.Info };
        string shown = text.Length > BalloonTextLimit ? text[..(BalloonTextLimit - 3)] + "..." : text;
        Post(() =>
        {
            if (_quitting) return;
            _balloonAction = onClick;
            _tray.ShowBalloonTip(6000, title, shown, icon);
        });
    }

    // ------------------------------------------------------------------------------------------------------------ lifecycle

    private async Task StartAsync(string? configProblem)
    {
        try
        {
            await Task.Run(_host.StartAsync);
            // An install that placed the mod folder but no ck3xbl.mod next to it (Melty copies files only).
            try
            {
                if (Ck3ModSetup.EnsureCk3ModDescriptor(_config.Ck3.UserDir) is { } wrote) _journal.Info("ck3", $"mod descriptor: {wrote}");
            }
            catch (Exception e) { _journal.Warn("ck3", $"mod descriptor not written: {e.Message}"); }
            var test = MeltyTest.Find(_startup.MeltyTestSlug, _host, _journal);
            if ((_startup.LaunchCk3 || test is not null) && _host.Ck3ModInstalled && !_host.Ck3ModEnabled && !_host.Ck3Running)
                _host.EnableCk3Mod();
            var checks = await Task.Run(_host.CheckInstall);
            foreach (var item in checks)
                _journal.Write(item.IsWarning ? JournalLevel.Warn : JournalLevel.Info, "install",
                    $"{item.Title}{(item.Text.Length > 0 ? ": " + item.Text : "")}");
            var warnings = checks.Where(c => c.IsWarning).ToList();
            if (warnings.Count == 0)
                Notify($"{BridgeVersion.ProductName} ready",
                    $"{(_host.Mode == BridgeMode.Normal ? "Normal mode (achievements)" : "Dev mode")}. Battles you take part in are fought in Bannerlord. " +
                    "Ctrl+Alt+Shift+End aborts a battle.");
            else
            {
                // One balloon for all of them (each would replace the last); the click shows what to do.
                var first = warnings[0];
                string more = warnings.Count > 1 ? $" ({warnings.Count - 1} more: click for details)" : " Click for details.";
                Notify(first.Title, first.Text + more, NoticeKind.Warning, first.Fix == InstallFix.SetGameFolders ? SetGameFolders : ShowAbout);
            }
            if (configProblem is not null) Notify("Settings problem", configProblem, NoticeKind.Warning);
            if (test is not null) _ = Task.Run(test.RunAsync);
            else if (_startup.LaunchCk3 && !_host.Ck3Running)
            {
                _ck3LaunchedByTray = DateTime.UtcNow;
                if (_host.LaunchCk3(_host.Mode) is { } problem) Notify("Crusader Kings III", problem, NoticeKind.Warning);
            }
        }
        catch (Exception e)
        {
            _journal.Error("app", "startup failed", e);
            Notify($"{BridgeVersion.ProductName} could not start", e.Message, NoticeKind.Error);
        }
        Post(RefreshStatus);
    }

    private async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        _refresh.Stop();
        _about?.Close();
        _tray.Text = $"{BridgeVersion.ProductName}: closing...";
        try { await Task.Run(async () => await _host.DisposeAsync()); }
        catch (Exception e) { _journal.Error("app", "shutdown failed", e); }
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _quitWait?.Unregister(null);
            _refresh.Dispose();
            _hotkey.Dispose();
            _about?.Dispose();
            _tray.Dispose();
            _menu.Dispose();
            _invoker.Dispose();
            DestroyIcon(_icon.Handle);
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------------------------------------------------ menu actions

    private void LaunchCk3(BridgeMode mode)
    {
        if (!ConfirmModListBeforeLaunch()) return;
        var problem = _host.LaunchCk3(mode);
        if (problem is not null)
        {
            Notify("Crusader Kings III", problem, NoticeKind.Warning);
            return;
        }
        _ck3LaunchedByTray = DateTime.UtcNow;
        if (mode == BridgeMode.Dev)
            Notify("Dev mode", "CK3 runs with -debug_mode and results go through the console: achievements are off for games played this way.",
                NoticeKind.Warning);
    }

    /// <summary>CK3 loads the mods dlc_load.json lists when it starts. When our mod is installed but not listed, ask before launching:
    /// add it, launch without it, or cancel. False = cancelled.</summary>
    private bool ConfirmModListBeforeLaunch()
    {
        bool installed, enabled;
        try
        {
            installed = _host.Ck3ModInstalled;
            enabled = installed && _host.Ck3ModEnabled;
        }
        catch (Exception e)
        {
            _journal.Warn("ck3", $"mod list check failed: {e.Message}");
            return true;
        }
        if (!installed || enabled || Ck3Locator.IsRunning()) return true;

        var enable = new TaskDialogButton("Enable and launch");
        var without = new TaskDialogButton("Launch without it");
        var page = new TaskDialogPage
        {
            Caption = BridgeVersion.ProductName,
            Heading = "The Crowns & Blades mod is not in your CK3 mod list",
            Text = "Without it, CK3 does not offer battles in Bannerlord.\n\n" +
                   "'Enable and launch' adds the mod to the list CK3 reads at start (dlc_load.json; the original is backed up once). " +
                   "The Paradox Launcher replaces that list whenever you press Play there: add 'Crowns & Blades Battles' to your " +
                   "playset in the launcher to keep it.",
            Icon = TaskDialogIcon.Warning,
            Buttons = { enable, without, TaskDialogButton.Cancel },
            DefaultButton = enable,
            AllowCancel = true,
        };
        var answer = TaskDialog.ShowDialog(page);
        if (answer == without) return true;
        if (answer != enable) return false;

        var change = _host.EnableCk3Mod();
        if (change is not (ModListChange.Changed or ModListChange.Unchanged))
            Notify("CK3 mod list", change switch
            {
                ModListChange.Unreadable => "dlc_load.json could not be read, so it was left unchanged. Add the mod in the Paradox Launcher.",
                ModListChange.Ck3Running => "CK3 is already running; the mod list is read only when it starts.",
                _ => "dlc_load.json could not be changed. Add the mod in the Paradox Launcher.",
            }, NoticeKind.Warning);
        return true;
    }

    /// <summary>CK3 appeared. When something other than this tray started it (the Paradox Launcher rewrote dlc_load.json seconds
    /// before), check that our mod is in the list it loaded.</summary>
    private void OnCk3Started()
    {
        if (DateTime.UtcNow - _ck3LaunchedByTray < OwnLaunchWindow) return;
        _ = Task.Run(() =>
        {
            try
            {
                if (!_host.Ck3ModInstalled || _host.Ck3ModEnabled) return;
                Notify("CK3 started without the Crowns & Blades mod",
                    "Battles will not be offered in Bannerlord. Add 'Crowns & Blades Battles' to your playset in the Paradox Launcher, " +
                    "or start CK3 from this tray menu.", NoticeKind.Warning);
            }
            catch (Exception e) { _journal.Warn("ck3", $"mod list check failed: {e.Message}"); }
        });
    }

    private void OpenSettings()
    {
        try
        {
            if (!File.Exists(_paths.ConfigFile)) ProtocolJson.WriteAtomic(_paths.ConfigFile, _config, indented: true);
            try { Process.Start(new ProcessStartInfo(_paths.ConfigFile) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { Process.Start("notepad.exe", $"\"{_paths.ConfigFile}\""); }
            Notify("Settings", $"Restart {BridgeVersion.ProductName} (Quit, then start it again) to apply changes.");
        }
        catch (Exception e) { Notify("Settings", $"Could not open {_paths.ConfigFile}: {e.Message}", NoticeKind.Warning); }
    }

    /// <summary>Lets the player pick each game's install folder (validated), then saves them to config.json.</summary>
    private void SetGameFolders()
    {
        if (_quitting) return;
        string? ck3 = PickFolder("Choose the Crusader Kings III install folder (the one that contains 'binaries' and 'game')",
            _config.Ck3.GameDir, GameInstall.IsCk3Dir, "Crusader Kings III");
        string? bl = PickFolder("Choose the Mount & Blade II: Bannerlord install folder (the one that contains 'bin' and 'Modules')",
            _config.Bannerlord.GameDir, GameInstall.IsBannerlordDir, "Mount & Blade II: Bannerlord");
        if (ck3 is null && bl is null) return;

        var saved = BridgeConfigStore.TryLoad(_paths.ConfigFile, out var problem);
        if (saved is null && problem is not null)
        {
            Notify("Set game folders", $"{problem}. Fix or delete {_paths.ConfigFile} first.", NoticeKind.Warning);
            return;
        }
        saved ??= _config;
        if (ck3 is not null) saved.Ck3.GameDir = ck3;
        if (bl is not null) saved.Bannerlord.GameDir = bl;
        try
        {
            ProtocolJson.WriteAtomic(_paths.ConfigFile, saved, indented: true);
            _journal.Info("app", $"game folders set: CK3 {ck3 ?? "(unchanged)"}, Bannerlord {bl ?? "(unchanged)"}");
            MessageBox.Show($"Saved. Quit {BridgeVersion.ProductName} and start it again to use the new folders.", BridgeVersion.ProductName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e) { Notify("Set game folders", $"Could not write {_paths.ConfigFile}: {e.Message}", NoticeKind.Warning); }
    }

    /// <summary>A folder dialog repeated until the choice is valid (a sub-folder such as 'binaries' or 'bin\Win64_Shipping_Client' is
    /// accepted for its install folder) or cancelled (null).</summary>
    private static string? PickFolder(string description, string current, Func<string?, bool> valid, string game)
    {
        using var dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true, ShowNewFolderButton = false };
        if (Directory.Exists(current)) dialog.InitialDirectory = current;
        while (dialog.ShowDialog() == DialogResult.OK)
        {
            var dir = new DirectoryInfo(dialog.SelectedPath);
            for (int up = 0; up <= 2 && dir is not null; up++, dir = dir.Parent)   // the folder itself, or one or two levels up
                if (valid(dir.FullName)) return dir.FullName;
            MessageBox.Show($"{dialog.SelectedPath}\n\nis not a {game} install folder. Choose the folder the game is installed in.",
                BridgeVersion.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        return null;
    }

    private void ToggleAutostart()
    {
        try
        {
            bool enable = !Autostart.IsEnabled();
            Autostart.Set(enable);
            _autostart.Checked = enable;
            _journal.Info("app", enable ? "start with Windows: on" : "start with Windows: off");
        }
        catch (Exception e) { Notify("Start with Windows", e.Message, NoticeKind.Warning); }
    }

    private void ShowAbout()
    {
        if (_quitting) return;
        if (_about is { IsDisposed: false })
        {
            _about.Activate();
            return;
        }
        _about = new AboutForm(_paths, _config, _host.CheckInstall, this);
        _about.FormClosed += (_, _) => _about = null;
        _about.Show();
    }

    private void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception e) { Notify("Battle log folder", e.Message, NoticeKind.Warning); }
    }

    private void RefreshStatus()
    {
        if (_quitting) return;
        var bannerlord = _host.Bannerlord;
        bool running = bannerlord.IsRunning;
        string blState = running ? bannerlord.Status?.Status ?? "starting"
            : bannerlord.Problem is null ? "not running"
            : _host.BannerlordModuleMissing ? "CK3Bridge module not installed" : "your own Bannerlord is open";
        _bannerlordItem.Text = $"Bannerlord: {blState}";
        _startBannerlord.Enabled = !running;
        _closeBannerlord.Enabled = running;

        var phase = _host.Orchestrator.Phase;
        var id = _host.Orchestrator.ActiveBattleId;
        _battleItem.Text = phase == BattlePhase.Idle || id is null ? "No battle in progress" : $"Battle {id}: {Describe(phase)}";

        bool ck3Running = _host.Ck3Running;
        if (_ck3WasRunning == false && ck3Running) OnCk3Started();
        _ck3WasRunning = ck3Running;
        string ck3 = ck3Running ? _host.Ck3Paused == true ? "CK3 paused" : "CK3 running" : "CK3 not running";
        var tip = $"{BridgeVersion.ProductName} {BridgeVersion.Display} ({_host.Mode})\n{ck3} · Bannerlord {blState}";
        if (id is not null) tip += $"\n{Describe(phase)}";
        if (_steering.Count > 0) tip += $"\n{Describe(_steering.GetAt(_steering.Count - 1).Value)}";
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    /// <summary>"Battle of Orangallu, day 6: 1,212 of 2,116 enemy fallen".</summary>
    private static string Describe(SteeringProgress p) => p.State switch
    {
        SteeringState.Awaiting => $"{p.Battle}: click '{Ck3Texts.DustSettlesOption}' in CK3",
        SteeringState.Suspended => $"{p.Battle}: reinforcements arrive",
        SteeringState.Fixing => $"{p.Battle}: over, the armies regroup",
        _ when p is { CombatDay: { } day, EnemyFallen: { } fallen, EnemyTarget: { } target } =>
            $"{p.Battle}, day {day}: {fallen:N0} of {target:N0} enemy fallen",
        _ => $"{p.Battle}: decided in CK3",
    };

    private static string Describe(BattlePhase phase) => phase switch
    {
        BattlePhase.Preparing => "preparing",
        BattlePhase.Offered => "awaiting your choice in CK3",
        BattlePhase.WaitingToEnter => "waiting for CK3 / Bannerlord",
        BattlePhase.Entering => "switching to Bannerlord",
        BattlePhase.Fighting => "fighting",
        BattlePhase.ResultShown => "battle over",
        BattlePhase.Returning => "returning to CK3",
        BattlePhase.AwaitingReturnFocus => "result waiting for CK3",
        BattlePhase.Delivering => "sending the result to CK3",
        _ => "idle",
    };

    private void Post(Action action)
    {
        if (_invoker.IsDisposed) return;
        try { _invoker.BeginInvoke(action); }
        catch (InvalidOperationException) { }   // shutting down
    }

    // ------------------------------------------------------------------------------------------------------------ icon

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint hIcon);

    /// <summary>The Crowns &amp; Blades badge: a gold crown over two crossed swords on a dark red rounded square (installer AppIcon.Draw).</summary>
    private static Icon DrawIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            using (var field = new SolidBrush(Color.FromArgb(90, 20, 20)))
            using (var square = new GraphicsPath())
            {
                square.AddArc(0.5f, 0.5f, 14, 14, 180, 90);
                square.AddArc(17.5f, 0.5f, 14, 14, 270, 90);
                square.AddArc(17.5f, 17.5f, 14, 14, 0, 90);
                square.AddArc(0.5f, 17.5f, 14, 14, 90, 90);
                square.CloseFigure();
                g.FillPath(field, square);
            }
            using (var blade = new Pen(Color.FromArgb(217, 217, 217), 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(blade, 7.5f, 27.5f, 24.5f, 10.5f);
                g.DrawLine(blade, 24.5f, 27.5f, 7.5f, 10.5f);
            }
            using (var grip = new Pen(Color.FromArgb(138, 106, 42), 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(grip, 9.8f, 22.6f, 12.3f, 25.1f);
                g.DrawLine(grip, 22.2f, 22.6f, 19.7f, 25.1f);
            }
            var crown = new[]
            {
                new PointF(8.1f, 12.8f), new PointF(8.1f, 5.9f), new PointF(12.3f, 9.4f), new PointF(16f, 4.4f),
                new PointF(19.7f, 9.4f), new PointF(23.9f, 5.9f), new PointF(23.9f, 12.8f),
            };
            using (var gold = new SolidBrush(Color.FromArgb(232, 184, 48))) g.FillPolygon(gold, crown);
            using (var edge = new Pen(Color.FromArgb(122, 90, 16), 0.5f) { LineJoin = LineJoin.Round }) g.DrawPolygon(edge, crown);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
