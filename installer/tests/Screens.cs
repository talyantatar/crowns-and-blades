// "screens": renders each wizard page (and the uninstall window) to PNG for a visual check, without installing anything.
// The wizard is built against a sandbox with fake games and a fake payload; pages are switched the way its buttons do.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using CK3xBL.Setup.Engine;
using CK3xBL.Setup.Ui;
using CK3xBL.Setup.Util;

namespace CK3xBL.Setup.Tests;

internal static class Screens
{
    public static int Capture(string outDir)
    {
        Application.EnableVisualStyles();
        Directory.CreateDirectory(outDir);
        var work = Path.Combine(Sandbox.Base, "screens " + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(work);
        try
        {
            using var s = new Sandbox("screens", keep: false);
            var stub = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CK3xBL-Setup.exe");
            var exe = Path.Combine(work, "setup.exe");
            Packer.Pack(new PackRequest { StubExe = stub, PayloadDir = SelfTest.MakePayload(1, work), Version = "0.9.0-beta", OutputExe = exe }, _ => { });
            using var payload = Payload.OpenAppended(exe)!;
            var options = Options.Parse(new[] { $"/target-root={s.Root}" });
            var ctx = SetupContext.Create(options);
            using var log = new SetupLog(console: false);

            using (var form = new WizardForm(ctx, payload, options, log, previous: null))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-4000, -4000);
                form.Show();
                var go = typeof(WizardForm).GetMethod("Go", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var pageType = typeof(WizardForm).GetNestedType("Page", BindingFlags.NonPublic)!;
                foreach (var name in new[] { "Welcome", "License", "Folders", "Options" })
                {
                    go.Invoke(form, new[] { Enum.Parse(pageType, name) });
                    Save(form, Path.Combine(outDir, $"wizard-{name.ToLowerInvariant()}.png"));
                }
                var finish = typeof(WizardForm).GetMethod("ShowFinish", BindingFlags.NonPublic | BindingFlags.Instance)!;
                finish.Invoke(form, new object[]
                {
                    ExitCodes.Partial, new List<StepResult>
                    {
                        new() { Step = "Program files", Status = StepStatus.Done, Message = ctx.InstallDir + " (18 files written)" },
                        new() { Step = "CK3 mod", Status = StepStatus.Done, Message = ctx.ModDir + " (25 files written) and ck3xbl.mod" },
                        new() { Step = "CK3 mod list", Status = StepStatus.Done, Message = "added mod/ck3xbl.mod to dlc_load.json" },
                        new() { Step = "Bannerlord module", Status = StepStatus.Skipped, Message = ctx.ModuleDir + " is a link to D:\\dev\\CK3Bridge; left unchanged" },
                    },
                });
                Save(form, Path.Combine(outDir, "wizard-finish.png"));
                form.Hide();
            }

            var manifest = new InstallManifest { Version = "0.9.0-beta", InstallDir = ctx.InstallDir, SharedDir = ctx.SharedDir };
            using (var un = new UninstallForm(ctx, options, log, manifest))
            {
                un.StartPosition = FormStartPosition.Manual;
                un.Location = new Point(-4000, -4000);
                un.Show();
                Save(un, Path.Combine(outDir, "uninstall.png"));
                un.Hide();
            }
            Console.WriteLine($"wrote the screens to {outDir}");
            return 0;
        }
        finally
        {
            FileOps.DeleteTree(work);
            Sandbox.CleanRegistryParent();
        }
    }

    private static void Save(Form form, string path)
    {
        Application.DoEvents();
        using var bmp = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
        bmp.Save(path, ImageFormat.Png);
    }
}
