// "Crowns & Blades": WinForms reads '&' in a label or button text as the mnemonic prefix (it underlines the next letter and drops the
// '&'). Setup has no keyboard mnemonics, so every label and button of a form shows its text as written, now and for controls added later.
using System.Windows.Forms;

namespace CK3xBL.Setup.Ui;

internal static class Mnemonics
{
    public static void Off(Control root)
    {
        switch (root)
        {
            case Label label: label.UseMnemonic = false; break;
            case ButtonBase button: button.UseMnemonic = false; break;
        }
        foreach (Control child in root.Controls) Off(child);
        root.ControlAdded += (_, e) => { if (e.Control is not null) Off(e.Control); };
    }
}
