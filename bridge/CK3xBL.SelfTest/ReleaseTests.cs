// Self tests of what a player install depends on: where an installed copy finds its data and config, game detection and version
// reading, the install check, the CK3 mod list (dlc_load.json), the Bannerlord module gate, diagnostics masking, archive pruning and
// the release versions. Headless; every file is written under a temp folder, never into a game install or the user's Documents.
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CK3xBL.Core;
using CK3xBL.Core.Bannerlord;
using CK3xBL.Core.Ck3;
using CK3xBL.Core.Config;
using CK3xBL.Core.Diagnostics;

namespace CK3xBL.SelfTest;

internal static class ReleaseTests
{
    public static void Register(TestRegistry r)
    {
        r.Add("release.paths.installed_copy", PathsInstalledCopy);
        r.Add("release.paths.source_checkout", PathsSourceCheckout);
        r.Add("release.paths.overrides_and_missing_data", PathsOverridesAndMissingData);
        r.Add("release.paths.this_checkout", PathsThisCheckout);
        r.Add("release.games.versions_and_editions", GameVersionsAndEditions);
        r.Add("release.games.store_records", GameStoreRecords);
        r.Add("release.install_check.fake_trees", InstallCheckFakeTrees);
        r.Add("release.ck3_mod_list.enable_disable", ModListEnableDisable);
        r.Add("release.ck3_mod_list.never_breaks_the_file", ModListNeverBreaksTheFile);
        r.Add("release.bannerlord.module_missing_blocks_launch", BannerlordModuleMissingBlocksLaunch);
        r.Add("release.diagnostics.user_folder_masked", DiagnosticsUserFolderMasked);
        r.Add("release.archive.prune_keeps_newest", ArchivePruneKeepsNewest);
        r.Add("release.versions_agree", VersionsAgree);
    }

    // ================================================================================================================ paths

    /// <summary>The install layout: CK3xBL.exe with data\ next to it, no source markers. config.json goes to the shared folder.</summary>
    private static void PathsInstalledCopy()
    {
        using var t = new Sandbox();
        string inst = t.Dir("Programs/CK3xBL");
        t.Write("Programs/CK3xBL/data/ck3_maa_index.json", "{}");
        string shared = t.Dir("LocalAppData/ck3xbl");

        var p = BridgePaths.Detect(null, null, inst, shared);
        Expect.Equal(inst, p.BridgeRoot);
        Expect.Equal(inst, p.InstallDir);
        Expect.Equal(Path.Combine(inst, "data"), p.DataDir);
        Expect.True(p.HasData);
        Expect.False(p.IsDevTree, "an install folder is not a checkout");
        Expect.Equal(Path.Combine(shared, "config.json"), p.ConfigFile, "an installed copy keeps its settings out of the install folder");
        Expect.Equal(Path.Combine(shared, "bridge.log"), p.JournalFile);

        // A config.json someone put next to the exe (portable use) is respected.
        t.Write("Programs/CK3xBL/config.json", "{}");
        Expect.Equal(Path.Combine(inst, "config.json"), BridgePaths.Detect(null, null, inst, shared).ConfigFile);
    }

    /// <summary>A checkout: bridge/ holds data/ and CK3xBL.slnx; the exe runs from bin/Release/...; config.json stays in bridge/.</summary>
    private static void PathsSourceCheckout()
    {
        using var t = new Sandbox();
        t.Write("repo/bridge/data/ck3_maa_index.json", "{}");
        t.Write("repo/bridge/CK3xBL.slnx", "<Solution />");
        string exe = t.Dir("repo/bridge/CK3xBL/bin/Release/net10.0-windows");
        string bridge = Path.Combine(t.Root, "repo", "bridge");

        var p = BridgePaths.Detect(null, null, exe, t.Dir("shared"));
        Expect.Equal(bridge, p.BridgeRoot);
        Expect.True(p.IsDevTree);
        Expect.Equal(Path.Combine(bridge, "config.json"), p.ConfigFile, "a checkout keeps bridge/config.json even before it exists");
        Expect.Equal(exe, p.InstallDir);
    }

