// The setup wizard: Welcome, License, Game folders, Options, Installing, Finished. The work itself is InstallEngine's,
// run on a worker thread; its questions come back here as dialogs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using CK3xBL.Setup.Engine;
using CK3xBL.Setup.Util;

namespace CK3xBL.Setup.Ui;

internal sealed class WizardForm : Form
{
    private enum Page { Welcome, License, Folders, Options, Progress, Finish }

    private readonly SetupContext _c;
    private readonly Payload _p;
    private readonly Options _opt;
    private readonly SetupLog _log;

    private readonly Label _title = new() { AutoSize = true, Font = Theme.TitleFont, Location = new Point(16, 12) };
    private readonly Label _subtitle = new() { AutoSize = true, ForeColor = Theme.Muted, Location = new Point(18, 38) };
    private readonly Panel _content = new() { Dock = DockStyle.Fill, Padding = new Padding(20, 14, 20, 8) };
    private readonly Button _back = new() { Text = "< Back", Size = new Size(88, 28) };
    private readonly Button _next = new() { Text = "Next >", Size = new Size(88, 28) };
    private readonly Button _cancel = new() { Text = "Cancel", Size = new Size(88, 28) };
    private readonly Dictionary<Page, Control> _pages = new();
    private Page _page = Page.Welcome;

    // License
    private readonly RadioButton _accept = new() { Text = "I accept the license", AutoSize = true };
    private readonly RadioButton _decline = new() { Text = "I do not accept", AutoSize = true, Checked = true };

    // Folders
    private TextBox _ck3Dir = null!, _ck3UserDir = null!, _blDir = null!, _installDir = null!;
    private Label _ck3Status = null!, _userStatus = null!, _blStatus = null!, _installStatus = null!;
    private readonly string _initialUserDir;
    private readonly bool _initialUserDirCustom;

    // Options
    private readonly CheckBox _optMod = new() { Text = "Install the CK3 mod \"Crowns & Blades Battles\"", AutoSize = true, Checked = true };
    private readonly CheckBox _optDlc = new() { Text = "Add the mod to CK3's current mod list (a backup of the list is kept)", AutoSize = true, Checked = true };
    private readonly CheckBox _optModule = new() { Text = "Install the Bannerlord module \"CK3 Bridge\"", AutoSize = true, Checked = true };
    private readonly CheckBox _optStartMenu = new() { Text = "Start menu shortcut", AutoSize = true, Checked = true };
    private readonly CheckBox _optDesktop = new() { Text = "Desktop shortcut", AutoSize = true };
    private readonly Label _optModWhere = new() { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(20, 0, 3, 8) };
    private readonly Label _optModuleWhere = new() { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(20, 0, 3, 8) };

