// Self-tests for CK3xBL.Appearance. Read-only on the user's real CK3 saves and install (nothing is written there);
// outputs (cache, rendered PNGs) go to a temp folder. Saves rotate, so tests pick whatever exists and assert structure;
// exact values are asserted only for one known autosave (by its file name, player id and date).
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using CK3xBL.Appearance;
using CK3xBL.Appearance.Cache;
using CK3xBL.Appearance.Coa;
using CK3xBL.Appearance.Data;
using CK3xBL.Appearance.Game;
using CK3xBL.Appearance.Genes;
using CK3xBL.Appearance.Hero;
using CK3xBL.Appearance.Imaging;
using CK3xBL.Appearance.Save;
using CK3xBL.Core.Model;
using CK3xBL.Protocol;

namespace CK3xBL.SelfTest;

internal static class AppearanceTests
{
    private static readonly string Docs =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Paradox Interactive", "Crusader Kings III");

    private static readonly string OutDir = Path.Combine(Path.GetTempPath(), "ck3xbl_selftest_appearance");

    private static readonly Lazy<string?> GameDir = new(() =>
        new[] { Environment.GetEnvironmentVariable("CK3XBL_CK3_GAME"), CK3xBL.Core.Config.GameLocator.FindCk3(),
                @"C:\Program Files (x86)\Steam\steamapps\common\Crusader Kings III" }
            .FirstOrDefault(d => GameFiles.ResolveGameDir(d) != null));

    private static readonly Lazy<AppearanceEngine> Engine = new(() =>
        AppearanceEngine.Load(GameDir.Value, null, null, _ => { }));

    /// <summary>Saves by header type ("00", "02", "03"), newest first; the debug-session autosave.ck3 first for type 00.</summary>
    private static readonly Lazy<Dictionary<string, List<string>>> SavesByType = new(() =>
    {
        var d = new Dictionary<string, List<string>>();
        string dir = Path.Combine(Docs, "save games");
        if (!Directory.Exists(dir)) return d;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.ck3").OrderByDescending(f => f.Name == "autosave.ck3").ThenByDescending(f => f.LastWriteTimeUtc))
        {
            try
            {
                using var fs = SaveContainer.OpenShared(f.FullName);
                if (SaveContainer.TryReadHeader(fs) is SaveHeader h) (d.TryGetValue(h.Type, out var l) ? l : d[h.Type] = new()).Add(f.FullName);
            }
            catch (IOException) { }
        }
        return d;
    });

    private static readonly Dictionary<string, SaveState> States = new();

    private static SaveState? State(string type)
    {
        lock (States)
        {
            if (States.TryGetValue(type, out var s)) return s;
            if (!SavesByType.Value.TryGetValue(type, out var files) || files.Count == 0) return null;
            s = Engine.Value.ReadSave(files[0], _ => { });
            States[type] = s;
            return s;
        }
    }

    public static void Register(TestRegistry r)
    {
        r.Add("appearance.install_and_data", InstallAndData);
        r.Add("appearance.save_reader.all_types", AllTypes);
        r.Add("appearance.save_reader.text_player_portrait", TextPlayerPortrait);
        r.Add("appearance.save_reader.binary_meta_and_gamestate", BinarySave);
        r.Add("appearance.save_reader.zipped_text_other_mod", ZippedTextOtherMod);
        r.Add("appearance.genes.dna_matches_meta_portrait", DnaMatchesMeta);
        r.Add("appearance.genes.stand_in_stable_and_inherited", StandInStable);
        r.Add("appearance.genes.serialize_roundtrip", GeneRoundtrip);
        r.Add("appearance.dds.all_formats", DdsFormats);
        r.Add("appearance.coa.player_png", PlayerPng);
        r.Add("appearance.banner.codes_are_valid", BannerCodes);
        r.Add("appearance.banner.side_clash", SideClash);
        r.Add("appearance.equipment.kits", EquipmentKits);
        r.Add("appearance.watcher.resolves_fresh_save", WatcherTarget);
        r.Add("appearance.enrich.synthetic_battle", EnrichSynthetic);
        r.Add("appearance.enrich.after_mapper", EnrichAfterMapper);
        r.Add("appearance.enrich.other_campaign_save_ignored", OtherCampaignSaveIgnored);
        r.Add("appearance.enrich.never_throws_without_data", EnrichWithoutData);
        // Regression tests of earlier fixes.
        r.Add("appearance.save_reader.binary_sex_unknown", BinarySexUnknown);
        r.Add("appearance.enrich.trust_needs_evidence", TrustNeedsEvidence);
        r.Add("appearance.banner.binary_live_title", BinaryLiveTitle);
        r.Add("appearance.banner.png_failure_keeps_banners", PngFailureKeepsBanners);
        r.Add("appearance.banner.layer_limit", BannerLayerLimit);
        r.Add("appearance.banner.clash_against_culture_banner", ClashAgainstCultureBanner);
        r.Add("appearance.coa.texture_memory_bounded", TextureMemoryBounded);
        r.Add("appearance.cache.format_and_playthrough", CacheFormatAndPlaythrough);
        r.Add("appearance.watcher.retries_failed_read", WatcherRetriesFailedRead);
        r.Add("appearance.hero.beardless_eunuch", BeardlessEunuch);
    }

    // ------------------------------------------------------------------ install / data

    private static void InstallAndData()
    {
        Expect.NotNull(GameDir.Value, "CK3 install not found (set CK3XBL_CK3_GAME)");
        var e = Engine.Value;
        Expect.Equal(104, e.Genes!.Inheritable.Count, "inheritable genes (3 colour + 98 morph + 3 accessory)");
        Expect.True(e.Palettes!.Complete, "skin/hair/eye palettes decoded");
        Expect.True(e.Ethnicities!.Count >= 40, $"ethnicities: {e.Ethnicities.Count}");
        Expect.True(e.GameCoas.Count > 1000, $"game coats of arms: {e.GameCoas.Count}");
        Expect.True(e.Colors.ResolveName("red") is { } red && red.R > red.G, "named colour red");
        Expect.NotNull(BridgeData.FindDataDirectory(null), "bridge/data discovered from the test binary");
        Expect.True(e.Equipment.HasData, "ruler kits loaded");
    }

    // ------------------------------------------------------------------ save reader

    private static void AllTypes()
    {
        var types = SavesByType.Value;
        Expect.True(types.Count > 0, $"no CK3 saves in {Docs}");
        foreach (var (type, files) in types)
        {
            Expect.True(type is "00" or "01" or "02" or "03", $"unknown save type {type}");
            foreach (var f in files)
            {
                using var fs = SaveContainer.OpenShared(f);
                var h = Expect.NotNull(SaveContainer.TryReadHeader(fs));
                var meta = MetaReader.Read(SaveContainer.ReadMeta(fs, h), h.BinaryMeta, new CoaTokenMap(), Engine.Value.Colors);
                var p = Expect.NotNull(meta.Player, $"{Path.GetFileName(f)}: player portrait");
                Expect.True(p.Id > 0 && p.Genes.Count >= 100, $"{Path.GetFileName(f)}: id {p.Id}, {p.Genes.Count} genes");
                Expect.True(p.Age is > 0 and < 1.3, $"{Path.GetFileName(f)}: portrait age {p.Age}");
                Expect.True(SaveContainer.ZipLooksComplete(fs, h), $"{Path.GetFileName(f)}: zip directory present");
                Console.WriteLine($"      {Path.GetFileName(f)}: type {type}, player {p.Id} ({p.Genes.Count} genes, age {p.Age:0.00}), {meta.GameVersion} {meta.Date}");
            }
        }
    }