    private static void PathsOverridesAndMissingData()
    {
        using var t = new Sandbox();
        string shared = t.Dir("shared");
        string exe = t.Dir("bare");

        var bare = BridgePaths.Detect(null, null, exe, shared);
        Expect.Equal(exe, bare.BridgeRoot, "nothing found: the exe folder");
        Expect.False(bare.HasData, "data/ missing is reported, not invented");

        t.Write("elsewhere/data/ck3_maa_index.json", "{}");
        string env = Path.Combine(t.Root, "elsewhere");
        var fromEnv = BridgePaths.Detect(null, env, exe, shared);
        Expect.Equal((env, true), (fromEnv.BridgeRoot, fromEnv.HasData), "CK3XBL_BRIDGE_ROOT names the root");
        Expect.Equal(Path.Combine(shared, "config.json"), fromEnv.ConfigFile);
        Expect.Equal(exe, BridgePaths.Detect(null, t.Dir("nodata"), exe, shared).BridgeRoot, "a root variable without data/ is ignored");

        string explicitConfig = Path.Combine(t.Root, "cfg", "mine.json");
        var fromArg = BridgePaths.Detect(explicitConfig, env, exe, shared);
        Expect.Equal(explicitConfig, fromArg.ConfigFile, "--config wins");
        Expect.Equal(Path.Combine(t.Root, "cfg"), fromArg.BridgeRoot);
    }

    /// <summary>This SelfTest runs from a checkout: the real detection must say so and find the real data.</summary>
    private static void PathsThisCheckout()
    {
        var p = BridgePaths.Detect();
        Expect.True(p.HasData, $"data/ found at {p.DataDir}");
        Expect.True(p.IsDevTree, $"{p.BridgeRoot} is recognised as a checkout");
        Expect.Equal(Path.Combine(p.BridgeRoot, "config.json"), p.ConfigFile);
    }

    // ================================================================================================================ games

    private static void GameVersionsAndEditions()
    {
        using var t = new Sandbox();
        string ck3 = FakeCk3(t, "1.20.0.4");
        Expect.True(GameInstall.IsCk3Dir(ck3));
        Expect.Equal("1.20.0.4", GameInstall.Ck3Version(ck3));
        Expect.Equal("steam", GameInstall.Ck3Platform(ck3));
        Expect.Equal(GameEdition.Steam, GameInstall.Ck3Edition(ck3));
        File.Delete(Path.Combine(ck3, "launcher", "launcher-settings.json"));
        t.Write("ck3/titus_branch.txt", "release/1.20.0\n");
        Expect.Equal("1.20.0", GameInstall.Ck3Version(ck3), "titus_branch.txt fallback");
        Expect.Null(GameInstall.Ck3Version(t.Dir("empty")));

        Expect.True(GameInstall.IsTargetCk3("1.20.0.4") && GameInstall.IsTargetCk3("1.20") && GameInstall.IsTargetCk3(null));
        Expect.False(GameInstall.IsTargetCk3("1.21.0") || GameInstall.IsTargetCk3("1.200.1"));
        Expect.True(GameInstall.IsTargetBannerlord("v1.4.8") && GameInstall.IsTargetBannerlord("v1.4.9") && GameInstall.IsTargetBannerlord(null));
        Expect.False(GameInstall.IsTargetBannerlord("v1.5.0") || GameInstall.IsTargetBannerlord("v1.40.0"));

        string bl = FakeBannerlord(t, "bl", "v1.4.8");
        Expect.Equal("v1.4.8", GameInstall.BannerlordVersion(bl));
        Expect.Equal(GameEdition.Steam, GameInstall.BannerlordEdition(bl));
        string gog = FakeBannerlord(t, "gog", "v1.4.8", steam: false);
        t.Write("gog/goggame-1802539526.info", "{}");
        Expect.Equal(GameEdition.Gog, GameInstall.BannerlordEdition(gog));
        string epic = FakeBannerlord(t, "epic", "v1.4.8", steam: false);
        t.Dir("epic/.egstore");
        Expect.Equal(GameEdition.Epic, GameInstall.BannerlordEdition(epic));
        t.Write("xbox/Content/bin/Gaming.Desktop.x64_Shipping_Client/Bannerlord.exe", "");
        string xbox = Path.Combine(t.Root, "xbox", "Content");
        Expect.False(GameInstall.IsBannerlordDir(xbox), "the Xbox layout is not a usable install");
        Expect.Equal(GameEdition.Xbox, GameInstall.BannerlordEdition(xbox));
        t.Write("broken/bin/Win64_Shipping_Client/Version.xml", "<Version><Singleplayer");
        Expect.Null(GameInstall.BannerlordVersion(Path.Combine(t.Root, "broken")), "an unreadable Version.xml is unknown, not a crash");
    }

