// CK3xBL.Setup.Tests.exe
//   selftest [--keep] [--only <name>]   the installer self-test (temp sandboxes only)
//   pack --stub <CK3xBL-Setup.exe> --payload <staging dir> --version <v> --out <file> [--ck3 1.20.*] [--bannerlord v1.4.*]
//        [--forbid <regex>]...           builds the release setup (see ../PAYLOAD_LAYOUT.md)
//   icon --out <file.ico>                writes the app icon (assets/CK3xBL.ico is made with this)
//   screens --out <dir>                  renders every wizard page to PNG (sandbox data, nothing installed)
using System;
using System.Collections.Generic;
using System.IO;
using CK3xBL.Setup.Ui;

namespace CK3xBL.Setup.Tests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        var verb = args.Length > 0 && !args[0].StartsWith("-", StringComparison.Ordinal) ? args[0] : "selftest";
        var rest = new List<string>(args);
        if (rest.Count > 0 && rest[0] == verb) rest.RemoveAt(0);
        try
        {
            switch (verb)
            {
                case "selftest": return SelfTest.Run(rest);
                case "pack": return Pack(rest);
                case "icon": return Icon(rest);
                case "screens": return Screens.Capture(Need(rest, "--out"));
                default:
                    Console.Error.WriteLine($"unknown command '{verb}' (selftest, pack, icon)");
                    return 4;
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 2;
        }
    }

    private static string? Value(List<string> args, string name)
    {
        int i = args.IndexOf(name);
        if (i < 0) return null;
        if (i + 1 >= args.Count) throw new ArgumentException($"{name} needs a value");
        return args[i + 1];
    }

    private static string Need(List<string> args, string name) => Value(args, name) ?? throw new ArgumentException($"{name} is required");

    private static int Pack(List<string> args)
    {
        var req = new PackRequest
        {
            StubExe = Need(args, "--stub"),
            PayloadDir = Need(args, "--payload"),
            Version = Need(args, "--version"),
            OutputExe = Need(args, "--out"),
        };
        if (Value(args, "--ck3") is { } ck3) req.SupportsCk3 = ck3;
        if (Value(args, "--bannerlord") is { } bl) req.SupportsBannerlord = bl;
        for (int i = 0; i < args.Count - 1; i++)
            if (args[i] == "--forbid") req.Forbidden.Add(args[i + 1]);
        Packer.Pack(req, Console.WriteLine);
        return 0;
    }

    private static int Icon(List<string> args)
    {
        var output = Need(args, "--out");
        File.WriteAllBytes(output, AppIcon.BuildIco(new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }));
        Console.WriteLine($"wrote {output}");
        return 0;
    }
}