    private static void TextPlayerPortrait()
    {
        var s = State("00");
        if (s == null) { Console.WriteLine("      no text (type 00) save present: skipped"); return; }
        var p = Expect.NotNull(s.Meta.Player);
        Expect.True(p.Genes.Count >= 117, $"{p.Genes.Count} genes (104 inheritable + 13 resolved)");
        Expect.True(p.Genes.Morph("hairstyles") != null && p.Genes.Morph("beards") != null, "resolved hair/beard genes");
        var ix = Expect.NotNull(s.Index);
        Expect.True(ix.Characters.Count > 1000 && ix.Titles.Count > 1000 && (ix.Coas?.Count ?? 0) > 1000, ix.Summary());
        var rec = Expect.NotNull(ix.Character(p.Id), "player record in the gamestate");
        Expect.True(rec.PrimaryTitle >= 0 && ix.Titles.ContainsKey(rec.PrimaryTitle), "player primary title");
        Expect.True(s.ReadTime < TimeSpan.FromSeconds(5), $"read + scan took {s.ReadTime.TotalMilliseconds:0} ms");
        if (s.FileName == "autosave.ck3" && p.Id == 10811 && s.Meta.Date?.ToString() == "871.1.1")
        {
            Expect.Near(0.71, p.Age, 1e-6, "debug session player age");
            Expect.Equal(117, p.Genes.Count);
            Expect.Equal("c_orangallu", ix.Titles[rec.PrimaryTitle].Key);
        }
        Console.WriteLine($"      {s.FileName}: player {p.Id} age {p.Age:0.00}, {ix.Summary()}, {s.ReadTime.TotalMilliseconds:0} ms");
    }

    private static void BinarySave()
    {
        var s = State("03");
        if (s == null) { Console.WriteLine("      no binary (type 03) save present: skipped"); return; }
        Expect.True(s.Binary, "gamestate detected as binary");
        var p = Expect.NotNull(s.Meta.Player);
        Expect.True(p.Genes.Count >= 100 && p.Id > 0 && p.Age > 0, $"binary meta portrait: id {p.Id}, {p.Genes.Count} genes, age {p.Age}");
        var coa = Expect.NotNull(s.TitleCoa, "binary meta title CoA");
        Expect.True(coa.Pattern.StartsWith("pattern", StringComparison.Ordinal), $"pattern {coa.Pattern}");
        var ix = Expect.NotNull(s.Index);
        Expect.True(ix.Titles.Count > 1000 && (ix.Coas?.Count ?? 0) > 1000, ix.Summary());
        int withCoa = ix.Titles.Values.Count(t => t.CoaId >= 0);
        Expect.True(withCoa > ix.Titles.Count / 2, $"titles with a CoA id: {withCoa}/{ix.Titles.Count}");
        var any = ix.Titles.Values.First(t => t.CoaId >= 0 && t.Key!.StartsWith("k_", StringComparison.Ordinal));
        Expect.NotNull(ix.Coas!.Get(any.CoaId, "title:" + any.Key), $"CoA of {any.Key}");
        Console.WriteLine($"      {s.FileName}: player {p.Id}, {ix.Summary()}, {s.ReadTime.TotalMilliseconds:0} ms");
    }

    private static void ZippedTextOtherMod()
    {
        var s = State("02");
        if (s == null) { Console.WriteLine("      no zipped text (type 02) save present: skipped"); return; }
        Expect.False(s.Binary, "type 02 gamestate is text");
        var p = Expect.NotNull(s.Meta.Player);
        Expect.True(p.Genes.Count > 0, "portrait genes (other mod's gene set)");
        var ix = Expect.NotNull(s.Index);
        Expect.True(ix.Characters.Count > 100, ix.Summary());
        // A character from another mod's playset must not break the look builder (unknown genes are ignored).
        var look = Engine.Value.Looks.Build(new LookInput { Ck3Id = p.Id, ApparentAge = 30, Genes = p.Genes, IsPortrait = true, Source = "meta_portrait" }, out _);
        Expect.True(look.Weight is >= 0 and <= 1);
        Console.WriteLine($"      {s.FileName}: {s.Meta.GameVersion}, {ix.Summary()}");
    }

    // ------------------------------------------------------------------ genes

    private static void DnaMatchesMeta()
    {
        var s = State("00");
        if (s == null) { Console.WriteLine("      no text save: skipped"); return; }
        var p = s.Meta.Player!;
        var dna = s.Index!.Character(p.Id)?.Dna;
        if (dna == null) { Console.WriteLine("      player has no stored dna in this save: skipped"); return; }
        var decoded = Expect.NotNull(GeneSet.FromDna(dna, Engine.Value.Genes!, out var err), err);
        int same = 0, total = 0;
        foreach (var (name, c) in decoded.Colors) { total++; if (p.Genes.Color(name) is ColorGene m && m == c) same++; }
        foreach (var (name, g) in decoded.Morphs) { total++; if (p.Genes.Morph(name) is MorphGene m && m == g) same++; }
        // Measured on a real save: 100 of 104 identical; the rest are portrait-modifier genes (weight, muscle, bust, wrinkles).
        Expect.Equal(104, total);
        Expect.True(same >= 96, $"{same}/104 genes identical between dna= and the meta portrait");
        Console.WriteLine($"      {same}/{total} genes identical");
    }

    private static void StandInStable()
    {
        var eth = Engine.Value.Ethnicities!;
        var a = eth.Generate("south_indian", 22690, Array.Empty<string>());
        var b = eth.Generate("south_indian", 22690, Array.Empty<string>());
        var c = eth.Generate("south_indian", 22691, Array.Empty<string>());
        Expect.True(a.Count >= 100, $"stand-in has {a.Count} genes");
        Expect.Equal(a.Serialize(), b.Serialize(), "same id -> same face");
        Expect.True(a.Serialize() != c.Serialize(), "different id -> different face");
        var unknown = eth.Generate("no_such_ethnicity", 5, Array.Empty<string>());
        Expect.True(unknown.Count > 50, "unknown ethnicity falls back to the template");
        var child = EthnicityDb.Inherit(a, c, null, 99);
        Expect.Equal(a.Count, child.Count);
        Expect.True(child.Morphs.All(kv => kv.Value.Value is >= 0 and <= 255));
    }

    private static void GeneRoundtrip()
    {
        var a = Engine.Value.Ethnicities!.Generate("caucasian_northern_blond", 1234, new[] { "beauty_good_2" });
        var back = GeneSet.Deserialize(a.Serialize());
        Expect.Equal(a.Serialize(), back.Serialize());
        var dir = Path.Combine(OutDir, "cache_roundtrip");
        Directory.CreateDirectory(dir);
        var cache = new AppearanceCache(Path.Combine(dir, "appearance_cache.json"));
        cache.Put(new CharacterFacts { Id = 42, Genes = a.Serialize(), GeneSource = "save_dna", PrimaryTitleKey = "k_test",
            TitleCoa = new CoaModel { Pattern = "pattern_solid", Colors = new CoaColor?[] { new() { Name = "red", R = 115, G = 34, B = 23 }, null, null } } });
        cache.Track(new long[] { 42 });
        cache.Save(_ => { });
        var reloaded = new AppearanceCache(cache.Path);
        reloaded.Load(m => throw new ExpectationException(m));
        Expect.Equal(a.Serialize(), reloaded.Character(42)?.Genes);
        Expect.Equal("red", reloaded.TitleCoa("k_test")?.Colors[0]?.Name);
    }

    // ------------------------------------------------------------------ DDS, PNG, banners

    private static void DdsFormats()
    {
        string root = Path.Combine(Engine.Value.Files!.GameDir, "gfx", "coat_of_arms");
        var found = new Dictionary<string, string>();
        foreach (var f in Directory.EnumerateFiles(root, "*.dds", SearchOption.AllDirectories))
        {
            var img = DdsImage.Parse(File.ReadAllBytes(f));
            if (img != null) found.TryAdd(img.FormatName, f);
            if (found.Count == 3) break;
        }
        foreach (var fmt in new[] { "Dxt1", "Dxt5", "Bgra32" })
        {
            var path = Expect.NotNull(found.GetValueOrDefault(fmt), $"a {fmt} texture in the install");
            var dds = DdsImage.Load(path);
            var img = dds.Decode(0);
            Expect.True(img.Width == dds.Width && img.Pixels.Length == img.Width * img.Height * 4);
            Expect.True(img.Pixels.Where((_, i) => i % 4 == 3).Any(a => a > 0), $"{fmt}: some opaque pixels");
            var small = dds.Decode(dds.MipCount - 1);
            Expect.True(small.Width <= img.Width);
        }
    }