    // Progress and finish
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Top, Height = 20 };
    private readonly Label _stepLabel = new() { Dock = DockStyle.Top, AutoSize = false, Height = 24, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ListBox _logList = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
    private readonly CheckBox _startNow = new() { Text = "Start Crowns & Blades now", AutoSize = true, Checked = true };
    private bool _installing;

    public int ExitCode { get; private set; } = ExitCodes.Cancelled;
    public bool StartBridgeRequested => ExitCode is ExitCodes.Ok or ExitCodes.Partial && _startNow.Enabled && _startNow.Checked;

    public WizardForm(SetupContext ctx, Payload payload, Options options, SetupLog log, InstallManifest? previous)
    {
        _c = ctx;
        _p = payload;
        _opt = options;
        _log = log;
        _initialUserDir = ctx.Ck3UserDir;
        _initialUserDirCustom = ctx.Ck3UserDirCustom;

        SuspendLayout();
        Text = $"{Product.Name} {payload.Manifest.Version} Setup" + (ctx.Sandbox ? " (sandbox)" : "") + (options.DryRun ? " (dry run)" : "");
        Font = Theme.BodyFont;
        Icon = AppIcon.WindowIcon();
        Mnemonics.Off(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(640, 500);

        // Width set before the anchored children are added, so their right anchors are measured from the final width.
        var header = new Panel { Dock = DockStyle.Top, Width = 640, Height = 64, BackColor = Color.White };
        header.Controls.Add(_title);
        header.Controls.Add(_subtitle);
        var logo = new PictureBox { Image = AppIcon.Draw(48), Size = new Size(48, 48), Location = new Point(640 - 64, 8), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        header.Controls.Add(logo);
        var headerLine = new Label { Dock = DockStyle.Top, Height = 2, BorderStyle = BorderStyle.Fixed3D };

        var footer = new Panel { Dock = DockStyle.Bottom, Width = 640, Height = 48 };
        var footerLine = new Label { Dock = DockStyle.Bottom, Height = 2, BorderStyle = BorderStyle.Fixed3D };
        _cancel.Location = new Point(640 - 16 - 88, 10);
        _next.Location = new Point(_cancel.Left - 12 - 88, 10);
        _back.Location = new Point(_next.Left - 4 - 88, 10);
        foreach (var b in new[] { _back, _next, _cancel })
        {
            b.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            footer.Controls.Add(b);
        }
        _back.Click += (_, _) => Go(_page - 1);
        _next.Click += (_, _) => OnNext();
        _cancel.Click += (_, _) => Close();
        AcceptButton = _next;

        Controls.Add(_content);
        Controls.Add(headerLine);
        Controls.Add(header);
        Controls.Add(footerLine);
        Controls.Add(footer);

        _pages[Page.Welcome] = BuildWelcome(previous);
        _pages[Page.License] = BuildLicense();
        _pages[Page.Folders] = BuildFolders();
        _pages[Page.Options] = BuildOptions(previous);
        _pages[Page.Progress] = BuildProgress();
        foreach (var page in _pages.Values)
        {
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _content.Controls.Add(page);
        }

        Theme.NoMnemonics(this);   // "Mount & Blade" must not turn into an underlined shortcut
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false);
        PerformLayout();
        Go(Page.Welcome);
    }

    private static Label Para(string text, int width = 590) =>
        new() { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), Margin = new Padding(0, 0, 0, 10) };

    private static FlowLayoutPanel Column() =>
        new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

    private Control BuildWelcome(InstallManifest? previous)
    {
        var col = Column();
        col.Controls.Add(Para(
            "Fight your Crusader Kings III battles in Mount & Blade II: Bannerlord. When a battle you are part of starts in CK3, " +
            "it is played out in Bannerlord with your real armies, commanders and terrain, and the result goes back to CK3."));
        col.Controls.Add(Para("Setup installs, for your Windows account only:\r\n" +
                              "  •  the Crowns & Blades app (it runs in the notification area)\r\n" +
                              "  •  the CK3 mod \"Crowns & Blades Battles\" (in your Documents folder)\r\n" +
                              "  •  the Bannerlord module \"CK3 Bridge\" (in Bannerlord's Modules folder)\r\n" +
                              "No game files are changed, and the uninstaller removes all of it again."));
        col.Controls.Add(Para($"This is a beta. It was built for CK3 {Short(_p.Manifest.SupportsCk3, "1.20")} and Bannerlord " +
                              $"{Short(_p.Manifest.SupportsBannerlord, "1.4")}; other versions may break and are not blocked. " +
                              "You need your own copies of both games."));
        if (previous is not null)
        {
            int cmp = ReleaseVersion.Compare(_p.Manifest.Version, previous.Version);
            var what = cmp > 0 ? $"Version {previous.Version} is installed and will be updated to {_p.Manifest.Version}."
                : cmp == 0 ? $"Version {previous.Version} is already installed; setup will repair it."
                : $"A newer version ({previous.Version}) is installed.";
            col.Controls.Add(new Label { Text = what, AutoSize = true, ForeColor = Theme.Caution, Margin = new Padding(0, 0, 0, 10) });
        }
        if (_c.Sandbox)
            col.Controls.Add(new Label { Text = $"Sandbox: everything goes under {_c.SandboxRoot}", AutoSize = true, ForeColor = Theme.Caution });
        return col;
    }

    private static string Short(string? pattern, string fallback) =>
        string.IsNullOrWhiteSpace(pattern) ? fallback : pattern!.TrimEnd('*').TrimEnd('.').TrimStart('v');

    private Control BuildLicense()
    {
        var panel = new TableLayoutPanel { ColumnCount = 1, RowCount = 3 };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = SystemColors.Window,
            Text = LicenseText.For(_p),
        };
        panel.Controls.Add(box, 0, 0);
        panel.Controls.Add(_accept, 0, 1);
        panel.Controls.Add(_decline, 0, 2);
        _accept.CheckedChanged += (_, _) => UpdateButtons();
        return panel;
    }

    private Control BuildFolders()
    {
        var t = new TableLayoutPanel { ColumnCount = 2 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        (_ck3Dir, _ck3Status) = FolderRow(t, "Crusader Kings III game folder", _c.Ck3Dir);
        (_ck3UserDir, _userStatus) = FolderRow(t, "Crusader Kings III user folder (mods and saves)", _c.Ck3UserDir);
        (_blDir, _blStatus) = FolderRow(t, "Mount & Blade II: Bannerlord game folder", _c.BlDir);
        (_installDir, _installStatus) = FolderRow(t, "Install Crowns & Blades to", _c.InstallDir, Product.InstallFolder);
        foreach (var box in new[] { _ck3Dir, _ck3UserDir, _blDir, _installDir }) box.TextChanged += (_, _) => ValidateFolders();
        ValidateFolders();
        return t;
    }

    /// <param name="ownFolder">For the install folder: a folder of that name is added to the one picked with Browse.</param>
    private (TextBox, Label) FolderRow(TableLayoutPanel t, string caption, string? value, string? ownFolder = null)
    {
        var label = new Label { Text = caption, AutoSize = true, Margin = new Padding(0, 4, 0, 2) };
        var box = new TextBox { Text = value ?? "", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 6, 0) };
        var browse = new Button { Text = "Browse...", AutoSize = true, MinimumSize = new Size(88, 24), Margin = new Padding(0) };
        var status = new Label { AutoSize = true, MaximumSize = new Size(580, 0), Margin = new Padding(0, 2, 0, 4) };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = caption, ShowNewFolderButton = ownFolder is not null };
            if (Directory.Exists(box.Text)) dlg.SelectedPath = box.Text;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var picked = dlg.SelectedPath;
            if (ownFolder is not null && !string.Equals(Path.GetFileName(picked.TrimEnd('\\')), ownFolder, StringComparison.OrdinalIgnoreCase))
                picked = Path.Combine(picked, ownFolder);
            box.Text = picked;
        };
        int row = t.RowCount;
        t.RowCount += 3;
        t.Controls.Add(label, 0, row);
        t.SetColumnSpan(label, 2);
        t.Controls.Add(box, 0, row + 1);
        t.Controls.Add(browse, 1, row + 1);
        t.Controls.Add(status, 0, row + 2);
        t.SetColumnSpan(status, 2);
        return (box, status);
    }

    private static void Status(Label l, string text, Color color)
    {
        l.Text = text;
        l.ForeColor = color;
    }

    private static string? Clean(TextBox box)
    {
        var s = box.Text.Trim().Trim('"');
        if (s.Length == 0) return null;
        try { return Path.IsPathRooted(s) ? FileOps.Full(s) : null; }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private void ValidateFolders()
    {
        if (_ck3Dir is null) return;
        var ck3 = Clean(_ck3Dir);
        if (GameDetector.IsCk3Dir(ck3))
        {
            var v = GameDetector.Ck3Version(ck3);
            bool ok = GameDetector.VersionMatches(v, _p.Manifest.SupportsCk3);
            Status(_ck3Status, $"✔ Crusader Kings III {v ?? ""} ({GameDetector.StoreOf(ck3)})" +
                               (ok ? "" : $" - this release was built for {_p.Manifest.SupportsCk3}; it may not work"), ok ? Theme.Good : Theme.Caution);
        }
        else
        {
            Status(_ck3Status, "binaries\\ck3.exe was not found here. The mod can still be installed; the app asks for this folder later.",
                Theme.Caution);
        }

        var user = Clean(_ck3UserDir);
        var moved = _c.Ck3UserDirMovedFrom is { } old && FileOps.PathEquals(user, _c.Ck3UserDir)
            ? $" Your Documents folder moved since the last install (it was {old})."
            : "";
        if (user is null) Status(_userStatus, "Choose a folder (normally Documents\\Paradox Interactive\\Crusader Kings III).", Theme.Bad);
        else if (Directory.Exists(user)) Status(_userStatus, "✔ " + (Directory.Exists(Path.Combine(user, "mod")) ? "CK3's mod folder is here" : "found") + moved, Theme.Good);
        else Status(_userStatus, "This folder does not exist yet and will be created. Start CK3 once first if you are not sure it is the right one." + moved, Theme.Caution);

        var bl = Clean(_blDir);
        if (GameDetector.IsBannerlordDir(bl))
        {
            var v = GameDetector.BannerlordVersion(bl);
            var store = GameDetector.StoreOf(bl);
            bool ok = GameDetector.VersionMatches(v, _p.Manifest.SupportsBannerlord);
            Status(_blStatus, $"✔ Bannerlord {v ?? ""} ({store})" +
                              (ok ? "" : $" - this release was built for {_p.Manifest.SupportsBannerlord}; it may not work") +
                              (store is "GOG" or "Epic" ? $" - the {store} version is untested" : ""),
                ok && store is not ("GOG" or "Epic") ? Theme.Good : Theme.Caution);
        }
        else
        {
            Status(_blStatus, "bin\\Win64_Shipping_Client\\Bannerlord.exe was not found here, so the Bannerlord module will not be installed.", Theme.Bad);
        }

        var inst = Clean(_installDir);
        var target = inst is null ? null : SetupContext.InstallFolderFor(inst);
        if (inst is null) Status(_installStatus, "Choose a folder.", Theme.Bad);
        else if (!FileOps.PathEquals(inst, target))
            Status(_installStatus, $"Setup will install to {target}" +
                                   (FileOps.IsRoot(inst) ? "." : ", because this folder already holds other files."), Theme.Muted);
        else Status(_installStatus, "", Theme.Muted);
        UpdateButtons();
    }

    private Control BuildOptions(InstallManifest? previous)
    {
        var col = Column();
        col.Controls.Add(_optMod);
        col.Controls.Add(_optModWhere);
        col.Controls.Add(_optDlc);
        col.Controls.Add(new Label
        {
            Text = "The Paradox Launcher replaces this list whenever you press Play there. To keep the mod, also add " +
                   "\"Crowns & Blades Battles\" to your playset in the launcher (Playsets, Add mods).",
            AutoSize = true,
            MaximumSize = new Size(570, 0),
            ForeColor = Theme.Muted,
            Margin = new Padding(20, 0, 3, 8),
        });
        col.Controls.Add(_optModule);
        col.Controls.Add(_optModuleWhere);
        col.Controls.Add(_optStartMenu);
        col.Controls.Add(_optDesktop);
        if (previous is not null)
            _optDesktop.Checked = previous.Shortcuts.Any(s => FileOps.PathEquals(Path.GetDirectoryName(s), _c.Desktop));
        _optMod.CheckedChanged += (_, _) => _optDlc.Enabled = _optMod.Checked;
        _optMod.Checked = !_opt.NoCk3Mod;
        _optDlc.Checked = !_opt.NoDlcLoad;
        _optModule.Checked = !_opt.NoBlModule;
        _optStartMenu.Checked = !_opt.NoStartMenu;
        _optDesktop.Checked |= _opt.DesktopShortcut;
        return col;
    }

    private Control BuildProgress()
    {
        var panel = new Panel();
        panel.Controls.Add(_logList);
        panel.Controls.Add(new Label { Dock = DockStyle.Top, Height = 8 });
        panel.Controls.Add(_bar);
        panel.Controls.Add(_stepLabel);
        return panel;
    }

    private void Go(Page page)
    {
        if (page < Page.Welcome) return;
        if (page == Page.Options) ApplyFolders();
        _pages[_page].Visible = false;
        _page = page;
        _pages[_page].Visible = true;
        (_title.Text, _subtitle.Text) = page switch
        {
            Page.Welcome => ($"Welcome to {Product.Name} {_p.Manifest.Version}", "Crusader Kings III battles, fought in Mount & Blade II: Bannerlord"),
            Page.License => ("License", "Please read the license and the notice below"),
            Page.Folders => ("Game folders", "Check the folders setup found, or choose them with Browse"),
            Page.Options => ("Options", "Choose what to install"),
            Page.Progress => (_opt.DryRun ? "Planning" : "Installing", "Please wait"),
            _ => ("Finished", ""),
        };
        UpdateButtons();
        if (page == Page.Progress) StartInstall();
    }

    private void UpdateButtons()
    {
        _back.Enabled = _page is Page.License or Page.Folders or Page.Options;
        _next.Text = _page switch { Page.Options => _opt.DryRun ? "Plan" : "Install", Page.Finish => "Finish", _ => "Next >" };
        _next.Enabled = _page switch
        {
            Page.License => _accept.Checked,
            Page.Folders => _installDir is not null && Clean(_installDir) is not null && Clean(_ck3UserDir) is not null,
            Page.Progress => false,
            _ => true,
        };
        _cancel.Enabled = !_installing;
        _cancel.Visible = _page != Page.Finish;
    }

    private void OnNext()
    {
        if (_page == Page.Finish)
        {
            Close();
            return;
        }
        Go(_page + 1);
    }

    /// <summary>The folders page's choices go into the context; the options page shows where things will go.</summary>
    private void ApplyFolders()
    {
        _c.Ck3Dir = GameDetector.IsCk3Dir(Clean(_ck3Dir)) ? Clean(_ck3Dir) : null;
        _c.Ck3UserDir = Clean(_ck3UserDir) ?? _c.Ck3UserDir;
        // A folder the player typed or picked that differs from the Documents default is theirs: upgrades keep it even if
        // Documents moves. The pre-filled folder keeps what setup already knew about it.
        _c.Ck3UserDirCustom = FileOps.PathEquals(_c.Ck3UserDir, _initialUserDir)
            ? _initialUserDirCustom
            : !FileOps.PathEquals(_c.Ck3UserDir, _c.DefaultCk3UserDir);
        if (FileOps.PathEquals(_c.Ck3UserDir, _c.Ck3UserDirMovedFrom)) _c.Ck3UserDirMovedFrom = null;
        _c.BlDir = GameDetector.IsBannerlordDir(Clean(_blDir)) ? Clean(_blDir) : null;
        _c.InstallDir = Clean(_installDir) is { } inst ? SetupContext.InstallFolderFor(inst) : _c.InstallDir;

        _optModWhere.Text = "to " + _c.ModDir;
        bool blOk = _c.BlDir is not null;
        _optModule.Enabled = blOk;
        if (!blOk) _optModule.Checked = false;
        _optModuleWhere.Text = blOk ? "to " + _c.ModuleDir : "Bannerlord was not found (Game folders page)";
        _optDlc.Enabled = _optMod.Checked;
    }

    private void StartInstall()
    {
        _installing = true;
        UpdateButtons();
        var choices = new InstallChoices
        {
            InstallCk3Mod = _optMod.Checked,
            EnableInDlcLoad = _optMod.Checked && _optDlc.Checked,
            InstallBlModule = _optModule.Enabled && _optModule.Checked,
            StartMenuShortcut = _optStartMenu.Checked,
            DesktopShortcut = _optDesktop.Checked,
            DryRun = _opt.DryRun,
        };
        var engine = new InstallEngine(_c, _p, choices, new GuiInteraction(this), _log, _opt.PayloadPath);
        engine.Progress += (pct, step) => BeginInvoke(new Action(() =>
        {
            _bar.Value = Math.Max(0, Math.Min(100, pct));
            _stepLabel.Text = step;
        }));
        _log.Line += OnLogLine;
        Task.Run(() => engine.Run()).ContinueWith(t => BeginInvoke(new Action(() =>
        {
            _log.Line -= OnLogLine;
            if (t.IsFaulted)
            {
                var e = t.Exception?.GetBaseException();
                _log.Error($"setup failed: {e}");
                ShowFinish(ExitCodes.Failed, new List<StepResult>
                {
                    new() { Step = "Setup", Status = StepStatus.Failed, Message = e?.Message ?? "unknown error" },
                });
            }
            else
            {
                ShowFinish(t.Result, engine.Results);
            }
        })));
    }

    private void OnLogLine(LogLevel level, string text)
    {
        if (level == LogLevel.Detail || IsDisposed) return;
        BeginInvoke(new Action(() =>
        {
            var prefix = level switch { LogLevel.Warn => "! ", LogLevel.Error => "X ", LogLevel.Plan => "> ", _ => "  " };
            _logList.Items.Add(prefix + text);
            _logList.TopIndex = Math.Max(0, _logList.Items.Count - 1);
        }));
    }

    private void ShowFinish(int exitCode, List<StepResult> results)
    {
        _installing = false;
        ExitCode = exitCode;
        var col = Column();
        col.Dock = DockStyle.Fill;
        string headline = _opt.DryRun ? "Dry run finished: nothing was changed. The plan is in the log."
            : exitCode == ExitCodes.Ok ? $"{Product.Name} {_p.Manifest.Version} is installed."
            : exitCode == ExitCodes.Partial ? "Setup finished, but some steps need your attention (see below)."
            : exitCode == ExitCodes.Blocked ? "Setup did not change anything: Crowns & Blades is still running."
            : "Setup failed (see below).";
        col.Controls.Add(new Label { Text = headline, AutoSize = true, MaximumSize = new Size(590, 0), Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) });
        foreach (var r in results.Where(r => r.Status != StepStatus.NotSelected))
        {
            var (mark, color) = r.Status switch
            {
                StepStatus.Done => ("✔", Theme.Good),
                StepStatus.Failed => ("✖", Theme.Bad),
                _ => ("!", Theme.Caution),
            };
            col.Controls.Add(new Label
            {
                Text = $"{mark}  {r.Step}: {r.Message}",
                AutoSize = true,
                MaximumSize = new Size(590, 0),
                ForeColor = r.Status == StepStatus.Done ? SystemColors.ControlText : color,
                Margin = new Padding(0, 0, 0, 3),
            });
        }
        if (!_opt.DryRun && exitCode is ExitCodes.Ok or ExitCodes.Partial)
        {
            col.Controls.Add(Para(
                "\r\nNext:\r\n" +
                "1.  In the Paradox Launcher, open Playsets and add \"Crowns & Blades Battles\" to the playset you play with.\r\n" +
                "2.  Start Bannerlord once from its own launcher and reach the main menu, so it can show its first-start notices.\r\n" +
                "3.  Start Crowns & Blades (its icon appears in the notification area), then start CK3."));
            bool canStart = !_c.Sandbox && !_c.Elevated && File.Exists(Path.Combine(_c.InstallDir, Product.BridgeExe)) && !ProcessGuard.AnyBridgeInstance();
            _startNow.Enabled = canStart;
            _startNow.Checked = canStart;
            if (canStart) col.Controls.Add(_startNow);
        }
        if (_log.FilePath is not null)
        {
            var link = new LinkLabel { Text = "Open the setup log", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            link.LinkClicked += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(_log.FilePath) { UseShellExecute = true }); }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException) { }
            };
            col.Controls.Add(link);
        }
        Theme.NoMnemonics(col);
        _pages[Page.Finish] = col;
        _content.Controls.Add(col);
        col.BringToFront();
        Go(Page.Finish);
        _next.Focus();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_installing)
        {
            e.Cancel = true;
            return;
        }
        if (_page < Page.Progress && e.CloseReason == CloseReason.UserClosing &&
            ChoiceDialog.Show(this, "Cancel setup", "Setup is not finished. Exit setup?", new[] { "Exit setup", "Continue" }, 1) != 0)
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }
}
