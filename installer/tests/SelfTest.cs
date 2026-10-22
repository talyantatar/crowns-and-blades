// The installer self-test. Builds fake payloads, packs them into real setup exes and runs those against temp sandboxes with
// fake CK3 / Bannerlord installs and a fake Documents folder (paths with spaces and non-ASCII letters). Plus unit tests of
// the pieces (JSON, dlc_load.json edits, versions, payload checks, leak gate).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using CK3xBL.Setup.Engine;
using CK3xBL.Setup.Util;

namespace CK3xBL.Setup.Tests;

internal sealed class TestFailure : Exception
{
    public TestFailure(string message) : base(message) { }
}

internal static class Expect
{
    public static void True(bool condition, string what)
    {
        if (!condition) throw new TestFailure(what);
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new TestFailure($"{what}: expected <{expected}>, got <{actual}>");
    }

    public static void Throws<TException>(Action action, string what) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new TestFailure($"{what}: expected {typeof(TException).Name}");
    }
}

internal static class SelfTest
{
    private static bool _keep;
    private static string _work = "";
    private static string _stub = "";
    private static string _setupV1 = "", _setupV2 = "";

    public static int Run(List<string> args)
    {
        _keep = args.Contains("--keep");
        int onlyAt = args.IndexOf("--only");
        string? only = onlyAt >= 0 && onlyAt + 1 < args.Count ? args[onlyAt + 1] : null;

        _stub = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CK3xBL-Setup.exe");
        if (!File.Exists(_stub)) { Console.Error.WriteLine($"missing {_stub} (build installer/tests, which builds the setup too)"); return 2; }

        _work = Path.Combine(Sandbox.Base, "work " + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(_work);
        var tests = new List<(string name, Action body)>
        {
            ("json_roundtrip", JsonRoundTrip),
            ("versions", Versions),
            ("modlist_edits", ModListEdits),
            ("payload_paths", PayloadPaths),
            ("pack_leak_gate", PackLeakGate),
            ("pack_and_tamper", PackAndTamper),
            ("context_sandbox", ContextSandbox),
            ("process_guard", ProcessGuardPaths),
            ("fresh_install", FreshInstall),
            ("dry_run", DryRun),
            ("repair_same_version", RepairSameVersion),
            ("upgrade", Upgrade),
            ("junction_refusal", JunctionRefusal),
            ("foreign_mod", ForeignMod),
            ("invalid_dlc_load", InvalidDlcLoad),
            ("uninstall_restores", UninstallRestores),
            ("uninstall_after_launcher_edit", UninstallAfterLauncherEdit),
            ("uninstall_installed_copy", UninstallInstalledCopy),
            ("uninstall_delete_user_data", UninstallDeleteUserData),
            ("modules_not_writable", ModulesNotWritable),
            ("bannerlord_running", BannerlordRunning),
            ("install_folder_rules", InstallFolderRules),
            ("install_dir_link", InstallDirLink),
            ("foreign_app_files", ForeignAppFiles),
            ("upgrade_step_failure", UpgradeStepFailure),
            ("uninstall_step_failure", UninstallStepFailure),
            ("uninstall_keeps_unrestored_backup", UninstallKeepsUnrestoredBackup),
            ("uninstall_incomplete_keeps_moved_aside", UninstallIncompleteKeepsMovedAside),
            ("uninstall_run_key", UninstallRunKey),
            ("documents_moved", DocumentsMoved),
            ("elevated_helper_checks", ElevatedHelperChecks),
        };

        int failed = 0, ran = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            _setupV1 = Path.Combine(_work, "CK3xBL-Setup-0.9.0-beta.exe");
            _setupV2 = Path.Combine(_work, "CK3xBL-Setup-0.9.1-beta.exe");
            Packer.Pack(new PackRequest { StubExe = _stub, PayloadDir = MakePayload(1), Version = "0.9.0-beta", OutputExe = _setupV1 }, _ => { });
            Packer.Pack(new PackRequest { StubExe = _stub, PayloadDir = MakePayload(2), Version = "0.9.1-beta", OutputExe = _setupV2 }, _ => { });

            foreach (var (name, body) in tests)
            {
                if (only is not null && !name.Contains(only)) continue;
                ran++;
                var t = Stopwatch.StartNew();
                try
                {
                    body();
                    Console.WriteLine($"PASS {name} ({t.ElapsedMilliseconds} ms)");
                }
                catch (Exception e) when (e is TestFailure or IOException or UnauthorizedAccessException or InvalidDataException
                                              or FormatException or ArgumentException or InvalidOperationException or TimeoutException)
                {
                    failed++;
                    Console.WriteLine($"FAIL {name}: {e.Message}");
                    if (e is not TestFailure) Console.WriteLine(e);
                }
            }
        }
        finally
        {
            if (!_keep) FileOps.DeleteTree(_work);
            Sandbox.CleanRegistryParent();
            if (!_keep)
            {
                try { FileOps.DeleteEmptyDirsUpward(Path.Combine(Sandbox.Base, "logs"), Path.GetTempPath()); }
                catch (IOException) { }
            }
        }
        Console.WriteLine($"{ran - failed}/{ran} passed in {sw.Elapsed.TotalSeconds:0.0} s" + (_keep ? $" (kept under {Sandbox.Base})" : ""));
        return failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Fake payloads

    private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };

