// Tray: "About Crowns & Blades...": the version, what it was built for, the install check, the folders in use, the licence, and the
// links for help (the GitHub page, issues, and a diagnostics block to paste into an issue).
using System.Diagnostics;
using CK3xBL.Core;
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Config;
using CK3xBL.Core.Diagnostics;

namespace CK3xBL;

internal sealed class AboutForm : Form
{
    private readonly BridgePaths _paths;
    private readonly BridgeConfig _config;
    private readonly Func<List<InstallItem>> _check;
    private readonly INotifier _notifier;
    private readonly TextBox _checks;
    private List<InstallItem> _items = new();

    /// <summary>"Crowns &amp; Blades", "Mount &amp; Blade": an '&amp;' in a label or button is shown as written, not as a mnemonic.</summary>
    private static void NoMnemonics(Control root)
    {
        if (root is Label label) label.UseMnemonic = false;
        else if (root is ButtonBase button) button.UseMnemonic = false;
        foreach (Control child in root.Controls) NoMnemonics(child);
    }

    public AboutForm(BridgePaths paths, BridgeConfig config, Func<List<InstallItem>> check, INotifier notifier)
    {
        _paths = paths;
        _config = config;
        _check = check;
        _notifier = notifier;

        Text = $"About {BridgeVersion.ProductName}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(640, 520);
        Padding = new Padding(14);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            Text = $"{BridgeVersion.ProductName} {BridgeVersion.Display}",
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
        });
        layout.Controls.Add(new Label
        {
            Text = "Battles you take part in, in Crusader Kings III, are fought in Mount & Blade II: Bannerlord.\n" +
                   $"Built for CK3 {BridgeVersion.Ck3Target}.x and Bannerlord {BridgeVersion.BannerlordTarget}; other versions may break and are not blocked.",
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            Margin = new Padding(0, 0, 0, 10),
        });
        layout.Controls.Add(new Label { Text = "Install check", Font = new Font("Segoe UI", 9f, FontStyle.Bold), AutoSize = true });
        _checks = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Text = "Checking...",
            BackColor = SystemColors.Window,
        };
        layout.Controls.Add(_checks);
        layout.Controls.Add(new Label
        {
            Text = $"Program: {paths.InstallDir}{(paths.IsDevTree ? " (source checkout)" : "")}\n" +
                   $"Settings: {paths.ConfigFile}\nLogs and battle archive: {paths.SharedDir}",
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            Margin = new Padding(0, 8, 0, 8),
        });
        layout.Controls.Add(new Label
        {
            Text = $"MIT License. {BridgeVersion.Copyright}. An unofficial fan project, not affiliated with or endorsed by " +
                   "Paradox Interactive or TaleWorlds Entertainment.",
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 10),
        });

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        buttons.Controls.Add(MakeButton("Open GitHub page", () => OpenUrl(BridgeVersion.RepositoryUrl)));
        buttons.Controls.Add(MakeButton("Report a problem", () => OpenUrl(BridgeVersion.IssuesUrl)));
        buttons.Controls.Add(MakeButton("Copy diagnostics", CopyDiagnostics));
        var close = MakeButton("Close", Close);
        buttons.Controls.Add(close);
        layout.Controls.Add(buttons);
        CancelButton = close;

        Controls.Add(layout);
        NoMnemonics(this);
        Shown += async (_, _) => await RefreshChecksAsync();
    }

    private static Button MakeButton(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
        b.Click += (_, _) => onClick();
        return b;
    }

    private async Task RefreshChecksAsync()
    {
        try { _items = await Task.Run(_check); }
        catch (Exception e)
        {
            _checks.Text = $"The check failed: {e.Message}";
            return;
        }
        if (IsDisposed) return;
        _checks.Text = string.Join(Environment.NewLine, _items.Select(i =>
            $"{(i.IsWarning ? "⚠" : "✓")}  {i.Title}{(i.Text.Length > 0 ? Environment.NewLine + "     " + i.Text : "")}"));
        _checks.Select(0, 0);
    }

    private void CopyDiagnostics()
    {
        try
        {
            Clipboard.SetText(DiagnosticsReport.Build(_paths, _config, _items));
            _notifier.Notify("Diagnostics copied",
                "Paste them into your issue report. Your Windows user folder is shown as %USERPROFILE%.");
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.ExternalException or ThreadStateException)
        {
            _notifier.Notify("Copy diagnostics", $"The clipboard is busy: {e.Message}", NoticeKind.Warning);
        }
    }

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { _notifier.Notify("Open link", $"{url}: {e.Message}", NoticeKind.Warning); }
    }
}
