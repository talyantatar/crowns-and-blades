// The uninstall window: what will be removed, the "also delete my settings and logs" box, then progress and the result.
using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using CK3xBL.Setup.Engine;

namespace CK3xBL.Setup.Ui;

internal sealed class UninstallForm : Form
{
    private readonly SetupContext _c;
    private readonly Options _opt;
    private readonly SetupLog _log;
    private readonly CheckBox _deleteData;
    private readonly Button _go = new() { Text = "Uninstall", Size = new Size(96, 28) };
    private readonly Button _close = new() { Text = "Cancel", Size = new Size(96, 28) };
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true, Visible = false };
    private readonly FlowLayoutPanel _intro = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private bool _running, _finished;

    public int ExitCode { get; private set; } = ExitCodes.Cancelled;

    public UninstallForm(SetupContext ctx, Options options, SetupLog log, InstallManifest? manifest)
    {
        _c = ctx;
        _opt = options;
        _log = log;
        SuspendLayout();
        Text = $"Uninstall {Product.Name}" + (ctx.Sandbox ? " (sandbox)" : "") + (options.DryRun ? " (dry run)" : "");
        Font = Theme.BodyFont;
        Icon = AppIcon.WindowIcon();
        Mnemonics.Off(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 360);
        Padding = new Padding(16);

        _intro.Controls.Add(new Label
        {
            Text = $"Uninstall {Product.Name} {manifest?.Version}",
            Font = Theme.TitleFont,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10),
        });
        _intro.Controls.Add(new Label
        {
            Text = "This removes the Crowns & Blades app, the CK3 mod and the Bannerlord module that setup installed, and takes " +
                   "the mod out of CK3's mod list again. Your CK3 and Bannerlord saves are never touched.\r\n\r\n" +
                   "If a battle is being fought or steered right now, finish it first: a CK3 save made in the middle of one keeps " +
                   "a few Crowns & Blades values that CK3 then drops with a warning.",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Margin = new Padding(0, 0, 0, 12),
        });
        _deleteData = new CheckBox
        {
            Text = "Also delete my Crowns & Blades settings, logs and battle archive",
            AutoSize = true,
            Checked = options.DeleteUserData,
        };
        _intro.Controls.Add(_deleteData);
        _intro.Controls.Add(new Label
        {
            Text = ctx.SharedDir,
            AutoSize = true,
            MaximumSize = new Size(500, 0),
            ForeColor = Theme.Muted,
            Margin = new Padding(18, 0, 0, 0),
        });

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40, Padding = new Padding(0, 8, 0, 0) };
        footer.Controls.Add(_close);
        footer.Controls.Add(_go);
        _go.Click += (_, _) => Start();
        _close.Click += (_, _) => Close();

        Controls.Add(_intro);
        Controls.Add(_list);
        Controls.Add(footer);
        Theme.NoMnemonics(this);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(false);
        PerformLayout();
    }

    private void Start()
    {
        _running = true;
        _go.Enabled = false;
        _close.Enabled = false;
        _intro.Visible = false;
        _list.Visible = true;
        var engine = new UninstallEngine(_c, new GuiInteraction(this), _log, _opt.DryRun, _deleteData.Checked);
        _log.Line += OnLine;
        Task.Run(() => engine.Run()).ContinueWith(t => BeginInvoke(new Action(() =>
        {
            _log.Line -= OnLine;
            _running = false;
            _finished = true;
            if (t.IsFaulted)
            {
                _log.Error($"uninstall failed: {t.Exception?.GetBaseException()}");
                ExitCode = ExitCodes.Failed;
            }
            else
            {
                ExitCode = t.Result;
            }
            _list.Items.Add("");
            _list.Items.Add(_opt.DryRun ? "Dry run finished: nothing was changed."
                : ExitCode == ExitCodes.Ok ? $"{Product.Name} was removed."
                : ExitCode == ExitCodes.Partial ? "Finished, but some things were kept (see above). You can run the uninstaller again later."
                : "Uninstall did not finish (see above).");
            _list.TopIndex = Math.Max(0, _list.Items.Count - 1);
            _close.Text = "Close";
            _close.Enabled = true;
            _go.Visible = false;
        })));
    }

    private void OnLine(LogLevel level, string text)
    {
        if (level == LogLevel.Detail || IsDisposed) return;
        BeginInvoke(new Action(() =>
        {
            var prefix = level switch { LogLevel.Warn => "! ", LogLevel.Error => "X ", LogLevel.Plan => "> ", _ => "  " };
            _list.Items.Add(prefix + text);
            _list.TopIndex = Math.Max(0, _list.Items.Count - 1);
        }));
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_running) e.Cancel = true;
        else if (!_finished) ExitCode = ExitCodes.Cancelled;
        base.OnFormClosing(e);
    }
}