    private static void PlayerPng()
    {
        var s = State("00") ?? State("03") ?? State("02");
        if (s == null) { Console.WriteLine("      no save present: skipped"); return; }
        var coa = Expect.NotNull(s.TitleCoa, "player's primary-title CoA from the meta");
        string dir = Path.Combine(OutDir, "coa");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        string path = Engine.Value.Renderer.RenderToFile(coa, dir, 256);
        var bytes = File.ReadAllBytes(path);
        Expect.True(bytes.Length > 1000, $"PNG size {bytes.Length}");
        Expect.True(bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), "PNG signature");
        Expect.Equal(256, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)));
        var img = Engine.Value.Renderer.Render(coa, 64, shieldMask: true);
        int opaque = 0;
        var colors = new HashSet<int>();
        for (int i = 0; i < img.Pixels.Length; i += 4)
            if (img.Pixels[i + 3] > 200) { opaque++; colors.Add(img.Pixels[i] << 16 | img.Pixels[i + 1] << 8 | img.Pixels[i + 2]); }
        Expect.True(opaque > 64 * 64 / 2, $"opaque pixels {opaque}");
        Expect.True(colors.Count >= 2 || coa.Emblems.Count == 0, "more than one colour");
        Console.WriteLine($"      {coa.Source}: {path}");
    }

    private static void BannerCodes()
    {
        var e = Engine.Value;
        var coas = new List<CoaModel>();
        foreach (var t in new[] { "00", "03", "02" })
        {
            var s = State(t);
            if (s == null) continue;
            if (s.TitleCoa != null) coas.Add(s.TitleCoa);
            if (s.HouseCoa != null) coas.Add(s.HouseCoa);
            if (s.Index?.Coas is CoaStore store)
                foreach (long id in store.Ids.Where(i => i % 53 == 0).Take(150))
                    if (store.Get(id, "db:" + id) is CoaModel m) coas.Add(m);
        }
        foreach (var key in new[] { "k_france", "e_byzantium", "e_hre", "k_england", "d_bavaria" })
            if (e.GameCoas.Get(key, "game:" + key) is CoaModel g) coas.Add(g);
        Expect.True(coas.Count > 20, $"{coas.Count} coats of arms to convert");
        var palette = BridgeData.Open(null, _ => { })!.LoadBannerPalette().Select(p => p.Id).ToHashSet();
        foreach (var coa in coas)
        {
            var b = e.Banners.Build(coa);
            var tokens = b.Code.Split('.');
            Expect.True(tokens.Length % 10 == 0, $"{coa.Source}: {tokens.Length} tokens");
            Expect.True(tokens.All(t => int.TryParse(t, NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)), $"{coa.Source}: integers only: {b.Code}");
            Expect.True(tokens.Length / 10 is >= 1 and <= 33, $"{coa.Source}: {tokens.Length / 10} layers");
            Expect.Equal("11", tokens[0], $"{coa.Source}: background mesh");
            for (int i = 0; i < tokens.Length; i += 10)
                Expect.True(palette.Contains(int.Parse(tokens[i + 1])) && palette.Contains(int.Parse(tokens[i + 2])), $"{coa.Source}: palette colours");
        }
        Console.WriteLine($"      {coas.Count} banners valid");
    }

    private static void SideClash()
    {
        var e = Engine.Value;
        var red = new CoaModel { Pattern = "pattern_solid", Colors = new[] { e.Colors.ResolveName("red"), e.Colors.ResolveName("yellow"), null },
                                 Emblems = { new CoaEmblem { Texture = "ce_lion_rampant", Colors = new[] { e.Colors.ResolveName("yellow"), null, null }, Instances = { new CoaInstance() } } } };
        var mine = e.Banners.Build(red);
        var enemy = e.Banners.Build(red);
        Expect.NotNull(e.Banners.ResolveClash(mine, enemy), "identical banners must be told apart");
        Expect.True(enemy.PrimaryColor != mine.PrimaryColor, "enemy primary colour changed");
        var blue = new CoaModel { Pattern = "pattern_solid", Colors = new[] { e.Colors.ResolveName("blue"), null, null } };
        Expect.Null(e.Banners.ResolveClash(e.Banners.Build(red), e.Banners.Build(blue)), "red vs blue does not clash");
    }

    // ------------------------------------------------------------------ equipment

    private static void EquipmentKits()
    {
        var planner = Engine.Value.Equipment;
        var rosters = BridgeData.Open(null, _ => { })!.LoadKits().Rosters;
        foreach (var culture in new[] { "vlandia", "empire", "sturgia", "battania", "khuzait", "aserai" })
            foreach (int tier in new[] { 0, 1, 2, 3, 4, 5 })
                foreach (bool female in new[] { false, true })
                {
                    var eq = Expect.NotNull(planner.Plan(null, culture, tier, female, captain: true, 1000 + tier, null, out var notes), $"{culture} t{tier}");
                    Expect.True(rosters.ContainsKey(eq.Roster), $"{culture} t{tier}: roster {eq.Roster}");
                    Expect.True(eq.Overrides.ContainsKey("Item4"), $"{culture} t{tier}: captain banner ({notes})");
                    if (tier >= 4) Expect.True(eq.Roster.Contains("king_template_bat"), $"{culture} t{tier}: {eq.Roster}");
                }
        var khu = planner.Plan(null, "khuzait", 4, false, false, 7, null, out var khuNotes)!;
        var shields = BridgeData.Open(null, _ => { })!.LoadKits().Shields.Select(s => s.Id).ToHashSet();
        Expect.True(khu.Overrides.Values.Any(shields.Contains), $"khuzait king kit gets a heraldic shield: {khuNotes}");
        // The mapper's roster and its overrides (foot nobles: emptied horse slots) are kept; only additions are made.
        var mapper = new EquipmentSpec { Roster = "stu_bat_template_medium", Overrides = { ["Horse"] = "", ["HorseHarness"] = "" } };
        var kept = planner.Plan(mapper, "vlandia", 2, false, true, 11, null, out var keptNotes)!;
        Expect.Equal("stu_bat_template_medium", kept.Roster);
        Expect.Equal("", kept.Overrides["Horse"], keptNotes);
        Expect.True(kept.Overrides.ContainsKey("Item4"), $"captain banner added: {keptNotes}");
        Expect.True(kept.Overrides["Item4"].Contains("shield") || kept.Overrides["Item4"].Contains("banner"), "a sturgian banner item (roster culture wins)");
        var skills = EquipmentPlanner.Skills(20, 12, 4, "vlandia");
        Expect.Equal(220, skills["OneHanded"]);
        Expect.Equal(120, skills["Tactics"]);
    }

    private static void WatcherTarget()
    {
        if (!Directory.Exists(Docs)) { Console.WriteLine("      no CK3 documents folder: skipped"); return; }
        using var w = new SaveWatcher(Docs, _ => true, _ => { });
        var target = w.ResolveTarget();
        Expect.NotNull(target, "a save to read");
        Expect.True(File.Exists(target), target);
        Console.WriteLine($"      fresh save: {target}");
    }

    // ------------------------------------------------------------------ provider

    private static void EnrichSynthetic()
    {
        var type = new[] { "00", "03", "02" }.FirstOrDefault(t => SavesByType.Value.ContainsKey(t));
        if (type == null) { Console.WriteLine("      no save present: skipped"); return; }
        string cacheDir = Path.Combine(OutDir, "provider");
        if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        using var provider = new AppearanceProvider();
        provider.Start("", GameDir.Value!, cacheDir);           // no documents folder: no watcher, the test feeds the save
        var s = Expect.NotNull(provider.LoadSaveNow(SavesByType.Value[type][0]));
        var player = s.Meta.Player!;
        var ix = s.Index!;
        // A knight without stored DNA and an enemy ruler with a title, from the same save when it is a text save.
        var knight = ix.Characters.Values.FirstOrDefault(c => c.Dna == null && c.Ethnicity != null && c.Id != player.Id);
        var ruler = ix.Characters.Values.FirstOrDefault(c => c.Id != player.Id && c.PrimaryTitle >= 0 && ix.Titles.ContainsKey(c.PrimaryTitle));
        long knightId = knight?.Id ?? 990001, rulerId = ruler?.Id ?? 990002;
        string? rulerTitle = ruler != null ? ix.Titles[ruler.PrimaryTitle].Key : "k_france";

        var battle = new BattleSnapshot { Bid = 1, SessionKey = "self", Year = s.Meta.Date?.Year ?? 870, Month = 6, Day = 1, PlayerCk3Id = player.Id };
        battle.Defender.PlayerIn = true;
        battle.Defender.Primary = new CharacterRef { Ck3Id = player.Id };
        int playerAge = LiveAge(s, player);
        battle.Defender.Characters.Add(Character(player.Id, true, playerAge, player.Female, 4, "south_indian"));
        battle.Defender.Characters.Add(Character(knightId, false, 35, false, 1, knight?.Ethnicity ?? "south_indian", "physique_good_1"));
        battle.Attacker.Primary = new CharacterRef { Ck3Id = rulerId, TitleKey = rulerTitle };
        battle.Attacker.Characters.Add(Character(rulerId, false, 45, ruler?.Female ?? false, 3, ruler?.Ethnicity ?? "byzantine", title: rulerTitle));
        battle.Attacker.Characters.Add(Character(990003, false, 30, false, 0, null)); // unknown everywhere: still gets a look
        var request = new BattleRequest
        {
            Id = "b1_self", PlayerSide = "defender",
            Defender = new SideRequest { Name = "Host", Culture = "aserai",
                Heroes = { Hero("h0", player.Id, "player", "Gunda {II}."), Hero("h1", knightId, "knight", "Nachana") } },
            Attacker = new SideRequest { Name = "Foe", Culture = "vlandia",
                Heroes = { Hero("e0", rulerId, "primary", "Nachana"), Hero("e1", 990003, "knight", "") } },
        };
        var sw = Stopwatch.StartNew();
        provider.Enrich(battle, request);
        Expect.True(sw.ElapsedMilliseconds < 5000, $"enrich took {sw.ElapsedMilliseconds} ms");

        var heroes = request.Attacker.Heroes.Concat(request.Defender.Heroes).ToList();
        foreach (var h in heroes)
        {
            var a = Expect.NotNull(h.Appearance, $"{h.Key} appearance");
            Expect.True(a.Source is "meta_portrait" or "save_dna" or "parent_inherited" or "ethnicity_seeded", $"{h.Key}: source {a.Source}");
            foreach (var v in new[] { a.Weight, a.Build, a.Height, a.Skin, a.HairColor, a.EyeColor })
                Expect.True(v is >= 0 and <= 1, $"{h.Key}: value {v} outside 0..1");
            Expect.True(a.Morphs.Count >= 30 && a.Morphs.Values.All(m => m is >= -1 and <= 1), $"{h.Key}: {a.Morphs.Count} morphs");
            Expect.True(a.Hair.Index != null || a.Hair.Tags.Count > 0, $"{h.Key}: hair");
            Expect.NotNull(h.Equipment?.Roster, $"{h.Key}: equipment roster");
            Expect.True(h.Skills.Count >= 8, $"{h.Key}: skills");
            Expect.False(h.Name.Contains('{') || h.Name.Contains('}') || h.Name.Contains('.'), $"{h.Key}: name '{h.Name}'");
        }
        Expect.Equal("meta_portrait", request.Defender.Heroes[0].Appearance.Source, "player look comes from the save meta");
        Expect.Equal(heroes.Count, heroes.Select(h => h.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "unique names");
        var def = Expect.NotNull(request.Defender.Banner, "player side banner");
        Expect.Equal("meta:primary_title", def.Source);
        Expect.True(def.Code.Split('.').Length % 10 == 0);
        Expect.True(File.Exists(request.Presentation.DefenderCoaPng), "defender CoA PNG");
        if (request.Attacker.Banner != null) Expect.True(File.Exists(request.Presentation.AttackerCoaPng), "attacker CoA PNG");
        // Heroes' own arms (PROTOCOL §25): the side's primary flies the side banner, anyone else only arms different from it.
        Expect.Null(request.Attacker.Heroes[0].Banner, "the attacker's primary carries the side's arms");
        Expect.Null(request.Defender.Heroes[0].Banner, "the player, the defender's primary, carries the side's arms");
        foreach (var (side, h) in new[] { request.Attacker, request.Defender }.SelectMany(sd => sd.Heroes.Select(h => (sd, h))).Where(x => x.h.Banner != null))
        {
            Expect.True(h.Banner!.Code.Split('.').Length % 10 == 0, $"{h.Key}: banner code");
            Expect.True(h.Banner.Source != side.Banner?.Source, $"{h.Key}: arms of their own");
            Console.WriteLine($"      {h.Key} carries {h.Banner.Source}");
        }
        Expect.True(File.Exists(Path.Combine(cacheDir, "appearance_cache.json")), "cache written");
        foreach (var h in heroes)
            Console.WriteLine($"      {h.Key} {h.Ck3Id} '{h.Name}': {h.Appearance.Source}, age {h.Age}, skin {h.Appearance.Skin}, hair {h.Appearance.Hair.Index}/{string.Join("|", h.Appearance.Hair.Tags)}, beard {h.Appearance.Beard.Index}, kit {h.Equipment.Roster}");
        Console.WriteLine($"      banners: att {request.Attacker.Banner?.Source} | def {def.Source} {def.Code}");
    }

    /// <summary>The real pipeline order: the mapper builds the request, then Enrich adds looks and banners on top of it.</summary>
    private static void EnrichAfterMapper()
    {
        var type = new[] { "00", "03", "02" }.FirstOrDefault(t => SavesByType.Value.ContainsKey(t));
        if (type == null) { Console.WriteLine("      no save present: skipped"); return; }
        string cacheDir = Path.Combine(OutDir, "after_mapper");
        if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        using var provider = new AppearanceProvider();
        provider.Start("", GameDir.Value!, cacheDir);
        var s = Expect.NotNull(provider.LoadSaveNow(SavesByType.Value[type][0]));
        long playerId = s.Meta.Player!.Id;
        var saveDate = s.Meta.Date ?? new CK3Date(871, 2, 1);

        var telugu = new Core.Model.CultureInfo { Culture = "telugu", Heritage = "heritage_dravidian", UnitGfx = "indian_unit_gfx", ClothingGfx = "indian_clothing_gfx", Era = "culture_era_tribal" };
        var me = new CharacterRef { Ck3Id = playerId, Name = "Gunda Kakatiya", TitleKey = "c_orangallu" };
        var foe = new CharacterRef { Ck3Id = 10935, Name = "Bhupati Phoro", TitleKey = "k_france" };
        CharacterSnapshot Person(CharacterRef r, int tag, bool cmd, bool primary, int army, bool player = false) => new()
        {
            Ref = r, Tag = tag, Role = cmd ? "CMD" : "KNIGHT", IsCommander = cmd, IsKnight = !cmd, IsPrimary = primary, IsPlayer = player,
            Culture = telugu, Ethnicity = "south_indian", Prowess = 12, Martial = 8, Age = player ? LiveAge(s, s.Meta.Player!) : 50,
            Female = player && s.Meta.Player!.Female,
            Tier = primary ? 2 : 0, ArmyTag = army,
            PrimaryTitleKey = primary ? r.TitleKey : null,
        };
        ArmySnapshot Army(int tag, CharacterRef owner)
        {
            var a = new ArmySnapshot { ArmyId = 16777000 + tag, Name = owner.Name + "'s Host", Tag = tag, Levies = 900, Owner = owner, Commander = owner, OwnerCulture = telugu };
            a.Regiments.Add(new RegimentSnapshot { RegimentId = 16700 + tag, MaaKey = "bowmen", Current = 200, Max = 200 });
            a.Size = 1100;
            return a;
        }
        var battle = new BattleSnapshot
        {
            // Fought on the save date, so the live player record (age, sex) agrees with whichever campaign saved last.
            Bid = 3, SessionKey = "self", Year = saveDate.Year, Month = Math.Max(1, saveDate.Month), Day = Math.Max(1, saveDate.Day),
            PlayerCk3Id = playerId, Complete = true,
            Location = new LocationInfo { ProvinceId = 1256, ProvinceName = "Orangallu", CountyKey = "c_orangallu", Terrain = "hills" },
        };
        battle.Defender.PlayerIn = true;
        battle.Defender.Primary = me;
        battle.Defender.Armies.Add(Army(2, me));
        battle.Defender.Characters.Add(Person(me, 3, cmd: true, primary: true, army: 2, player: true));
        battle.Defender.Characters.Add(Person(new CharacterRef { Ck3Id = 22690, Name = "Nachana" }, 5, cmd: false, primary: false, army: 2));
        battle.Defender.Size = 1100;
        battle.Attacker.Primary = foe;
        battle.Attacker.Armies.Add(Army(1, foe));
        battle.Attacker.Characters.Add(Person(foe, 1, cmd: true, primary: true, army: 1));
        battle.Attacker.Size = 1100;

        var request = new CK3xBL.Mapping.BattleMapper().BuildRequest(battle, new CK3xBL.Core.Abstractions.MapperOptions());
        var rosterBefore = request.Defender.Heroes.ToDictionary(h => h.Key, h => h.Equipment?.Roster);
        provider.Enrich(battle, request);

        var heroes = request.Attacker.Heroes.Concat(request.Defender.Heroes).ToList();
        Expect.True(heroes.Count >= 3, $"{heroes.Count} heroes");
        foreach (var h in request.Defender.Heroes)
            Expect.Equal(rosterBefore[h.Key], h.Equipment.Roster, $"{h.Key}: the mapper's roster is kept");
        Expect.Equal("meta_portrait", heroes.Single(h => h.Ck3Id == playerId).Appearance.Source);
        Expect.True(heroes.All(h => h.Appearance != null && h.Appearance.Morphs.Count > 0), "every CK3 hero has a look");
        Expect.NotNull(request.Defender.Banner, "player banner");
        // Serialised exactly as the bridge writes request.json (camelCase members, verbatim dictionary keys).
        var json = System.Text.Json.JsonSerializer.Serialize(request, new System.Text.Json.JsonSerializerOptions
            { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, IncludeFields = true });
        Expect.True(json.Contains("\"appearance\":{\"source\":\"meta_portrait\"", StringComparison.Ordinal), "appearance serialised");
        Expect.True(json.Contains("\"face_width\":", StringComparison.Ordinal) && json.Contains("\"Item4\":", StringComparison.Ordinal), "dictionary keys verbatim");
        Console.WriteLine($"      {heroes.Count} heroes; attacker banner {request.Attacker.Banner?.Source ?? "culture"}, defender {request.Defender.Banner!.Source}; {json.Length} B of JSON");
    }

    private static void EnrichWithoutData()
    {
        using var provider = new AppearanceProvider { DataDirectory = Path.Combine(OutDir, "no_such_dir") };
        provider.Start(Path.Combine(OutDir, "no_docs"), Path.Combine(OutDir, "no_game"), Path.Combine(OutDir, "empty_cache"));
        var battle = new BattleSnapshot { Bid = 2 };
        var request = new BattleRequest { Id = "b2", Attacker = new SideRequest { Heroes = { Hero("e0", 1, "primary", "A") } }, Defender = new SideRequest() };
        provider.Enrich(battle, request);   // must not throw
        Expect.Equal("stock", request.Attacker.Heroes[0].Appearance?.Source);
        Expect.Null(request.Attacker.Banner);
    }

    /// <summary>The player's live age as CK3 would log it at the save date (real age from the birth date when known).</summary>
    private static int LiveAge(SaveState s, MetaPortrait p) =>
        s.Index?.Character(p.Id)?.Birth is string b && CK3Date.Parse(b) is CK3Date birth && s.Meta.Date is CK3Date d
            ? (int)Math.Floor(d.AsYears - birth.AsYears)
            : (int)Math.Round(p.Age * 100);

    /// <summary>Character ids are reused between campaigns: a save whose player disagrees with the live record is ignored.</summary>
    private static void OtherCampaignSaveIgnored()
    {
        var type = new[] { "00", "03", "02" }.FirstOrDefault(t => SavesByType.Value.ContainsKey(t));
        if (type == null) { Console.WriteLine("      no save present: skipped"); return; }
        string cacheDir = Path.Combine(OutDir, "other_campaign");
        if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        using var provider = new AppearanceProvider();
        provider.Start("", GameDir.Value!, cacheDir);
        var s = Expect.NotNull(provider.LoadSaveNow(SavesByType.Value[type][0]));
        var player = s.Meta.Player!;
        var battle = new BattleSnapshot { Bid = 4, SessionKey = "self", Year = (s.Meta.Date?.Year ?? 870) + 1, Month = 1, Day = 1, PlayerCk3Id = player.Id };
        battle.Defender.PlayerIn = true;
        battle.Defender.Primary = new CharacterRef { Ck3Id = player.Id };
        // Same internal id, but a 19-year-old of the other sex: a different person in a new campaign.
        battle.Defender.Characters.Add(Character(player.Id, true, 19, !player.Female, 2, "byzantine"));
        var request = new BattleRequest { Id = "b4_self", PlayerSide = "defender", Attacker = new SideRequest(),
            Defender = new SideRequest { Culture = "empire", Heroes = { Hero("h0", player.Id, "player", "Newcomer") } } };
        provider.Enrich(battle, request);
        var h0 = request.Defender.Heroes[0];
        Expect.Equal("ethnicity_seeded", h0.Appearance?.Source, "stand-in instead of the other campaign's portrait");
        Expect.Equal(!player.Female, h0.Female);
        Expect.True(request.Defender.Banner == null || request.Defender.Banner.Source != "meta:primary_title", "no banner from the other campaign");
        Expect.False(File.Exists(Path.Combine(cacheDir, "appearance_cache.json")) &&
                     File.ReadAllText(Path.Combine(cacheDir, "appearance_cache.json")).Contains("Newcomer"), "cache untouched by the battle");
    }

    // ------------------------------------------------------------------ regression fixes

    /// <summary>Binary records do not tell the sex: it stays unknown (never "male"), so women keep their stored DNA.</summary>
    private static void BinarySexUnknown()
    {
        if (State("00") is SaveState text)
        {
            var recs = text.Index!.Characters.Values;
            Expect.True(recs.All(c => c.Female != null), "text records always tell the sex");
            Expect.True(recs.Any(c => c.Female == true) && recs.Any(c => c.Female == false), "both sexes in a text save");
        }
        using var provider = ProviderWith("binary_sex", out var s, "03");
        if (provider == null) { Console.WriteLine("      no binary (type 03) save present: skipped"); return; }
        var ix = s.Index!;
        var player = s.Meta.Player!;
        Expect.True(ix.Characters.Count > 0 && ix.Characters.Values.All(c => c.Female == null), "binary records do not tell the sex");
        long dnaId = ix.Characters.Keys.First(id => id != player.Id && id != s.Meta.Heir?.Id);
        Expect.Null(Engine.Value.FactsFromSave(s, dnaId)?.Female, "unknown sex stays unknown in the facts");
        foreach (bool female in new[] { true, false })
        {
            var battle = BattleAtSave(s, 10, player.Id);
            battle.Defender.PlayerIn = true;
            battle.Defender.Characters.Add(Character(player.Id, true, LiveAge(s, player), player.Female, 4, "south_indian"));
            battle.Defender.Characters.Add(Character(dnaId, false, 30, female, 1, "south_indian"));
            var request = new BattleRequest { Id = "b10_self", PlayerSide = "defender", Attacker = new SideRequest(),
                Defender = new SideRequest { Culture = "aserai", Heroes = { Hero("h0", player.Id, "player", "Me"), Hero("h1", dnaId, "knight", "Knight") } } };
            provider.Enrich(battle, request);
            Expect.Equal("save_dna", request.Defender.Heroes[1].Appearance?.Source, $"{(female ? "woman" : "man")} with stored dna");
            Expect.Equal(female, request.Defender.Heroes[1].Female);
        }
        var cache = new AppearanceCache(Path.Combine(OutDir, "binary_sex", "appearance_cache.json"));
        cache.Load(m => throw new ExpectationException(m));
        Expect.Null(Expect.NotNull(cache.Character(dnaId), "knight cached after the battle").Female, "no guessed sex in the cache");
    }

    /// <summary>The save and the cache are used only on positive evidence that they describe the live player.</summary>
    private static void TrustNeedsEvidence()
    {
        const string cacheName = "trust";
        using var provider = ProviderWith(cacheName, out var s, "03", "00", "02");
        if (provider == null) { Console.WriteLine("      no save present: skipped"); return; }
        var ix = s.Index!;
        var player = s.Meta.Player!;
        // A man with stored dna: what an id collision with another campaign's save looks like.
        var knight = ix.Characters.Values.First(c => c.Dna != null && c.Female != true && c.Id != player.Id && c.Id != s.Meta.Heir?.Id);
        int knightAge = knight.Birth is string kb && CK3Date.Parse(kb) is CK3Date born && s.Meta.Date is CK3Date at
            ? (int)Math.Floor(at.AsYears - born.AsYears) : 40;
        const long foeId = 990010, newPlayer = 987654321;
        string cachePath = Path.Combine(OutDir, cacheName, "appearance_cache.json");

        BattleRequest Fight(AppearanceProvider p, long playerId, CharacterSnapshot? live)
        {
            var battle = BattleAtSave(s, 11, playerId);
            battle.Defender.PlayerIn = live != null;
            if (live != null) battle.Defender.Characters.Add(live);
            battle.Defender.Characters.Add(Character(knight.Id, false, knightAge, false, 1, "byzantine"));
            battle.Attacker.Primary = new CharacterRef { Ck3Id = foeId, TitleKey = "k_france" };
            battle.Attacker.Characters.Add(Character(foeId, false, 45, false, 4, "french", title: "k_france"));
            var request = new BattleRequest { Id = "b11_self", PlayerSide = "defender",
                Attacker = new SideRequest { Culture = "vlandia", Heroes = { Hero("e0", foeId, "primary", "Foe") } },
                Defender = new SideRequest { Culture = "empire", Heroes = { Hero("h1", knight.Id, "knight", "Knight") } } };
            if (live != null) request.Defender.Heroes.Insert(0, Hero("h0", playerId, "player", "Me"));
            p.Enrich(battle, request);
            return request;
        }
        string? KnightSource(BattleRequest r) => r.Defender.Heroes.Single(h => h.Ck3Id == knight.Id).Appearance?.Source;
        var realPlayer = Character(player.Id, true, LiveAge(s, player), player.Female, 4, "south_indian");
        var newcomer = Character(newPlayer, true, 30, false, 3, "byzantine");

        // A new campaign before its first save: the live player is not in the newest save (present in the battle or not).
        byte[] cacheBefore = File.ReadAllBytes(cachePath);
        foreach (var live in new[] { newcomer, null })
        {
            var r = Fight(provider, newPlayer, live);
            Expect.Equal("ethnicity_seeded", KnightSource(r), "no face from an unrelated save");
            Expect.Equal("game:k_france", r.Attacker.Banner?.Source, "coat of arms from the game files, not the unrelated save");
            Expect.True(File.ReadAllBytes(cachePath).AsSpan().SequenceEqual(cacheBefore), "cache untouched");
        }
        // The save's own player: save and cache are used, and the battle's characters are cached.
        var trusted = Fight(provider, player.Id, realPlayer);
        Expect.Equal("meta_portrait", trusted.Defender.Heroes[0].Appearance?.Source);
        Expect.Equal("save_dna", KnightSource(trusted));

        // No save read yet (bridge just started): the cache counts only for the player it was built for.
        using var cacheOnly = new AppearanceProvider();
        cacheOnly.Start("", GameDir.Value!, Path.Combine(OutDir, cacheName));
        var known = Fight(cacheOnly, player.Id, realPlayer);
        Expect.Null(cacheOnly.CurrentSave, "no save read");
        Expect.Equal("meta_portrait", known.Defender.Heroes[0].Appearance?.Source, "cached portrait of the same player");
        Expect.Equal("save_dna", KnightSource(known), "cached dna of a knight met before");
        var stranger = Fight(cacheOnly, newPlayer, newcomer);
        Expect.Equal("ethnicity_seeded", KnightSource(stranger), "another player: the cache is not used");
        Console.WriteLine($"      {s.FileName}: knight {knight.Id}; foe banner untrusted {Fight(provider, newPlayer, null).Attacker.Banner?.Source}, trusted {trusted.Attacker.Banner?.Source}");
    }

    /// <summary>Binary saves do not tell the meta CoA's title: the live title is looked up in the save's CoA database.</summary>
    private static void BinaryLiveTitle()
    {
        using var provider = ProviderWith("binary_title", out var s, "03");
        if (provider == null) { Console.WriteLine("      no binary (type 03) save present: skipped"); return; }
        var player = s.Meta.Player!;
        var ix = s.Index!;
        string other = ix.TitleCoaId("k_france") >= 0 ? "k_france" : ix.TitleIdByKey.Keys.First(k => k.StartsWith("k_", StringComparison.Ordinal) && ix.TitleCoaId(k) >= 0);
        string? BannerSource(string? liveTitle)
        {
            var battle = BattleAtSave(s, 13, player.Id);
            battle.Defender.PlayerIn = true;
            battle.Defender.Primary = new CharacterRef { Ck3Id = player.Id, TitleKey = liveTitle };
            battle.Defender.Characters.Add(Character(player.Id, true, LiveAge(s, player), player.Female, 4, "south_indian", title: liveTitle));
            var request = new BattleRequest { Id = "b13_self", PlayerSide = "defender", Attacker = new SideRequest(),
                Defender = new SideRequest { Culture = "aserai", Heroes = { Hero("h0", player.Id, "player", "Me") } } };
            provider.Enrich(battle, request);
            return request.Defender.Banner?.Source;
        }
        Expect.Equal("meta:primary_title", BannerSource(null), "no live title: the save header's CoA");
        Expect.Equal("title:" + other, BannerSource(other), "a primary title gained since the save: its CoA from the save database");
    }

    /// <summary>A PNG that fails to render (any exception) costs neither side its banner, and the cache is still updated.</summary>
    private static void PngFailureKeepsBanners()
    {
        const string cacheName = "png_failure";
        using var provider = ProviderWith(cacheName, out var s, "00", "03", "02");
        if (provider == null) { Console.WriteLine("      no save present: skipped"); return; }
        // 32768^2 * 4 overflows the canvas size to 0: rendering throws IndexOutOfRangeException (not an IO error).
        provider.CoaPngSize = 1 << 15;
        var player = s.Meta.Player!;
        const long foeId = 990020;
        var battle = BattleAtSave(s, 14, player.Id);
        battle.Defender.PlayerIn = true;
        battle.Defender.Characters.Add(Character(player.Id, true, LiveAge(s, player), player.Female, 4, "south_indian"));
        battle.Attacker.Primary = new CharacterRef { Ck3Id = foeId, TitleKey = "k_france" };
        battle.Attacker.Characters.Add(Character(foeId, false, 45, false, 4, "french", title: "k_france"));
        var request = new BattleRequest { Id = "b14_self", PlayerSide = "defender",
            Attacker = new SideRequest { Culture = "vlandia", Heroes = { Hero("e0", foeId, "primary", "Foe") } },
            Defender = new SideRequest { Culture = "aserai", Heroes = { Hero("h0", player.Id, "player", "Me") } } };
        provider.Enrich(battle, request);
        Expect.NotNull(request.Attacker.Banner, "attacker banner");
        Expect.NotNull(request.Defender.Banner, "defender banner");
        Expect.Null(request.Presentation.AttackerCoaPng);
        Expect.Null(request.Presentation.DefenderCoaPng);
        var cache = new AppearanceCache(Path.Combine(OutDir, cacheName, "appearance_cache.json"));
        cache.Load(m => throw new ExpectationException(m));
        Expect.True(cache.TrackedIds().Contains(foeId), "battle characters still cached");
    }

    /// <summary>Table values cannot push a banner past the 33 layers a banner code may hold.</summary>
    private static void BannerLayerLimit()
    {
        var e = Engine.Value;
        var data = BridgeData.Open(null, _ => { })!;
        using var table = System.Text.Json.JsonDocument.Parse("{\"max_division_layers\": 1000, \"max_icon_layers\": 1000}");
        var builder = new BannerBuilder(data.LoadBannerPalette(), data.LoadNamedColorMap(), new(), table, e.Textures);
        CoaColor? C(string n) => e.Colors.ResolveName(n);
        var coa = new CoaModel { Pattern = "pattern_solid", Colors = new[] { C("white"), null, null } };
        // The same bars repainted in alternating colours: every repaint is a visible division of its own.
        for (int i = 0; i < 20; i++)
            coa.Emblems.Add(new CoaEmblem { Texture = "ce_barry_06", Colors = new[] { C(i % 2 == 0 ? "red" : "blue"), null, null }, Instances = { new CoaInstance() } });
        for (int i = 0; i < 40; i++)
            coa.Emblems.Add(new CoaEmblem { Texture = "ce_lion_rampant", Colors = new[] { C("yellow"), null, null },
                Instances = { new CoaInstance { X = 0.2 + i % 5 * 0.15, Y = 0.2 + i / 5 * 0.08, ScaleX = 0.1, ScaleY = 0.1 } } });
        var b = builder.Build(coa);
        int divisions = b.Layers.Count(l => l.Mesh == 510);
        Expect.True(b.Layers.Count <= BannerBuilder.MaxLayers, $"{b.Layers.Count} layers");
        Expect.True(divisions > 10, $"the table raised the division budget above its default 10 ({divisions} division layers)");
        Expect.Equal(b.Layers.Count * 10, b.Code.Split('.').Length);
        Console.WriteLine($"      {b.Layers.Count} layers ({divisions} divisions)");
    }

    /// <summary>
    /// An enemy CK3 banner is made distinct from the player's culture banner too: the module's own swap would repaint every
    /// icon layer of a CK3 banner in one colour.
    /// </summary>
    private static void ClashAgainstCultureBanner()
    {
        var e = Engine.Value;
        var vl = Expect.NotNull(e.Banners.CultureBannerOf("vlandia"), "vlandia banner colours from bl_catalog.json");
        Expect.Equal(new Rgb(0x8D, 0x29, 0x1A), vl.Primary);
        Expect.Equal(vl, e.Banners.CultureBannerOf("no_such_culture"), "the module flies vlandia's banner for an unknown culture");

        CoaColor? C(string n) => e.Colors.ResolveName(n);
        var redCoa = new CoaModel { Pattern = "pattern_solid", Colors = new[] { C("red"), C("yellow"), null },
            Emblems = { new CoaEmblem { Texture = "ce_barry_06", Colors = new[] { C("black"), null, null }, Instances = { new CoaInstance() } },
                        new CoaEmblem { Texture = "ce_lion_rampant", Colors = new[] { C("yellow"), null, null }, Instances = { new CoaInstance() } } } };
        var enemy = e.Banners.Build(redCoa);
        int colours = enemy.Layers.Select(l => l.Color).Distinct().Count();
        Expect.True(Rgb.Distance(vl.Primary, e.Banners.RgbOf(enemy.PrimaryColor)) < BannerBuilder.ClashDistance, "premise: CK3 red clashes with Vlandia's red");
        Expect.NotNull(e.Banners.ResolveClash(vl.Primary, vl.Icon, enemy));
        Expect.True(Rgb.Distance(vl.Primary, e.Banners.RgbOf(enemy.PrimaryColor)) >= BannerBuilder.ClashDistance, "enemy primary now distinct");
        Expect.Equal(colours, enemy.Layers.Select(l => l.Color).Distinct().Count(), "per-layer swap: every colour of the arms is kept");

        // Through Enrich: a landless player (culture banner) against a ruler whose predefined arms are red.
        string? key = new[] { "k_england", "k_aragon", "k_castille", "k_denmark", "k_poland", "k_georgia", "e_hre", "k_hungary" }
            .FirstOrDefault(k => e.GameCoas.Get(k, "game:" + k) is CoaModel g &&
                                 Rgb.Distance(vl.Primary, e.Banners.RgbOf(e.Banners.Build(g).PrimaryColor)) < BannerBuilder.ClashDistance);
        if (key == null) { Console.WriteLine("      no red predefined CoA among the candidates: Enrich part skipped"); return; }
        string cacheDir = Path.Combine(OutDir, "culture_clash");
        if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        using var provider = new AppearanceProvider();
        provider.Start("", GameDir.Value!, cacheDir);
        var battle = new BattleSnapshot { Bid = 15, SessionKey = "self", Year = 1066, Month = 1, Day = 1, PlayerCk3Id = 5 };
        battle.Defender.PlayerIn = true;
        battle.Defender.Characters.Add(Character(5, true, 30, false, 0, "byzantine"));   // landless, unknown house: no CoA
        battle.Attacker.Primary = new CharacterRef { Ck3Id = 6, TitleKey = key };
        battle.Attacker.Characters.Add(Character(6, false, 40, false, 4, "english", title: key));
        var request = new BattleRequest { Id = "b15_self", PlayerSide = "Defender",   // the module reads it case-insensitively
            Attacker = new SideRequest { Culture = "vlandia", Heroes = { Hero("e0", 6, "primary", "Foe") } },
            Defender = new SideRequest { Culture = "vlandia", Heroes = { Hero("h0", 5, "player", "Me") } } };
        provider.Enrich(battle, request);
        Expect.Null(request.Defender.Banner, "the player flies the culture banner");
        var code = Expect.NotNull(request.Attacker.Banner, "enemy CK3 banner").Code.Split('.');
        Expect.True(Rgb.Distance(vl.Primary, e.Banners.RgbOf(int.Parse(code[1], System.Globalization.CultureInfo.InvariantCulture))) >= BannerBuilder.ClashDistance,
            $"game:{key} made distinct from the vlandia banner");
        Console.WriteLine($"      game:{key} vs vlandia culture banner -> enemy primary {code[1]}");
    }

    /// <summary>Decoded textures stay within the budget over a long session; the shield mask is kept.</summary>
    private static void TextureMemoryBounded()
    {
        var e = Engine.Value;
        const long budget = 1 << 20;
        var tex = new CoaTextures(e.Files, budget);
        var names = Directory.EnumerateFiles(Path.Combine(e.Files!.GameDir, "gfx", "coat_of_arms", "colored_emblems"), "ce_*.dds")
            .Select(f => Path.GetFileNameWithoutExtension(f)!).Take(40).ToList();
        string? firstName = null, lastName = null;
        RgbaImage? first = null, last = null;
        long decoded = 0;
        foreach (var n in names)
        {
            if (tex.Emblem(n, false, 256) is not RgbaImage img) continue;
            decoded += img.Pixels.Length;
            if (first == null) { first = img; firstName = n; }
            last = img; lastName = n;
            Expect.True(tex.CachedBytes <= Math.Max(budget, img.Pixels.Length), $"{tex.CachedBytes} bytes cached after {n}");
        }
        Expect.True(decoded > 2 * budget, $"premise: {decoded} bytes decoded in total");
        Expect.True(ReferenceEquals(last, tex.Emblem(lastName!, false, 256)), "the most recent level is served from memory");
        var again = Expect.NotNull(tex.Emblem(firstName!, false, 256), "an evicted level is decoded again");
        Expect.False(ReferenceEquals(first, again), "the oldest level was evicted");
        Expect.Equal(first!.Pixels.Length, again.Pixels.Length);
        Expect.True(tex.TitleMask() is RgbaImage mask && ReferenceEquals(mask, tex.TitleMask()), "the shield mask is kept");
        Console.WriteLine($"      {decoded / 1024} KB decoded, {tex.CachedBytes / 1024} KB cached (budget {budget / 1024} KB)");
    }

    private static void CacheFormatAndPlaythrough()
    {
        string dir = Path.Combine(OutDir, "cache_format");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "appearance_cache.json");
        // Version 1 could hold a wrong "female": false learned from a binary save: discarded.
        File.WriteAllText(path, "{\"version\":1,\"playerId\":5,\"trackedIds\":[5],\"characters\":{\"5\":{\"id\":5,\"female\":false}}}");
        var old = new AppearanceCache(path);
        string? note = null;
        old.Load(m => note = m);
        Expect.Null(old.Character(5), "version-1 cache discarded");
        Expect.NotNull(note, "and logged");

        // Playthrough ids unknown: continuity is judged by the player and heir.
        var c = new AppearanceCache(path);
        Expect.False(c.SetSave("a.ck3", null, "870.1.1", "1.20", 10, 11), "first save");
        c.Put(new CharacterFacts { Id = 10, Female = false });
        Expect.False(c.SetSave("b.ck3", null, "900.1.1", "1.20", 11, 12), "the heir succeeded: same playthrough");
        Expect.NotNull(c.Character(10));
        Expect.True(c.SetSave("c.ck3", null, "867.1.1", "1.20", 500, 501), "a player who was neither: another playthrough");
        Expect.Null(c.Character(10));
        Expect.False(c.SetSave("d.ck3", "pt-1", "868.1.1", "1.20", 500, 501), "same player, playthrough id now known");
        Expect.True(c.SetSave("e.ck3", "pt-2", "869.1.1", "1.20", 500, 501), "both ids known and different");
        Expect.True(c.Describes("e.ck3", "pt-2", "869.1.1"));
        Expect.False(c.Describes("e.ck3", "pt-2", "870.1.1"), "same file, newer save");
        Expect.False(c.Describes("e.ck3", "pt-3", "869.1.1"), "other playthrough");
        Expect.Equal((500L, 501L), c.SavePlayers);
    }

    /// <summary>A failed read is retried; a successful one is not repeated; a save that never reads is given up after 3 tries.</summary>
    private static async Task WatcherRetriesFailedRead()
    {
        string docs = Path.Combine(OutDir, "watcher_docs");
        if (Directory.Exists(docs)) Directory.Delete(docs, recursive: true);
        Directory.CreateDirectory(Path.Combine(docs, "save games"));
        string save = Path.Combine(docs, "save games", "test.ck3");
        // Minimal text save: "SAV" + version + type 00 + 8 hex + meta length (8 hex) + newline, then the meta bytes.
        File.WriteAllText(save, "SAV" + "01" + "00" + "00000000" + "00000004" + "\n" + "meta");

        int calls = 0;
        using (var w = new SaveWatcher(docs, _ => Interlocked.Increment(ref calls) > 1, _ => { }))
        {
            w.Start();
            await Until(() => Volatile.Read(ref calls) >= 2, TimeSpan.FromSeconds(10));
            Expect.Equal(2, Volatile.Read(ref calls), "one failed read, then a retry that succeeded");
            w.Kick();
            await Task.Delay(1500);
            Expect.Equal(2, Volatile.Read(ref calls), "a save read successfully is not read again");
        }

        int attempts = 0;
        using (var w = new SaveWatcher(docs, _ => { Interlocked.Increment(ref attempts); return false; }, _ => { }))
        {
            w.Start();
            await Until(() => Volatile.Read(ref attempts) >= 3, TimeSpan.FromSeconds(10));
            await Task.Delay(1500);
            w.Kick();
            await Task.Delay(1000);
            Expect.Equal(3, Volatile.Read(ref attempts), "given up after 3 attempts on the same file version");
            File.AppendAllText(save, "\nchanged");
            w.Kick();
            await Until(() => Volatile.Read(ref attempts) >= 4, TimeSpan.FromSeconds(5));
            Expect.Equal(4, Volatile.Read(ref attempts), "a new version of the save is tried again");
        }
    }

    private static void BeardlessEunuch()
    {
        var e = Engine.Value;
        LookInput Input(long id, params string[] traits) => new()
        {
            Ck3Id = id, Female = false, ApparentAge = 40, Traits = traits, IsPortrait = false, Source = "ethnicity_seeded",
            Genes = e.Ethnicities!.Generate("caucasian_northern_blond", (uint)id, Array.Empty<string>()),
        };
        // A character whose derived look has a beard (CK3 base odds 70 %).
        long id = Enumerable.Range(1, 50).Select(i => (long)i).First(i => e.Looks.Build(Input(i), out _).Beard.Index is > 0);
        var bearded = e.Looks.Build(Input(id), out _);
        Expect.Equal(0, e.Looks.Build(Input(id, "beardless_eunuch"), out _).Beard.Index, "beardless_eunuch: clean-shaven, as CK3's portrait modifier");
        Expect.Equal(bearded.Beard.Index, e.Looks.Build(Input(id, "eunuch_1"), out _).Beard.Index, "eunuch_1 (castrated as an adult) keeps the beard");
    }

    /// <summary>A provider (no watcher) that has read the newest save of the first header type present; null when none is.</summary>
    private static AppearanceProvider? ProviderWith(string cacheName, out SaveState save, params string[] types)
    {
        save = null!;
        var type = types.FirstOrDefault(t => SavesByType.Value.ContainsKey(t));
        if (type == null) return null;
        string cacheDir = Path.Combine(OutDir, cacheName);
        if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        var provider = new AppearanceProvider();
        provider.Start("", GameDir.Value!, cacheDir);
        save = Expect.NotNull(provider.LoadSaveNow(SavesByType.Value[type][0]));
        return provider;
    }

    /// <summary>A battle on the save's date (the live ages then equal the ages at the save).</summary>
    private static BattleSnapshot BattleAtSave(SaveState s, int bid, long playerId)
    {
        var d = s.Meta.Date ?? new CK3Date(870, 1, 1);
        return new BattleSnapshot { Bid = bid, SessionKey = "self", Year = d.Year, Month = d.Month, Day = d.Day, PlayerCk3Id = playerId };
    }

    private static async Task Until(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < timeout) await Task.Delay(50);
    }

    private static CharacterSnapshot Character(long id, bool player, int age, bool female, int tier, string? ethnicity, string? trait = null, string? title = null) => new()
    {
        Ref = new CharacterRef { Ck3Id = id, Name = $"Character {id}", TitleKey = title },
        Role = player ? "PLAYER" : "KNIGHT", IsPlayer = player, IsPrimary = player || title != null,
        Age = age, Female = female, Prowess = 8, Martial = 6, Weight = 10, Tier = tier, PrimaryTitleKey = title,
        Culture = new Core.Model.CultureInfo { Culture = "telugu", Heritage = "heritage_dravidian", UnitGfx = "indian_unit_gfx", ClothingGfx = "indian_clothing_gfx" },
        Ethnicity = ethnicity,
        Traits = trait == null ? new() : new() { trait },
    };

    private static HeroSpec Hero(string key, long id, string role, string name) =>
        new() { Key = key, Ck3Id = id, Role = role, Name = name, Captain = role != "knight", IsPlayer = role == "player" };
}