    private static void GameStoreRecords()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0" { "path" "C:\\Program Files (x86)\\Steam" "label" "" }
                "1" { "path" "E:\\Games\\SteamLibrary" }
            }
            """;
        Expect.SequenceEqual(new[] { @"C:\Program Files (x86)\Steam", @"E:\Games\SteamLibrary" }, GameLocator.LibraryPaths(vdf));

        using var t = new Sandbox();
        t.Write("manifests/a.item", """{ "DisplayName": "Other", "InstallLocation": "F:\\Epic\\Other" }""");
        t.Write("manifests/b.item", "not json");
        t.Write("manifests/c.item", """{ "InstallLocation": "F:\\Epic\\MountAndBlade2" }""");
        Expect.SequenceEqual(new[] { @"F:\Epic\MountAndBlade2", @"F:\Epic\Other" },
            GameLocator.EpicInstallLocations(Path.Combine(t.Root, "manifests")).OrderBy(s => s, StringComparer.Ordinal));
        Expect.False(GameLocator.EpicInstallLocations(Path.Combine(t.Root, "none")).Any());
    }

    // ================================================================================================================ install check

    private static void InstallCheckFakeTrees()
    {
        using var t = new Sandbox();
        var targets = new InstallTargets
        {
            Ck3Dir = FakeCk3(t, "1.20.0.4"),
            Ck3UserDir = FakeCk3User(t, BridgeVersion.Mod, enabled: true),
            BannerlordDir = FakeBannerlord(t, "bl", "v1.4.8"),
        };
        FakeModule(t, targets.BannerlordDir, BridgeVersion.Module);

        var all = InstallCheck.Run(targets);
        Expect.SequenceEqual(Array.Empty<string>(), Warnings(all), "a complete install has no warnings");
        Expect.True(all.Any(i => i.Check == "ck3.found" && i.Title.Contains("1.20.0.4") && i.Title.Contains("Steam")), "versions are shown");
        Expect.True(all.Any(i => i.Check == "mod.enabled" && !i.IsWarning));

        // The mod is in the list only until the Paradox Launcher rewrites it.
        File.WriteAllText(Ck3ModList.PathIn(targets.Ck3UserDir), """{"enabled_mods":[],"disabled_dlcs":[]}""");
        Expect.SequenceEqual(new[] { "mod.enabled" }, Warnings(InstallCheck.Run(targets)));
        Expect.Equal(InstallFix.AddToPlayset, InstallCheck.Run(targets).Single(i => i.IsWarning).Fix);
        Ck3ModList.Enable(targets.Ck3UserDir, () => false);

        // Mismatched versions: an old mod, an old module, a newer CK3, a newer Bannerlord.
        File.WriteAllText(Path.Combine(t.Root, "docs", "ck3xbl", "descriptor.mod"), "version=\"0.1.0\"\nname=\"Crowns & Blades Battles\"\n");
        FakeModule(t, targets.BannerlordDir, "v0.4.0");
        t.Write("ck3/launcher/launcher-settings.json", """{"rawVersion":"1.21.0.1","distPlatform":"steam"}""");
        t.Write("bl/bin/Win64_Shipping_Client/Version.xml", """<Version><Singleplayer Value="v1.5.0"/></Version>""");
        Expect.SequenceEqual(new[] { "bl.version", "ck3.version", "mod.version", "module.version" },
            Warnings(InstallCheck.Run(targets)).OrderBy(s => s, StringComparer.Ordinal));

        // Missing pieces.
        Directory.Delete(Path.Combine(targets.BannerlordDir, "Modules", "CK3Bridge"), recursive: true);
        Directory.Delete(Path.Combine(t.Root, "docs", "ck3xbl"), recursive: true);
        var missing = InstallCheck.Run(targets);
        Expect.True(Warnings(missing).Contains("module.files") && Warnings(missing).Contains("mod.files"));
        Expect.True(missing.Where(i => i.Check is "module.files" or "mod.files").All(i => i.Fix == InstallFix.Reinstall));

        var none = InstallCheck.Run(new InstallTargets { Ck3Dir = t.Dir("nothing"), Ck3UserDir = t.Dir("nouser"), BannerlordDir = "" });
        Expect.True(none.Where(i => i.Check is "ck3.found" or "bl.found").All(i => i.IsWarning && i.Fix == InstallFix.SetGameFolders));
        var xbox = InstallCheck.Run(new InstallTargets { BannerlordDir = "", XboxBannerlordDir = @"X:\XboxGames\Bannerlord\Content" });
        Expect.True(xbox.Single(i => i.Check == "bl.found").Title.Contains("Xbox"), "the Xbox edition gets its own notice");
    }

    private static string[] Warnings(IEnumerable<InstallItem> items) => items.Where(i => i.IsWarning).Select(i => i.Check).ToArray();

    // ================================================================================================================ dlc_load.json

    private static void ModListEnableDisable()
    {
        using var t = new Sandbox();
        string user = t.Dir("user");
        string file = Ck3ModList.PathIn(user);
        Func<bool> notRunning = () => false;

        Expect.Equal(ModListChange.Changed, Ck3ModList.Enable(user, notRunning), "a missing file is created");
        Expect.Equal("""{"enabled_mods":["mod/ck3xbl.mod"],"disabled_dlcs":[]}""", File.ReadAllText(file));
        Expect.False(File.Exists(file + Ck3ModList.BackupSuffix), "nothing to back up");

        const string original = "\uFEFF{\"enabled_mods\":[\"mod/ugc_1.mod\"],\"disabled_dlcs\":[\"dlc_x\"],\"future\":{\"k\":1}}";
        File.WriteAllText(file, original, new UTF8Encoding(false));
        byte[] originalBytes = File.ReadAllBytes(file);
        Expect.False(Ck3ModList.IsEnabled(user));
        Expect.Equal(ModListChange.Changed, Ck3ModList.Enable(user, notRunning));
        string enabled = File.ReadAllText(file);
        Expect.Equal("""{"enabled_mods":["mod/ugc_1.mod","mod/ck3xbl.mod"],"disabled_dlcs":["dlc_x"],"future":{"k":1}}""", enabled,
            "appended last; other keys, entries and order kept");
        Expect.False(File.ReadAllBytes(file).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "written without BOM");
        Expect.SequenceEqual(originalBytes, File.ReadAllBytes(file + Ck3ModList.BackupSuffix), "the original is backed up byte for byte");
        Expect.True(Ck3ModList.IsEnabled(user));

        Expect.Equal(ModListChange.Unchanged, Ck3ModList.Enable(user, notRunning), "idempotent");
        Expect.Equal(ModListChange.Changed, Ck3ModList.Disable(user, notRunning));
        Expect.Equal("""{"enabled_mods":["mod/ugc_1.mod"],"disabled_dlcs":["dlc_x"],"future":{"k":1}}""", File.ReadAllText(file));
        Expect.Equal(ModListChange.Unchanged, Ck3ModList.Disable(user, notRunning));
        Expect.SequenceEqual(originalBytes, File.ReadAllBytes(file + Ck3ModList.BackupSuffix), "the backup is made once, never replaced");

        // An absolute entry naming our descriptor counts as ours (and is removed by Disable).
        string absolute = Path.Combine(user, "mod", "ck3xbl.mod").Replace('\\', '/');
        File.WriteAllText(file, $$"""{"enabled_mods":["{{absolute}}","mod/ugc_2.mod"]}""");
        Expect.True(Ck3ModList.IsEnabled(user));
        Expect.Equal(ModListChange.Unchanged, Ck3ModList.Enable(user, notRunning));
        Ck3ModList.Disable(user, notRunning);
        Expect.Equal("""{"enabled_mods":["mod/ugc_2.mod"]}""", File.ReadAllText(file));
        Expect.False(Ck3ModList.IsOurEntry(user, "mod/ck3xbl_other.mod"));
        Expect.True(Ck3ModList.IsOurEntry(user, @"MOD\CK3XBL.MOD"));
    }

    private static void ModListNeverBreaksTheFile()
    {
        using var t = new Sandbox();
        string user = t.Dir("user");
        string file = Ck3ModList.PathIn(user);

        File.WriteAllText(file, "{ broken");
        Expect.Equal(ModListChange.Unreadable, Ck3ModList.Enable(user, () => false));
        Expect.Equal(ModListChange.Unreadable, Ck3ModList.Disable(user, () => false));
        Expect.Equal("{ broken", File.ReadAllText(file), "invalid JSON is never overwritten");
        Expect.False(Ck3ModList.IsEnabled(user));

        File.WriteAllText(file, """{"enabled_mods":"oops"}""");
        Expect.Equal(ModListChange.Unreadable, Ck3ModList.Enable(user, () => false), "an unexpected shape is left alone");

        File.WriteAllText(file, """{"enabled_mods":[]}""");
        Expect.Equal(ModListChange.Ck3Running, Ck3ModList.Enable(user, () => true), "never while CK3 runs");
        Expect.Equal("""{"enabled_mods":[]}""", File.ReadAllText(file));
    }

    // ================================================================================================================ bannerlord

    /// <summary>Without Modules\CK3Bridge nothing is launched and Problem says why (a 0-byte fake exe: even a mistaken launch could not
    /// start a game).</summary>
    private static async Task BannerlordModuleMissingBlocksLaunch()
    {
        using var t = new Sandbox();
        string bl = FakeBannerlord(t, "bl", "v1.4.8");
        var journal = new MemoryJournal();
        string? announced = null;
        using var link = new BannerlordLink(new BannerlordLinkOptions { GameDir = bl, SharedDir = t.Dir("shared"), KeepRunning = false }, journal);
        link.ProblemChanged += p => announced ??= p;
        bool playerGameOpen = System.Diagnostics.Process.GetProcessesByName("Bannerlord").Length > 0;

        await link.StartAsync(CancellationToken.None);
        Expect.False(link.IsRunning, "nothing was started");
        if (playerGameOpen)
        {
            Console.WriteLine("      a Bannerlord is running on this machine: the module-missing branch is not reached (foreign game first)");
            return;
        }
        Expect.True(link.ModuleMissing);
        Expect.True(link.Problem?.Contains("CK3Bridge module") == true, $"problem: {link.Problem}");
        Expect.Equal(link.Problem, announced, "announced through ProblemChanged");
        Expect.False(journal.Lines.Any(l => l.Contains("launched pid")), "no launch");
        Expect.False(File.Exists(link.LaunchLeasePath), "no launch lease without a launch");
        Expect.Equal("CK3XBL_HOST", BannerlordLink.HostVariable, "the module reads this name (CK3Bridge HostGate)");
    }

    // ================================================================================================================ diagnostics

    private static void DiagnosticsUserFolderMasked()
    {
        const string profile = @"C:\Users\Jane Doe";
        string text = @"config C:\Users\Jane Doe\AppData\Local\ck3xbl\config.json; log c:/users/jane doe/Documents/x; json ""C:\\Users\\Jane Doe\\x""";
        string masked = DiagnosticsReport.MaskUserProfile(text, profile);
        Expect.False(masked.Contains("Jane", StringComparison.OrdinalIgnoreCase), masked);
        Expect.Equal(3, Regex.Matches(masked, Regex.Escape(DiagnosticsReport.UserProfileToken)).Count, masked);
        Expect.Equal(text, DiagnosticsReport.MaskUserProfile(text, @"C:\"), "a drive root is never masked");

        using var t = new Sandbox();
        var paths = BridgePaths.Detect(null, null, t.Dir("inst"), t.Dir("shared"));
        File.WriteAllText(Path.Combine(paths.SharedDir, "bridge.log"),
            string.Concat(Enumerable.Range(0, 300).Select(i => $"line {i} {profile}\\x\n")));
        string report = DiagnosticsReport.Build(paths, new BridgeConfig(), new List<InstallItem>
        {
            new("ck3.found", InstallSeverity.Warning, "Crusader Kings III not found", "Use 'Set game folders...'"),
        }, profile);
        Expect.True(report.Contains(BridgeVersion.Full) && report.Contains("[!!] Crusader Kings III not found"));
        Expect.True(report.Contains("line 299") && !report.Contains("line 99 "), "only the tail of the log");
        Expect.False(report.Contains("Jane"), "the log tail is masked too");
    }

    private static void ArchivePruneKeepsNewest()
    {
        using var t = new Sandbox();
        string root = t.Dir("battles");
        var start = DateTime.UtcNow.AddDays(-1);
        for (int i = 0; i < 55; i++)
        {
            string dir = t.Dir($"battles/b{i}_s");
            File.WriteAllText(Path.Combine(dir, "request.json"), "{}");
            Directory.SetLastWriteTimeUtc(dir, start.AddMinutes(i));
        }
        var archive = new BattleArchive(root, new MemoryJournal());
        Expect.Equal(5, archive.Prune(50));
        var left = Directory.GetDirectories(root).Select(Path.GetFileName).ToHashSet();
        Expect.Equal(50, left.Count);
        Expect.True(left.Contains("b54_s") && left.Contains("b5_s") && !left.Contains("b4_s") && !left.Contains("b0_s"), "the oldest go");
        Expect.Equal(0, archive.Prune(50));
        Expect.Equal(0, new BattleArchive(Path.Combine(t.Root, "none"), new MemoryJournal()).Prune());
    }

    // ================================================================================================================ versions

    /// <summary>The bridge assembly, BridgeVersion and the Bannerlord module's SubModule.xml name one release. The CK3 mod's
    /// descriptor.mod is reported, not failed: it belongs to the CK3 mod and is bumped with it.</summary>
    private static void VersionsAgree()
    {
        string prefix = typeof(BridgeVersion).Assembly.GetName().Version!.ToString(3);
        Expect.True(BridgeVersion.Display.StartsWith(prefix + "-", StringComparison.Ordinal) || BridgeVersion.Display == prefix,
            $"display {BridgeVersion.Display} vs assembly {prefix}");
        Expect.Equal(BridgeVersion.Mod, BridgeVersion.Display, "the CK3 mod carries the bridge's version");
        Expect.Equal("v" + prefix, BridgeVersion.Module, "the module carries v<major.minor.patch> (no suffix: Bannerlord parses it)");
        Expect.False(BridgeVersion.Full.Contains('\\') || BridgeVersion.Full.Contains(':'), "no build path in the version");

        string repo = Directory.GetParent(BridgePaths.Detect().BridgeRoot)!.FullName;
        string subModule = Path.Combine(repo, "bannerlord_module", "CK3Bridge", "SubModule.xml");
        var module = XDocument.Load(subModule).Root!;
        Expect.Equal(BridgeVersion.Module, module.Element("Version")?.Attribute("value")?.Value, subModule);
        foreach (var dep in module.Descendants("DependedModule"))
            Expect.Equal(BridgeVersion.BannerlordTarget, dep.Attribute("DependentVersion")?.Value, $"{dep.Attribute("Id")?.Value} targets the game version");

        string descriptor = Path.Combine(repo, "ck3_mod", "ck3xbl", "descriptor.mod");
        if (File.Exists(descriptor))
        {
            string text = File.ReadAllText(descriptor);
            string? version = InstallCheck.DescriptorValue(text, "version");
            Expect.Equal($"{BridgeVersion.Ck3Target}.*", InstallCheck.DescriptorValue(text, "supported_version"), "the mod targets CK3 " + BridgeVersion.Ck3Target);
            if (version != BridgeVersion.Mod)
                Console.WriteLine($"      NOTE: {descriptor} has version=\"{version}\"; the release expects \"{BridgeVersion.Mod}\" (the CK3 mod is owned outside the bridge; bump it there)");
        }
    }

    // ================================================================================================================ fakes

    private static string FakeCk3(Sandbox t, string version)
    {
        t.Write("ck3/binaries/ck3.exe", "");
        t.Write("ck3/binaries/steam_appid.txt", "1158310");
        t.Write("ck3/launcher/launcher-settings.json", $$"""{"rawVersion":"{{version}}","distPlatform":"steam"}""");
        return Path.Combine(t.Root, "ck3");
    }

    /// <summary>A CK3 user folder (docs/) whose mod/ck3xbl.mod points at docs/ck3xbl, the way setup writes it.</summary>
    private static string FakeCk3User(Sandbox t, string modVersion, bool enabled)
    {
        string docs = t.Dir("docs");
        t.Write("docs/ck3xbl/descriptor.mod", $"version=\"{modVersion}\"\nname=\"Crowns & Blades Battles\"\nsupported_version=\"1.20.*\"\n");
        t.Write("docs/ck3xbl/" + InstallCheck.ModProbeFile, "# on actions\n");
        t.Write("docs/mod/ck3xbl.mod",
            $"version=\"{modVersion}\"\nname=\"Crowns & Blades Battles\"\npath=\"{Path.Combine(docs, "ck3xbl").Replace('\\', '/')}\"\n");
        t.Write("docs/dlc_load.json", enabled ? """{"enabled_mods":["mod/ck3xbl.mod"],"disabled_dlcs":[]}""" : """{"enabled_mods":[]}""");
        return docs;
    }

    private static string FakeBannerlord(Sandbox t, string name, string version, bool steam = true)
    {
        t.Write($"{name}/bin/Win64_Shipping_Client/Bannerlord.exe", "");
        t.Write($"{name}/bin/Win64_Shipping_Client/Version.xml", $"""<Version><Singleplayer Value="{version}"/></Version>""");
        if (steam) t.Write($"{name}/bin/Win64_Shipping_Client/steam_appid.txt", "261550");
        t.Dir($"{name}/Modules/Native");
        return Path.Combine(t.Root, name);
    }

    private static void FakeModule(Sandbox t, string blDir, string version)
    {
        string dir = Path.Combine(blDir, "Modules", "CK3Bridge");
        Directory.CreateDirectory(Path.Combine(dir, "bin", "Win64_Shipping_Client"));
        File.WriteAllText(Path.Combine(dir, "SubModule.xml"), $"""<Module><Id value="CK3Bridge"/><Version value="{version}"/></Module>""");
        File.WriteAllText(Path.Combine(dir, "bin", "Win64_Shipping_Client", "CK3Bridge.dll"), "");
    }

    /// <summary>A temp folder that is removed afterwards; every path is relative to it, '/' separated.</summary>
    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ck3xbl-release-" + Guid.NewGuid().ToString("N")[..8]);

        public Sandbox() => Directory.CreateDirectory(Root);

        public string Dir(string relative)
        {
            string full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(full);
            return full;
        }

        public void Write(string relative, string text)
        {
            string full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