    /// <summary>A staging folder in the release layout. Version 2 drops a data file and a GUI file, changes the exe and the
    /// mod, and adds a file.</summary>
    internal static string MakePayload(int version, string? work = null)
    {
        var dir = Path.Combine(work ?? _work, $"payload-v{version}");
        void Put(string rel, string text, bool bom = false)
        {
            var path = Path.Combine(dir, rel.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var body = FileOps.Utf8NoBom.GetBytes(text);
            File.WriteAllBytes(path, bom ? Bom.Concat(body).ToArray() : body);
        }
        Put("app/CK3xBL.exe", $"MZ fake bridge v{version}");
        Put("app/LICENSE.txt", "MIT License\n\nCopyright (c) 2026 graff0n\n");
        Put("app/THIRD-PARTY-NOTICES.txt", ".NET runtime: MIT\n");
        Put("app/data/ck3_maa_index.json", $"{{\"v\":{version}}}");
        Put("app/data/terrain.json", "{\"plains\":1}");
        if (version == 1) Put("app/data/dropped_in_v2.json", "{}");
        if (version == 1) Put("app/data/user_edits_this.json", "{\"a\":1}");
        if (version == 2) Put("app/data/new_in_v2.json", "{}");
        Put("ck3mod/ck3xbl/descriptor.mod",
            $"version=\"0.9.{version - 1}-beta\"\ntags={{\n\t\"Gameplay\"\n}}\nname=\"Crowns & Blades Battles\"\nsupported_version=\"1.20.*\"\n");
        Put("ck3mod/ck3xbl/common/on_action/ck3xbl_on_actions.txt", $"# v{version}\non_game_start = {{ }}\n", bom: true);
        Put("ck3mod/ck3xbl/localization/english/ck3xbl_l_english.yml", "l_english:\n ck3xbl_title:0 \"Battle\"\n", bom: true);
        Put("blmodule/CK3Bridge/SubModule.xml", $"<Module><Id value=\"CK3Bridge\"/><Version value=\"v0.9.{version - 1}\"/></Module>\n");
        Put("blmodule/CK3Bridge/bin/Win64_Shipping_Client/CK3Bridge.dll", $"MZ fake module v{version}");
        Put("blmodule/CK3Bridge/GUI/Prefabs/CK3TitleCard.xml", "<Prefab/>\n");
        if (version == 1) Put("blmodule/CK3Bridge/GUI/Prefabs/CK3Old.xml", "<Prefab/>\n");
        return dir;
    }

    private static string StagedFile(int version, string rel) => Path.Combine(_work, $"payload-v{version}", rel.Replace('/', '\\'));

    /// <summary>Every payload file is at its destination with the right bytes.</summary>
    private static void ExpectInstalled(Sandbox s, int version)
    {
        var dir = Path.Combine(_work, $"payload-v{version}");
        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = FileOps.Relative(dir, f);
            string dest =
                rel.StartsWith(@"app\") ? Path.Combine(s.InstallDir, rel.Substring(4)) :
                rel.StartsWith(@"ck3mod\ck3xbl\") ? Path.Combine(s.ModDir, rel.Substring(@"ck3mod\ck3xbl\".Length)) :
                Path.Combine(s.ModuleDir, rel.Substring(@"blmodule\CK3Bridge\".Length));
            Expect.True(File.Exists(dest), $"{dest} installed");
            Expect.Equal(FileOps.Sha256File(f), FileOps.Sha256File(dest), $"{dest} content");
        }
    }

    private static void ExpectNoErrors(RunResult r)
    {
        var errors = Sandbox.Lines(r.Log).Where(l => l.Contains(" ERROR ")).ToList();
        Expect.True(errors.Count == 0, "no errors in the log:\n" + string.Join("\n", errors));
    }

    private static void ExpectExit(int expected, RunResult r)
    {
        if (r.ExitCode != expected) throw new TestFailure($"exit code: expected {expected}, got {r}");
    }

    private static string ReadText(string path) => FileOps.ReadUtf8(File.ReadAllBytes(path), out _);

    // ------------------------------------------------------------------------------------------------------------------
    // Unit tests

    private static void JsonRoundTrip()
    {
        const string text = "{\"enabled_mods\":[\"mod/a.mod\",\"mod/\\u00e9t\\u00e9.mod\"],\"disabled_dlcs\":[],\"n\":1.50,\"e\":-2E+3,\"t\":true,\"z\":null,\"o\":{\"k\":\"a\\\"b\\\\c\\n\"}}";
        var parsed = Json.Parse(text);
        var written = Json.Write(parsed, indented: false);
        Expect.Equal("{\"enabled_mods\":[\"mod/a.mod\",\"mod/\u00e9t\u00e9.mod\"],\"disabled_dlcs\":[],\"n\":1.50,\"e\":-2E+3,\"t\":true,\"z\":null,\"o\":{\"k\":\"a\\\"b\\\\c\\n\"}}",
            written, "compact round trip keeps order and number text");
        Expect.Equal(written, Json.Write(Json.Parse("\uFEFF" + written), indented: false), "BOM ignored");
        Expect.Equal(written, Json.Write(Json.Parse(Json.Write(parsed, indented: true)), indented: false), "indented round trip");
        foreach (var bad in new[] { "", "{", "{\"a\":}", "[1,]", "{\"a\":1}x", "\"\u0001\"", "nul", "01x" })
            Expect.Throws<FormatException>(() => Json.Parse(bad), $"invalid JSON '{bad}'");
    }

    private static void Versions()
    {
        Expect.True(ReleaseVersion.Compare("0.9.0-beta", "0.9.0") < 0, "beta < release");
        Expect.True(ReleaseVersion.Compare("0.9.1-beta", "0.9.0") > 0, "0.9.1-beta > 0.9.0");
        Expect.True(ReleaseVersion.Compare("0.10.0", "0.9.9") > 0, "numeric compare");
        Expect.True(ReleaseVersion.Compare("v1.0.0", "1.0.0") == 0, "leading v");
        Expect.True(ReleaseVersion.Compare("0.9.0-beta", "0.9.0-rc") < 0, "beta < rc");
        Expect.True(GameDetector.VersionMatches("1.20.0.4", "1.20.*"), "1.20.0.4 fits 1.20.*");
        Expect.True(!GameDetector.VersionMatches("1.21.0", "1.20.*"), "1.21.0 does not fit 1.20.*");
        Expect.True(GameDetector.VersionMatches("v1.4.8", "v1.4.*"), "v1.4.8 fits v1.4.*");
        Expect.True(!GameDetector.VersionMatches("v1.5.0", "v1.4.*"), "v1.5.0 does not fit");
        var libs = GameDetector.ParseLibraryFolders("\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"E:\\\\SteamLibrary\"\n\t}\n}");
        Expect.Equal(2, libs.Count, "two libraries");
        Expect.Equal(@"E:\SteamLibrary", libs[1], "vdf unescaped");
        Expect.Equal("Mount & Blade II Bannerlord", GameDetector.ParseInstallDir("\"AppState\"\n{\n\t\"installdir\"\t\t\"Mount & Blade II Bannerlord\"\n}"), "acf installdir");
    }

    private static void ModListEdits()
    {
        const string user = @"C:\Docs\Paradox Interactive\Crusader Kings III";
        byte[] Bytes(string s, bool bom = false) => FileOps.EncodeUtf8(s, bom);
        string Text(byte[]? b) => FileOps.ReadUtf8(b!, out _);

        Expect.True(Ck3ModList.TryEdit(Bytes(Sandbox.OriginalDlcLoad), user, add: true, out var added, out bool present, out _), "add parses");
        Expect.True(!present, "not present before");
        Expect.Equal("{\"enabled_mods\":[\"mod/ugc_1.mod\",\"mod/ck3xbl.mod\"],\"disabled_dlcs\":[]}", Text(added), "entry appended, rest kept");

        Expect.True(Ck3ModList.TryEdit(added!, user, add: true, out var again, out present, out _), "re-add parses");
        Expect.True(present && again is null, "idempotent: already present, nothing to write");

        Expect.True(Ck3ModList.TryEdit(added!, user, add: false, out var removed, out present, out _), "remove parses");
        Expect.Equal(Sandbox.OriginalDlcLoad, Text(removed), "remove gives the original back");

        var withBom = Bytes("{\"enabled_mods\":[\"mod\\\\CK3XBL.mod\",\"" + (user + @"\mod\ck3xbl.mod").Replace(@"\", @"\\") + "\"],\"x\":1}", bom: true);
        Expect.True(Ck3ModList.TryEdit(withBom, user, add: false, out var cleaned, out present, out _), "BOM input parses");
        Expect.True(present, "backslash, upper-case and absolute entries are recognised");
        Expect.True(cleaned![0] == 0xEF && cleaned[1] == 0xBB, "BOM kept");
        Expect.Equal("{\"enabled_mods\":[],\"x\":1}", Text(cleaned), "all our entries removed, other keys kept");

        Expect.True(Ck3ModList.TryEdit(Bytes("{\"disabled_dlcs\":[]}"), user, add: true, out var created, out _, out _), "missing list");
        Expect.Equal("{\"disabled_dlcs\":[],\"enabled_mods\":[\"mod/ck3xbl.mod\"]}", Text(created), "missing list is added");

        Expect.True(!Ck3ModList.TryEdit(Bytes("{\"enabled_mods\":"), user, add: true, out _, out _, out _), "broken JSON refused");
        Expect.True(!Ck3ModList.TryEdit(Bytes("{\"enabled_mods\":\"x\"}"), user, add: true, out _, out _, out _), "wrong type refused");
    }

    private static void PayloadPaths()
    {
        foreach (var good in new[] { "app/CK3xBL.exe", "app/data/x.json", "ck3mod/ck3xbl/descriptor.mod", "blmodule/CK3Bridge/SubModule.xml" })
            PayloadManifest.CheckPath(good);
        foreach (var bad in new[]
                 {
                     "../x", "app/../../evil.dll", "app\\x", "/app/x", "C:/x", "app//x", "other/x", "app", "ck3mod/other/x",
                     "blmodule/Native/x", "app/./x", "app/x.", "app/ x",
                 })
            Expect.Throws<InvalidDataException>(() => PayloadManifest.CheckPath(bad), $"unsafe path '{bad}'");
    }

    private static void PackLeakGate()
    {
        var dir = MakePayload(1);
        var copy = Path.Combine(_work, "leaky");
        FileOps.CopyTree(dir, copy);
        File.WriteAllText(Path.Combine(copy, "app", "data", "meta.json"), "{\"generator\":\"C:\\\\Users\\\\someone\\\\tools\\\\x.mjs\"}");
        File.WriteAllBytes(Path.Combine(copy, "app", "CK3xBL.pdb"), new byte[] { 1 });
        File.WriteAllText(Path.Combine(copy, "app", "config.json"), "{}");
        var out1 = Path.Combine(_work, "leaky.exe");
        try
        {
            Packer.Pack(new PackRequest { StubExe = _stub, PayloadDir = copy, Version = "0.9.0-beta", OutputExe = out1, Forbidden = { "someone" } }, _ => { });
            throw new TestFailure("a leaky payload was packed");
        }
        catch (InvalidDataException e)
        {
            Expect.True(e.Message.Contains("user folder"), "user folder path reported");
            Expect.True(e.Message.Contains("CK3xBL.pdb"), "pdb reported");
            Expect.True(e.Message.Contains("app/config.json"), "config.json reported");
            Expect.True(e.Message.Contains("forbidden pattern"), "custom pattern reported");
        }
        Expect.True(!File.Exists(out1), "nothing written");

        // UTF-16 text inside a binary is found too
        var copy2 = Path.Combine(_work, "leaky2");
        FileOps.CopyTree(dir, copy2);
        File.WriteAllBytes(Path.Combine(copy2, "app", "CK3xBL.exe"), Encoding.Unicode.GetBytes("MZ....D:\\Users\\dev\\src\\x.pdb"));
        Expect.Throws<InvalidDataException>(() => Packer.Pack(
            new PackRequest { StubExe = _stub, PayloadDir = copy2, Version = "0.9.0-beta", OutputExe = out1 }, _ => { }), "UTF-16 user path");

        var missing = Path.Combine(_work, "missing");
        FileOps.CopyTree(dir, missing);
        File.Delete(Path.Combine(missing, "blmodule", "CK3Bridge", "SubModule.xml"));
        Expect.Throws<InvalidDataException>(() => Packer.Pack(
            new PackRequest { StubExe = _stub, PayloadDir = missing, Version = "0.9.0-beta", OutputExe = out1 }, _ => { }), "required file missing");
    }

    private static void PackAndTamper()
    {
        using (var p = Payload.OpenAppended(_setupV1) ?? throw new TestFailure("no payload in the packed exe"))
        {
            p.VerifyAll();
            Expect.Equal("0.9.0-beta", p.Manifest.Version, "manifest version");
            Expect.Equal("1.20.*", p.Manifest.SupportsCk3, "supports ck3");
        }
        Expect.True(Payload.OpenAppended(_stub) is null, "the bare stub has no payload");
        var stub = Payload.ReadStub(_setupV1);
        Expect.Equal(FileOps.Sha256File(_stub), FileOps.Sha256(stub), "the stub inside a packed exe is the bare setup");

        // Flip one byte inside a stored file: the hash check must catch it (zip CRC or SHA-256).
        // Positions: inside manifest.json's data, and the first data byte of two stored files (found from their local headers;
        // a flip in a local header's name or time would be harmless, since readers use the central directory).
        long zipStart = Payload.StubLength(_setupV1);
        var image = File.ReadAllBytes(_setupV1);
        long DataOf(string name)
        {
            var needle = Encoding.UTF8.GetBytes(name);
            for (long i = zipStart + 30; i < image.Length - needle.Length; i++)
            {
                if (!needle.Select((b, k) => image[i + k] == b).All(x => x)) continue;
                long header = i - 30;
                if (image[header] != 'P' || image[header + 1] != 'K' || image[header + 2] != 3 || image[header + 3] != 4) continue;
                int extra = BitConverter.ToUInt16(image, (int)header + 28);
                return i + needle.Length + extra - zipStart;
            }
            throw new TestFailure($"{name} not found in the zip");
        }
        foreach (var at in new[] { 60L, DataOf("app/data/terrain.json"), DataOf(PayloadManifest.ModulePrefix + "bin/Win64_Shipping_Client/CK3Bridge.dll") })
        {
            var tampered = Path.Combine(_work, $"tampered-{at}.exe");
            var bytes = File.ReadAllBytes(_setupV1);
            bytes[zipStart + at] ^= 0x5A;
            File.WriteAllBytes(tampered, bytes);
            bool caught = false;
            try
            {
                using var p = Payload.OpenAppended(tampered);
                p?.VerifyAll();
            }
            catch (Exception e) when (e is InvalidDataException or IOException) { caught = true; }
            Expect.True(caught, $"a payload damaged at zip offset {at} is refused");
        }

        // A damaged trailer is refused, not ignored.
        var cut = Path.Combine(_work, "cut.exe");
        var shortBytes = File.ReadAllBytes(_setupV1);
        shortBytes[shortBytes.Length - 20] ^= 0x01;
        File.WriteAllBytes(cut, shortBytes);
        Expect.Throws<InvalidDataException>(() => Payload.OpenAppended(cut), "damaged trailer");

        // A signature added after packing (certificate table at the end, 8-byte aligned) does not hide the payload.
        var signed = Path.Combine(_work, "signed.exe");
        var s = File.ReadAllBytes(_setupV1).ToList();
        while (s.Count % 8 != 0) s.Add(0);
        int certStart = s.Count;
        s.AddRange(new byte[64]);
        var arr = s.ToArray();
        int pe = BitConverter.ToInt32(arr, 0x3C);
        int optional = pe + 24;
        int dataDirs = BitConverter.ToUInt16(arr, optional) == 0x20B ? optional + 112 : optional + 96;
        BitConverter.GetBytes(certStart).CopyTo(arr, dataDirs + 32);
        BitConverter.GetBytes(64).CopyTo(arr, dataDirs + 36);
        File.WriteAllBytes(signed, arr);
        using (var p = Payload.OpenAppended(signed) ?? throw new TestFailure("payload hidden by a signature"))
            p.VerifyAll();
        var stubOfSigned = Payload.ReadStub(signed);
        Expect.Equal(FileOps.Sha256File(_stub), FileOps.Sha256(stubOfSigned), "the stub of a signed exe has no signature entry");
    }

    private static void ContextSandbox()
    {
        var root = Path.Combine(_work, "ctx " + Sandbox.OddName);
        var ctx = SetupContext.Create(Options.Parse(new[] { $"/target-root={root}" }));
        Expect.True(ctx.Sandbox && !ctx.DetectionAllowed, "sandbox without detection");
        foreach (var path in new[] { ctx.InstallDir, ctx.SharedDir, ctx.Ck3UserDir, ctx.Ck3Dir!, ctx.BlDir!, ctx.StartMenuPrograms, ctx.Desktop, ctx.TempDir })
            Expect.True(FileOps.IsUnder(path, root), $"{path} is inside the sandbox");
        Expect.True(ctx.RegistryPrefix.StartsWith(@"Software\CK3xBL-Sandbox\"), "registry stand-in");
        var single = SetupContext.Create(Options.Parse(new[] { $"/bl-dir={root}" }));
        Expect.True(!single.DetectionAllowed && single.Ck3Dir is null, "any override switches detection off");
        Expect.Throws<ArgumentException>(() => Options.Parse(new[] { "/bogus" }), "unknown option");
        Expect.Throws<ArgumentException>(() => Options.Parse(new[] { "/target-root" }), "option without value");
        var quoted = Options.Quote(new[] { "/a=C:\\x y\\", "plain", "q\"q" });
        Expect.Equal("\"/a=C:\\x y\\\\\" plain \"q\\\"q\"", quoted, "argument quoting");
    }

    private static void ProcessGuardPaths()
    {
        var dir = Path.Combine(_work, "proc " + Sandbox.OddName);
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "pingcopy.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
        using var p = Process.Start(new ProcessStartInfo(exe, "-n 30 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            var found = ProcessGuard.RunningFrom(dir);
            Expect.True(found.Any(x => x.Id == p.Id), "a process is found by its folder");
            found.ForEach(x => x.Dispose());
            var named = ProcessGuard.RunningFrom(dir, "pingcopy");
            Expect.Equal(1, named.Count, "and by name inside the folder");
            named.ForEach(x => x.Dispose());
            Expect.Equal(0, ProcessGuard.RunningFrom(Path.Combine(_work, "elsewhere")).Count, "nothing elsewhere");
        }
        finally
        {
            if (!p.HasExited) p.Kill();
            p.WaitForExit();
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // End-to-end runs of the packed setup

    private static void FreshInstall()
    {
        using var s = new Sandbox("fresh", _keep);
        var r = s.Run(_setupV1);
        ExpectExit(0, r);
        ExpectNoErrors(r);
        ExpectInstalled(s, 1);

        var uninstaller = Path.Combine(s.InstallDir, "CK3xBL-Uninstall.exe");
        Expect.True(File.Exists(uninstaller), "uninstaller copied");
        Expect.True(Payload.OpenAppended(uninstaller) is null, "the uninstaller carries no payload");
        Expect.Equal(FileOps.Sha256File(_stub), FileOps.Sha256File(uninstaller), "the uninstaller is the bare setup");

        var descBytes = File.ReadAllBytes(s.Descriptor);
        Expect.True(!(descBytes[0] == 0xEF && descBytes[1] == 0xBB), "ck3xbl.mod has no BOM");
        var desc = Encoding.UTF8.GetString(descBytes);
        var expectedPath = FileOps.Full(s.ModDir).Replace('\\', '/');
        Expect.True(desc.Contains($"path=\"{expectedPath}\"\n"), $"ck3xbl.mod points at the mod with / and non-ASCII intact:\n{desc}");
        Expect.True(desc.StartsWith("version=\"0.9.0-beta\""), "descriptor body copied");
        Expect.Equal(1, Regex.Matches(desc, "^path=", RegexOptions.Multiline).Count, "one path=");

        Expect.Equal("{\"enabled_mods\":[\"mod/ugc_1.mod\",\"mod/ck3xbl.mod\"],\"disabled_dlcs\":[]}", ReadText(s.DlcLoad), "dlc_load.json edited");
        Expect.Equal(Sandbox.OriginalDlcLoad, ReadText(s.DlcLoadBackup), "dlc_load.json backed up");

        var config = ReadText(Path.Combine(s.SharedDir, "config.json"));
        Expect.True(config.Contains("\"gameDir\"") && config.Contains("Bannerlord"), "config seeded with the game folders");
        var parsedConfig = Json.Parse(config) as JsonObject;
        Expect.Equal(FileOps.Full(s.BlDir), parsedConfig?.GetObject("bannerlord")?.GetString("gameDir"), "seeded Bannerlord folder");

        Expect.True(File.Exists(s.StartMenuShortcut), "Start menu shortcut");
        Expect.True(!File.Exists(s.DesktopShortcut), "no desktop shortcut by default");
        Expect.Equal("0.9.0-beta", s.RegistryValue(Product.UninstallKey, "DisplayVersion") as string, "Add/Remove Programs entry");
        Expect.True((s.RegistryValue(Product.UninstallKey, "UninstallString") as string ?? "").Contains("CK3xBL-Uninstall.exe\" /uninstall"), "uninstall command");

        var m = InstallManifest.Parse(File.ReadAllText(s.Manifest));
        Expect.Equal("0.9.0-beta", m.Version, "manifest version");
        Expect.True(m.DlcLoad is { Added: true, CreatedFile: false } && m.DlcLoad.Backup is not null, "manifest records the dlc_load edit");
        Expect.True(m.Files.Count(f => f.Target == Targets.BlModule) == 4 && m.Files.Count(f => f.Target == Targets.Ck3Mod) == 3, "manifest lists the files");
        Expect.True(Directory.GetFiles(s.Root, "*.ck3xbl_tmp", SearchOption.AllDirectories).Length == 0, "no temp files left");
    }

    private static void DryRun()
    {
        using var s = new Sandbox("dry", _keep);
        var before = s.Snapshot();
        var r = s.Run(_setupV1, "/dry-run");
        ExpectExit(0, r);
        var after = s.Snapshot();
        Expect.True(before.SequenceEqual(after), "a dry run changes nothing:\n" +
                                                  string.Join("\n", after.Keys.Except(before.Keys).Concat(before.Keys.Except(after.Keys))));
        Expect.True(!s.RegistryKeyExists(Product.UninstallKey), "no registry key");
        Expect.True(r.Output.Contains("PLAN") && r.Output.Contains("ck3xbl.mod"), "the plan is printed");
    }

    private static void RepairSameVersion()
    {
        using var s = new Sandbox("repair", _keep);
        ExpectExit(0, s.Run(_setupV1));
        var backup = File.ReadAllBytes(s.DlcLoadBackup);
        File.Delete(Path.Combine(s.ModuleDir, "GUI", "Prefabs", "CK3TitleCard.xml"));   // a damaged install
        var r = s.Run(_setupV1);
        ExpectExit(0, r);
        ExpectInstalled(s, 1);
        Expect.Equal("{\"enabled_mods\":[\"mod/ugc_1.mod\",\"mod/ck3xbl.mod\"],\"disabled_dlcs\":[]}", ReadText(s.DlcLoad), "no duplicate entry");
        Expect.True(backup.SequenceEqual(File.ReadAllBytes(s.DlcLoadBackup)), "backup unchanged");
        Expect.True(r.Log.Contains("repairing 0.9.0-beta"), "reported as a repair");
    }

    private static void Upgrade()
    {
        using var s = new Sandbox("upgrade", _keep);
        ExpectExit(0, s.Run(_setupV1, "/desktop-shortcut"));
        var edited = Path.Combine(s.InstallDir, "data", "user_edits_this.json");
        File.WriteAllText(edited, "{\"a\":2}");
        var r = s.Run(_setupV2);
        ExpectExit(0, r);
        ExpectInstalled(s, 2);
        Expect.True(!File.Exists(Path.Combine(s.InstallDir, "data", "dropped_in_v2.json")), "a file dropped by v2 is removed");
        Expect.True(!File.Exists(Path.Combine(s.ModuleDir, "GUI", "Prefabs", "CK3Old.xml")), "a module file dropped by v2 is removed");
        Expect.True(File.Exists(edited), "a dropped file the player changed is kept");
        Expect.True(r.Log.Contains("upgrading from 0.9.0-beta"), "reported as an upgrade");
        Expect.True(!File.Exists(s.DesktopShortcut), "the desktop shortcut follows the new choice (off)");
        Expect.Equal("0.9.1-beta", InstallManifest.Parse(File.ReadAllText(s.Manifest)).Version, "manifest updated");
        Expect.Equal(Sandbox.OriginalDlcLoad, ReadText(s.DlcLoadBackup), "the backup still holds the original");

        // And back: a downgrade needs /allow-downgrade.
        var down = s.Run(_setupV1);
        ExpectExit(2, down);
        Expect.Equal("0.9.1-beta", InstallManifest.Parse(File.ReadAllText(s.Manifest)).Version, "a refused downgrade changes nothing");
        // v1 brings user_edits_this.json back, but the player's edited copy is no longer ours (v2 dropped it): moved aside, not overwritten.
        var back = s.Run(_setupV1, "/allow-downgrade");
        ExpectExit(1, back);
        ExpectInstalled(s, 1);
        var m = InstallManifest.Parse(File.ReadAllText(s.Manifest));
        var aside = m.MovedAside.SingleOrDefault(a => FileOps.PathEquals(a.Original, edited)) ?? throw new TestFailure("the edited file is recorded as moved aside");
        Expect.Equal("{\"a\":2}", File.ReadAllText(aside.Backup), "the player's edited copy is in setup-backups");
        ExpectExit(0, s.Run(_setupV1, "/uninstall"));
        Expect.Equal("{\"a\":2}", File.ReadAllText(edited), "uninstall puts the player's copy back");
    }

    private static void MakeJunction(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var psi = new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (!FileOps.IsReparsePoint(link)) throw new TestFailure("could not create a junction for the test");
    }

    private static void JunctionRefusal()
    {
        using var s = new Sandbox("junction", _keep);
        var dev = Path.Combine(s.Root, "dev checkout", "CK3Bridge");
        Sandbox.Write(Path.Combine(dev, "SubModule.xml"), "<Module><Id value=\"CK3Bridge\"/><!-- dev --></Module>");
        Sandbox.Write(Path.Combine(dev, "bin", "Win64_Shipping_Client", "CK3Bridge.dll"), "dev build");
        MakeJunction(s.ModuleDir, dev);
        string DevHash() => string.Join(",", Directory.GetFiles(dev, "*", SearchOption.AllDirectories).OrderBy(f => f).Select(FileOps.Sha256File));
        var devBefore = DevHash();

        var r = s.Run(_setupV1);
        ExpectExit(1, r);
        Expect.True(FileOps.IsReparsePoint(s.ModuleDir), "the junction is left alone");
        Expect.Equal(devBefore, DevHash(), "nothing written through the junction");
        Expect.True(r.Log.Contains("is a link to"), "reported");
        Expect.True(File.Exists(s.Descriptor), "the other steps still ran");

        // Uninstall must not delete through it either.
        ExpectExit(0, s.Run(_setupV1, "/uninstall"));
        Expect.Equal(devBefore, DevHash(), "uninstall does not touch the linked checkout");
        Expect.True(FileOps.IsReparsePoint(s.ModuleDir), "the junction survives uninstall");

        // Asked to remove the link: only the link goes, the checkout stays.
        var r2 = s.Run(_setupV1, "/remove-module-link");
        ExpectExit(0, r2);
        Expect.True(!FileOps.IsReparsePoint(s.ModuleDir) && File.Exists(Path.Combine(s.ModuleDir, "SubModule.xml")), "release module installed");
        Expect.Equal(devBefore, DevHash(), "the checkout is untouched");
        ExpectInstalled(s, 1);
    }

    private static void ForeignMod()
    {
        using var s = new Sandbox("foreign", _keep);
        const string theirs = "name=\"Crowns & Blades Battles\"\npath=\"D:/projects/ck3xbl-dev/ck3_mod/ck3xbl\"\n";
        Sandbox.Write(s.Descriptor, theirs);
        var r = s.Run(_setupV1);
        ExpectExit(1, r);
        Expect.Equal(theirs, File.ReadAllText(s.Descriptor), "a foreign ck3xbl.mod is kept without /replace-existing");
        Expect.True(!Directory.Exists(s.ModDir), "nothing written for the mod");

        var r2 = s.Run(_setupV1, "/replace-existing");
        ExpectExit(0, r2);
        Expect.True(File.ReadAllText(s.Descriptor).Contains(FileOps.Full(s.ModDir).Replace('\\', '/')), "replaced");
        var m = InstallManifest.Parse(File.ReadAllText(s.Manifest));
        Expect.Equal(1, m.MovedAside.Count, "the old descriptor was moved aside");
        Expect.Equal(theirs, File.ReadAllText(m.MovedAside[0].Backup), "backup holds the old descriptor");

        ExpectExit(0, s.Run(_setupV1, "/uninstall"));
        Expect.Equal(theirs, File.ReadAllText(s.Descriptor), "uninstall puts the old descriptor back");
    }

    private static void InvalidDlcLoad()
    {
        using var s = new Sandbox("badjson", _keep);
        const string broken = "{\"enabled_mods\":[\"mod/ugc_1.mod\",";
        Sandbox.Write(s.DlcLoad, broken);
        var r = s.Run(_setupV1);
        ExpectExit(1, r);
        Expect.Equal(broken, File.ReadAllText(s.DlcLoad), "invalid dlc_load.json is not overwritten");
        Expect.True(!File.Exists(s.DlcLoadBackup), "no backup made");
        Expect.True(r.Log.Contains("not valid JSON"), "reported");
        ExpectInstalled(s, 1);
    }

    private static void UninstallRestores()
    {
        using var s = new Sandbox("uninstall", _keep);
        var before = s.Snapshot();
        ExpectExit(0, s.Run(_setupV1, "/desktop-shortcut"));
        var r = s.Run(_setupV1, "/uninstall");
        ExpectExit(0, r);
        ExpectNoErrors(r);
        Expect.Equal(Sandbox.OriginalDlcLoad, ReadText(s.DlcLoad), "dlc_load.json restored");
        Expect.True(!File.Exists(s.DlcLoadBackup), "backup put back");
        Expect.True(!Directory.Exists(s.InstallDir), "install folder removed");
        Expect.True(!Directory.Exists(s.ModDir) && !File.Exists(s.Descriptor), "CK3 mod removed");
        Expect.True(!Directory.Exists(s.ModuleDir), "Bannerlord module removed");
        Expect.True(!File.Exists(s.StartMenuShortcut) && !File.Exists(s.DesktopShortcut), "shortcuts removed");
        Expect.True(!s.RegistryKeyExists(Product.UninstallKey), "registry entry removed");
        using (var parent = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\CK3xBL-Sandbox"))
            Expect.True(parent is null, "the sandbox registry stand-in is removed completely");
        Expect.True(File.Exists(Path.Combine(s.SharedDir, "config.json")), "settings kept by default");

        // Apart from the kept settings (and the setup log), the sandbox is as it was.
        var after = s.Snapshot();
        bool Allowed(string k) =>
            k.StartsWith(@"LocalAppData\ck3xbl\", StringComparison.OrdinalIgnoreCase) ||      // settings and setup.log, kept
            k.StartsWith(@"Temp\", StringComparison.OrdinalIgnoreCase) ||                      // the uninstall log
            k is @"LocalAppData\" or @"LocalAppData\Programs\" ||                               // exist on every real PC
            (k.EndsWith(@"\") && (k.StartsWith(@"RoamingAppData\", StringComparison.OrdinalIgnoreCase) ||
                                  k.StartsWith(@"Desktop\", StringComparison.OrdinalIgnoreCase)));   // Start menu folders, Desktop
        var extra = after.Keys.Except(before.Keys).Where(k => !Allowed(k)).ToList();
        Expect.True(extra.Count == 0, "left behind:\n" + string.Join("\n", extra));
        var changed = before.Keys.Where(k => !after.TryGetValue(k, out var h) || h != before[k]).ToList();
        Expect.True(changed.Count == 0, "changed or missing:\n" + string.Join("\n", changed));
        Expect.True(Directory.GetFiles(Path.Combine(s.Root, "RoamingAppData"), "*", SearchOption.AllDirectories).Length == 0, "nothing left in AppData");
    }

    private static void UninstallAfterLauncherEdit()
    {
        using var s = new Sandbox("launcher", _keep);
        ExpectExit(0, s.Run(_setupV1));
        // The Paradox Launcher rewrote the list since: a new mod, and our entry kept by the player's playset.
        const string now = "{\"enabled_mods\":[\"mod/ugc_2.mod\",\"mod/ck3xbl.mod\",\"mod/ugc_1.mod\"],\"disabled_dlcs\":[\"dlc001\"]}";
        Sandbox.Write(s.DlcLoad, now);
        var r = s.Run(_setupV1, "/uninstall");
        ExpectExit(0, r);
        Expect.Equal("{\"enabled_mods\":[\"mod/ugc_2.mod\",\"mod/ugc_1.mod\"],\"disabled_dlcs\":[\"dlc001\"]}", ReadText(s.DlcLoad),
            "only our entry is removed from a changed list");
        Expect.True(File.Exists(s.DlcLoadBackup), "the stale backup is kept, not restored");
    }

    private static void UninstallInstalledCopy()
    {
        using var s = new Sandbox("selfcopy", _keep);
        ExpectExit(0, s.Run(_setupV1));
        var uninstaller = Path.Combine(s.InstallDir, "CK3xBL-Uninstall.exe");
        var r = s.Run(uninstaller, "/uninstall");
        ExpectExit(0, r);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Directory.Exists(s.InstallDir) && DateTime.UtcNow < deadline) System.Threading.Thread.Sleep(100);
        Expect.True(!Directory.Exists(s.InstallDir), "the install folder is gone, the uninstaller included");
        Expect.Equal(Sandbox.OriginalDlcLoad, ReadText(s.DlcLoad), "dlc_load.json restored");
        Expect.True(!s.RegistryKeyExists(Product.UninstallKey), "registry entry removed");
        // Its %TEMP% copy deletes itself a moment later.
        var tempDir = Path.Combine(s.Root, "Temp");
        deadline = DateTime.UtcNow.AddSeconds(15);
        while (Directory.Exists(tempDir) && Directory.GetFiles(tempDir, "CK3xBL-Uninstall-*.exe").Length > 0 && DateTime.UtcNow < deadline)
            System.Threading.Thread.Sleep(200);
        Expect.True(!Directory.Exists(tempDir) || Directory.GetFiles(tempDir, "CK3xBL-Uninstall-*.exe").Length == 0, "temp copy removed");
    }

    private static void UninstallDeleteUserData()
    {
        using var s = new Sandbox("purge", _keep);
        ExpectExit(0, s.Run(_setupV1));
        Sandbox.Write(Path.Combine(s.SharedDir, "bridge.log"), "log");
        Sandbox.Write(Path.Combine(s.SharedDir, "battles", "1", "request.json"), "{}");
        ExpectExit(0, s.Run(_setupV1, "/uninstall"));
        Expect.True(File.Exists(Path.Combine(s.SharedDir, "bridge.log")), "logs kept without the option");

        ExpectExit(0, s.Run(_setupV1));
        ExpectExit(0, s.Run(_setupV1, "/uninstall", "/delete-user-data"));
        Expect.True(!Directory.Exists(s.SharedDir), "settings, logs and battles deleted on request");
    }

    private static void ModulesNotWritable()
    {
        using var s = new Sandbox("readonly", _keep);
        var sid = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(sid, FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.WriteData,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
        var info = new DirectoryInfo(s.ModulesDir);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(rule);
        info.SetAccessControl(acl);
        try
        {
            var r = s.Run(_setupV1);
            ExpectExit(1, r);
            Expect.True(r.Log.Contains("needs administrator rights"), "reported with instructions");
            Expect.True(!Directory.Exists(s.ModuleDir), "nothing written to Modules");
            var manual = Path.Combine(s.InstallDir, "bannerlord-module", "CK3Bridge", "SubModule.xml");
            Expect.True(File.Exists(manual), "a copy to install by hand is next to the program");
            Expect.True(File.Exists(s.Descriptor) && File.Exists(Path.Combine(s.InstallDir, "CK3xBL.exe")), "the other steps ran");
        }
        finally
        {
            acl = info.GetAccessControl();
            acl.RemoveAccessRule(rule);
            info.SetAccessControl(acl);
        }
        ExpectExit(0, s.Run(_setupV1, "/uninstall"));
        Expect.True(!Directory.Exists(s.InstallDir), "the manual copy is removed by uninstall too");
    }

    private static void BannerlordRunning()
    {
        using var s = new Sandbox("blrunning", _keep);
        // Not Bannerlord: a copy of ping.exe inside the fake Bannerlord folder, so that folder has a running program.
        var fake = Path.Combine(s.BlDir, "bin", "Win64_Shipping_Client", "pingcopy.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), fake);
        using var p = Process.Start(new ProcessStartInfo(fake, "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            var r = s.Run(_setupV1);
            ExpectExit(1, r);
            Expect.True(r.Log.Contains("Bannerlord is running"), "reported");
            Expect.True(!Directory.Exists(s.ModuleDir), "module not written while the game runs");
            Expect.True(File.Exists(s.Descriptor), "the other steps ran");
        }
        finally
        {
            if (!p.HasExited) p.Kill();
            p.WaitForExit();
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Ownership, partial failures and moved-aside copies

    private static void InstallFolderRules()
    {
        var root = Path.Combine(_work, "folders " + Sandbox.OddName);
        var empty = Path.Combine(root, "empty");
        Directory.CreateDirectory(empty);
        var shared = Path.Combine(root, "Games");
        Sandbox.Write(Path.Combine(shared, "LICENSE.txt"), "theirs");
        var earlier = Path.Combine(root, "Mine");
        Sandbox.Write(Path.Combine(earlier, Product.ManifestFile), "{}");
        var named = Path.Combine(root, "x", "CK3xBL");
        Sandbox.Write(Path.Combine(named, "other.txt"), "x");

        Expect.Equal(FileOps.Full(empty), SetupContext.InstallFolderFor(empty), "an empty folder is used as it is");
        Expect.Equal(FileOps.Full(Path.Combine(root, "new")), SetupContext.InstallFolderFor(Path.Combine(root, "new")), "a new folder is used as it is");
        Expect.Equal(Path.Combine(FileOps.Full(shared), "CK3xBL"), SetupContext.InstallFolderFor(shared), "a folder with other files gets a CK3xBL folder");
        Expect.Equal(FileOps.Full(earlier), SetupContext.InstallFolderFor(earlier), "an earlier install is used as it is");
        Expect.Equal(FileOps.Full(named), SetupContext.InstallFolderFor(named), "a folder called CK3xBL is used as it is");
        var drive = Path.GetPathRoot(FileOps.Full(_work))!;
        Expect.Equal(Path.Combine(drive, "CK3xBL"), SetupContext.InstallFolderFor(drive), "a drive root is never the install folder");

        var ctx = SetupContext.Create(Options.Parse(new[] { $"/target-root={root}", $"/install-dir={shared}" }));
        Expect.Equal(Path.Combine(FileOps.Full(shared), "CK3xBL"), ctx.InstallDir, "/install-dir follows the same rule");
        var un = SetupContext.Create(Options.Parse(new[] { $"/target-root={root}", $"/install-dir={shared}", "/uninstall" }));
        Expect.Equal(FileOps.Full(shared), un.InstallDir, "uninstall uses the named folder as it is");

        Expect.Equal(@"C:\Program Files\CK3xBL\CK3xBL.exe", UninstallEngine.CommandExe("\"C:\\Program Files\\CK3xBL\\CK3xBL.exe\" --tray"), "quoted Run command");
        Expect.Equal(@"C:\Tools\CK3xBL.exe", UninstallEngine.CommandExe(@"C:\Tools\CK3xBL.exe --tray"), "unquoted Run command");

        // The record rules: files are deleted only in Crowns & Blades's own folders.
        const string inst = @"C:\Games\CK3xBL";
        bool Allowed(string path, string target) => RecordRules.Place(new InstalledFile { Path = path, Target = target }, inst, out _);
        Expect.True(Allowed(@"C:\Games\CK3xBL\data\x.json", Targets.App), "app file in the install folder");
        Expect.True(!Allowed(@"C:\Games\other.txt", Targets.App) && !Allowed(inst, Targets.App), "app file outside the install folder");
        Expect.True(Allowed(@"D:\Docs\Paradox Interactive\Crusader Kings III\mod\ck3xbl\common\x.txt", Targets.Ck3Mod), "mod file");
        Expect.True(!Allowed(@"C:\Windows\System32\drivers\etc\hosts", Targets.Ck3Mod), "not a mod file");
        Expect.True(Allowed(@"D:\Docs\Crusader Kings III\mod\ck3xbl.mod", Targets.Ck3Descriptor), "descriptor");
        Expect.True(!Allowed(@"D:\Docs\Crusader Kings III\mod\ugc_1.mod", Targets.Ck3Descriptor), "another mod's descriptor");
        Expect.True(Allowed(@"E:\Steam\Bannerlord\Modules\CK3Bridge\SubModule.xml", Targets.BlModule), "module file");
        Expect.True(!Allowed(@"E:\Steam\Bannerlord\Modules\Native\SubModule.xml", Targets.BlModule), "another module's file");
        Expect.True(!Allowed(@"relative\Modules\CK3Bridge\x", Targets.BlModule), "relative paths never count");

        Expect.True(Native.RunsAsSessionUser(), "the self-test runs as the user signed in to this session");
    }

    private static void InstallDirLink()
    {
        using var s = new Sandbox("instlink", _keep);
        var elsewhere = Path.Combine(s.Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        MakeJunction(s.InstallDir, elsewhere);
        var r = s.Run(_setupV1);
        ExpectExit(2, r);
        Expect.True(r.Log.Contains("is a link"), "reported");
        Expect.True(Directory.GetFileSystemEntries(elsewhere).Length == 0, "nothing written through the link");
        Expect.True(!File.Exists(s.Descriptor) && !Directory.Exists(s.ModuleDir), "nothing else was changed");
        Expect.True(!s.RegistryKeyExists(Product.UninstallKey), "no Add/Remove Programs entry");
    }

    private static void ForeignAppFiles()
    {
        using var s = new Sandbox("foreignapp", _keep);
        var theirs = Path.Combine(s.InstallDir, "LICENSE.txt");
        var other = Path.Combine(s.InstallDir, "their-notes.txt");
        Sandbox.Write(theirs, "another program's license");
        Sandbox.Write(other, "not ours");
        var r = s.Run(_setupV1);
        ExpectExit(1, r);
        ExpectInstalled(s, 1);
        var m = InstallManifest.Parse(File.ReadAllText(s.Manifest));
        var aside = m.MovedAside.SingleOrDefault(a => FileOps.PathEquals(a.Original, theirs)) ?? throw new TestFailure("the foreign LICENSE.txt is recorded");
        Expect.Equal("another program's license", File.ReadAllText(aside.Backup), "moved aside, not overwritten");

        var u = s.Run(_setupV1, "/uninstall");
        ExpectExit(0, u);
        Expect.Equal("another program's license", File.ReadAllText(theirs), "put back by uninstall");
        Expect.Equal("not ours", File.ReadAllText(other), "a file setup never touched stays");
        Expect.True(!File.Exists(Path.Combine(s.InstallDir, "CK3xBL.exe")) && !File.Exists(s.Manifest), "our files are gone");
        Expect.True(!Directory.Exists(Path.Combine(s.SharedDir, Product.BackupFolder)), "the emptied backup folder is removed");
    }

    private static void UpgradeStepFailure()
    {
        using var s = new Sandbox("upfail", _keep);
        ExpectExit(0, s.Run(_setupV1));
        var exe = Path.Combine(s.InstallDir, "CK3xBL.exe");
        RunResult r;
        // Held open without delete sharing (as an antivirus scan or a backup tool might): v2 cannot replace it.
        using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
            r = s.Run(_setupV2);
        ExpectExit(1, r);
        Expect.True(Sandbox.Lines(r.Log).Any(l => l.Contains(" ERROR ") && l.Contains("Program files:")), "the program files step failed:\n" + r.Log);
        var m = InstallManifest.Parse(File.ReadAllText(s.Manifest));
        foreach (var rel in new[] { "CK3xBL.exe", @"data\dropped_in_v2.json", @"data\terrain.json", "CK3xBL-Uninstall.exe" })
            Expect.True(m.Files.Any(f => FileOps.PathEquals(f.Path, Path.Combine(s.InstallDir, rel))), $"{rel} is still recorded after the failed step");
        Expect.True(m.Files.Any(f => f.Target == Targets.Ck3Mod) && m.Files.Any(f => f.Target == Targets.BlModule), "the other steps still ran");
        Expect.True(s.RegistryKeyExists(Product.UninstallKey), "the Add/Remove Programs entry is there");

        ExpectExit(0, s.Run(_setupV1, "/uninstall"));
        Expect.True(!Directory.Exists(s.InstallDir), "uninstall removes everything, including the files of the failed upgrade");
    }

    private static void UninstallStepFailure()
    {
        using var s = new Sandbox("unfail", _keep);
        ExpectExit(0, s.Run(_setupV1));
        RunResult r;
        using (new FileStream(s.DlcLoad, FileMode.Open, FileAccess.Read, FileShare.Read))
            r = s.Run(_setupV1, "/uninstall");
        ExpectExit(1, r);
        Expect.True(Sandbox.Lines(r.Log).Any(l => l.Contains(" ERROR ") && l.Contains("CK3 mod list:")), "the mod list step failed:\n" + r.Log);
        Expect.True(File.Exists(s.Manifest), "the record stays");
        var m = InstallManifest.Parse(File.ReadAllText(s.Manifest));
        Expect.True(m.DlcLoad is { Added: true }, "it still knows how to undo the mod list edit");
        Expect.True(File.Exists(Path.Combine(s.InstallDir, "CK3xBL-Uninstall.exe")), "the uninstaller stays");
        Expect.True(s.RegistryKeyExists(Product.UninstallKey), "the Add/Remove Programs entry stays");
        Expect.True(!File.Exists(Path.Combine(s.InstallDir, "CK3xBL.exe")) && !Directory.Exists(s.ModuleDir), "the rest was removed");

        var again = s.Run(_setupV1, "/uninstall");
        ExpectExit(0, again);
        Expect.Equal(Sandbox.OriginalDlcLoad, ReadText(s.DlcLoad), "the second run restores dlc_load.json");
        Expect.True(!Directory.Exists(s.InstallDir) && !s.RegistryKeyExists(Product.UninstallKey), "and finishes");
    }

    /// <summary>A foreign CK3Bridge replaced with /replace-existing, then a module file edited by the player: the folder stays
    /// after uninstall, so the earlier copy cannot go back; /delete-user-data must not delete it.</summary>
    private static void UninstallKeepsUnrestoredBackup()
    {
        using var s = new Sandbox("unrestored", _keep);
        Sandbox.Write(Path.Combine(s.ModuleDir, "SubModule.xml"), "<Module><Id value=\"CK3Bridge\"/><!-- theirs --></Module>");
        ExpectExit(0, s.Run(_setupV1, "/replace-existing"));
        var backup = InstallManifest.Parse(File.ReadAllText(s.Manifest)).MovedAside.Single().Backup;
        File.AppendAllText(Path.Combine(s.ModuleDir, "GUI", "Prefabs", "CK3TitleCard.xml"), "<!-- edited -->");
        Sandbox.Write(Path.Combine(s.SharedDir, "bridge.log"), "log");

        var r = s.Run(_setupV1, "/uninstall", "/delete-user-data");
        ExpectExit(1, r);
        Expect.True(File.ReadAllText(Path.Combine(backup, "SubModule.xml")).Contains("theirs"), "the earlier copy is kept in setup-backups");
        Expect.True(!File.Exists(Path.Combine(s.SharedDir, "bridge.log")) && !File.Exists(Path.Combine(s.SharedDir, "config.json")),
            "settings and logs are deleted");
        Expect.True(r.Log.Contains("kept " + Path.Combine(s.SharedDir, Product.BackupFolder)), "the player is told where the copy is");
        Expect.True(!s.RegistryKeyExists(Product.UninstallKey), "nothing to run again for: the place is the player's now");
    }

    /// <summary>Uninstall while Bannerlord runs: the module and the foreign copy behind it stay recorded, and the next run
    /// puts the copy back.</summary>
    private static void UninstallIncompleteKeepsMovedAside()
    {
        using var s = new Sandbox("incomplete", _keep);
        Sandbox.Write(Path.Combine(s.ModuleDir, "SubModule.xml"), "<Module><Id value=\"CK3Bridge\"/><!-- theirs --></Module>");
        ExpectExit(0, s.Run(_setupV1, "/replace-existing"));
        var fake = Path.Combine(s.BlDir, "bin", "Win64_Shipping_Client", "pingcopy.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), fake);
        using (var p = Process.Start(new ProcessStartInfo(fake, "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
        {
            try
            {
                ExpectExit(1, s.Run(_setupV1, "/uninstall"));
                var rest = InstallManifest.Parse(File.ReadAllText(s.Manifest));
                Expect.Equal(1, rest.MovedAside.Count, "the moved-aside copy stays recorded");
                Expect.True(rest.Files.Any(f => f.Target == Targets.BlModule), "the module stays recorded");
            }
            finally
            {
                if (!p.HasExited) p.Kill();
                p.WaitForExit();
            }
        }
        File.Delete(fake);
        ExpectExit(0, s.Run(_setupV1, "/uninstall"));
        Expect.True(File.ReadAllText(Path.Combine(s.ModuleDir, "SubModule.xml")).Contains("theirs"), "the second run puts the earlier module back");
        Expect.True(!Directory.Exists(s.InstallDir), "and finishes");
    }

    private static void UninstallRunKey()
    {
        string RunKeyPath(Sandbox s) => s.RegistryPrefix + "\\" + Product.RunKey;
        using (var s = new Sandbox("runkey", _keep))
        {
            ExpectExit(0, s.Run(_setupV1));
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath(s)))
                key.SetValue(Product.RunValue, $"\"{Path.Combine(s.InstallDir, "CK3xBL.exe")}\"");
            var r = s.Run(_setupV1, "/uninstall");
            ExpectExit(0, r);
            Expect.True(r.Log.Contains($"Start with Windows: removed"), "the Start with Windows entry of this install is removed");
        }
        using (var s = new Sandbox("runkeydev", _keep))
        {
            ExpectExit(0, s.Run(_setupV1));
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath(s)))
                key.SetValue(Product.RunValue, "\"D:\\src\\ck3xbl\\bridge\\bin\\CK3xBL.exe\"");
            var r = s.Run(_setupV1, "/uninstall");
            ExpectExit(0, r);
            Expect.True(r.Log.Contains("not this install; left unchanged"), "an entry for another copy is left alone");
        }
    }

    private static void DocumentsMoved()
    {
        using var s = new Sandbox("kfm", _keep);
        ExpectExit(0, s.Run(_setupV1));
        // The last install recorded a CK3 user folder in an older Documents place that still exists.
        var old = Path.Combine(s.Root, "Old Documents", "Paradox Interactive", "Crusader Kings III");
        Sandbox.Write(Path.Combine(old, "pdx_settings.txt"), "old");
        void Record(bool custom)
        {
            var m = InstallManifest.Parse(File.ReadAllText(s.Manifest));
            m.Ck3UserDir = old;
            m.Ck3UserDirCustom = custom;
            m.Save(s.Manifest);
            var config = Path.Combine(s.SharedDir, "config.json");
            var o = (JsonObject)Json.Parse(File.ReadAllText(config))!;
            o.GetObject("ck3")!["userDir"] = old;
            File.WriteAllText(config, Json.Write(o, indented: true), FileOps.Utf8NoBom);
        }
        string? ConfigUserDir() => (Json.Parse(File.ReadAllText(Path.Combine(s.SharedDir, "config.json"))) as JsonObject)?.GetObject("ck3")?.GetString("userDir");

        Record(custom: false);
        var r = s.Run(_setupV2);
        ExpectExit(0, r);
        Expect.True(r.Log.Contains("the Documents folder moved"), "reported");
        var after = InstallManifest.Parse(File.ReadAllText(s.Manifest));
        Expect.True(FileOps.PathEquals(s.Ck3UserDir, after.Ck3UserDir), "the upgrade follows the current Documents folder");
        Expect.True(FileOps.PathEquals(s.Ck3UserDir, ConfigUserDir()), "the bridge settings follow too");
        Expect.True(!Directory.Exists(Path.Combine(old, "mod")), "nothing is written to the old place");

        Record(custom: true);
        var r2 = s.Run(_setupV2);
        Expect.True(r2.ExitCode is 0 or 1, $"upgrade with a chosen folder: {r2}");
        var kept = InstallManifest.Parse(File.ReadAllText(s.Manifest));
        Expect.True(FileOps.PathEquals(old, kept.Ck3UserDir) && kept.Ck3UserDirCustom, "a folder the player chose is kept");
        Expect.True(File.Exists(Path.Combine(old, "mod", "ck3xbl.mod")), "and used");
        Expect.True(!File.Exists(s.Descriptor) && !Directory.Exists(s.ModDir), "the mod at the other place is removed (it was ours and unchanged)");
        Expect.Equal(Sandbox.OriginalDlcLoad, ReadText(s.DlcLoad), "the mod list at the other place is restored");
        Expect.True(Ck3ModList.IsEnabled(old), "the chosen folder's mod list has the mod");
    }

    /// <summary>The elevated helper, run directly (unelevated, in a sandbox): it refuses requests with paths outside its
    /// folders, and the restore operation puts a moved-aside module back.</summary>
    private static void ElevatedHelperChecks()
    {
        using var s = new Sandbox("helper", _keep);
        var temp = Path.Combine(s.Root, "Temp");
        var backupRoot = Path.Combine(s.SharedDir, Product.BackupFolder);
        var victim = Path.Combine(s.Root, "not ours.txt");
        Sandbox.Write(victim, "keep me");
        int n = 0;
        int Helper(JsonObject request, out JsonObject? result)
        {
            var file = Path.Combine(temp, $"request-{n++}.json");
            var resultFile = request.GetString("resultFile") ?? Path.Combine(temp, $"request-{n}.result.json");
            request["resultFile"] = resultFile;
            Sandbox.Write(file, Json.Write(request, indented: true));
            int code = Elevation.RunHelper(file);
            result = File.Exists(resultFile) ? Json.Parse(File.ReadAllText(resultFile)) as JsonObject : null;
            return code;
        }
        JsonObject Request(string op) => new() { ["op"] = op, ["blDir"] = s.BlDir, ["backupRoot"] = backupRoot };
        var victimRecord = new List<object?>
        {
            new JsonObject { ["path"] = victim, ["sha256"] = FileOps.Sha256File(victim), ["size"] = new JsonNumber(7), ["target"] = Targets.BlModule },
        };

        var outside = Request(Elevation.OpUninstall);
        outside["previousFiles"] = victimRecord;
        Expect.Equal(ExitCodes.BadArguments, Helper(outside, out var refused), "a file outside Modules\\CK3Bridge is refused");
        Expect.True(refused?.GetString("message")?.Contains("request refused") == true, "and reported");
        Expect.True(File.Exists(victim), "nothing outside the module folder is deleted");

        var badResult = Request(Elevation.OpUninstall);
        badResult["resultFile"] = Path.Combine(s.Root, "elsewhere.json");
        Expect.Equal(ExitCodes.BadArguments, Helper(badResult, out _), "a result file outside the request's folder is refused");
        Expect.True(!File.Exists(Path.Combine(s.Root, "elsewhere.json")), "and not written");

        Expect.Equal(ExitCodes.BadArguments, Helper(Request("delete-everything"), out _), "an unknown operation is refused");

        var stray = Request(Elevation.OpRestore);
        stray["restoreFrom"] = Path.Combine(s.Root, "Documents");
        Expect.Equal(ExitCodes.BadArguments, Helper(stray, out _), "a backup outside setup-backups is refused");
        Expect.True(Directory.Exists(Path.Combine(s.Root, "Documents")) && !Directory.Exists(s.ModuleDir), "nothing was moved");

        var saved = Path.Combine(backupRoot, "20260101-000000", "CK3Bridge");
        Sandbox.Write(Path.Combine(saved, "SubModule.xml"), "<Module><!-- theirs --></Module>");
        var restore = Request(Elevation.OpRestore);
        restore["restoreFrom"] = saved;
        Expect.Equal(ExitCodes.Ok, Helper(restore, out var restored), $"restore: {restored?.GetString("message")}");
        Expect.True(File.ReadAllText(Path.Combine(s.ModuleDir, "SubModule.xml")).Contains("theirs") && !Directory.Exists(saved), "the module is put back");
    }
}
