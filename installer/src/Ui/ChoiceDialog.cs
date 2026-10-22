// A question with our own button texts ("Replace" / "Skip", "Retry" / "Cancel"...), and the IInteraction the wizard uses.
using System;
using System.Drawing;
using System.Windows.Forms;
using CK3xBL.Setup.Engine;

namespace CK3xBL.Setup.Ui;

internal sealed class ChoiceDialog : Form
{
    public int Choice { get; private set; } = -1;

    private ChoiceDialog(string title, string text, string[] buttons)
    {
        Text = title;
        Font = Theme.BodyFont;
        Icon = AppIcon.WindowIcon();
        Mnemonics.Off(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var icon = new PictureBox
        {
            Image = SystemIcons.Warning.ToBitmap(),
            SizeMode = PictureBoxSizeMode.AutoSize,
            Margin = new Padding(0, 0, 12, 0),
        };
        var label = new Label { Text = text, AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 4, 0, 16) };
        layout.Controls.Add(icon, 0, 0);
        layout.Controls.Add(label, 1, 0);

        var bar = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };
        for (int i = buttons.Length - 1; i >= 0; i--)
        {
            int index = i;
            var b = new Button { Text = buttons[i], AutoSize = true, MinimumSize = new Size(88, 28), Margin = new Padding(8, 0, 0, 0) };
            b.Click += (_, _) => { Choice = index; DialogResult = DialogResult.OK; Close(); };
            bar.Controls.Add(b);
            if (i == 0) AcceptButton = b;
        }
        layout.Controls.Add(bar, 0, 1);
        layout.SetColumnSpan(bar, 2);
        Controls.Add(layout);
        Theme.NoMnemonics(this);
    }

    public static int Show(IWin32Window? owner, string title, string text, string[] buttons, int cancelChoice)
    {
        using var dlg = new ChoiceDialog(title, text, buttons);
        dlg.ShowDialog(owner);
        return dlg.Choice < 0 ? cancelChoice : dlg.Choice;
    }
}

/// <summary>Questions from the engine thread, shown as dialogs on the UI thread.</summary>
internal sealed class GuiInteraction : IInteraction
{
    private readonly Form _owner;

    public GuiInteraction(Form owner) { _owner = owner; }

    public bool IsSilent => false;

    public int Ask(string title, string text, string[] buttons, int silentChoice)
    {
        // Closing the dialog with the X counts as the last button (Skip / Cancel).
        int cancel = buttons.Length - 1;
        if (_owner.InvokeRequired) return (int)_owner.Invoke(new Func<int>(() => ChoiceDialog.Show(_owner, title, text, buttons, cancel)));
        return ChoiceDialog.Show(_owner, title, text, buttons, cancel);
    }
}

internal static class Theme
{
    public static readonly Font BodyFont = new("Segoe UI", 9f);
    public static readonly Font TitleFont = new("Segoe UI Semibold", 12f);
    public static readonly Font SmallFont = new("Segoe UI", 8.25f);
    public static readonly Color Muted = Color.FromArgb(96, 96, 96);
    public static readonly Color Good = Color.FromArgb(24, 120, 48);
    public static readonly Color Bad = Color.FromArgb(176, 32, 32);
    public static readonly Color Caution = Color.FromArgb(160, 100, 0);

    /// <summary>Labels show "&amp;" as it is (game names contain it) instead of as a keyboard shortcut marker.</summary>
    public static void NoMnemonics(Control root)
    {
        if (root is Label label) label.UseMnemonic = false;
        foreach (Control child in root.Controls) NoMnemonics(child);
    }
}
