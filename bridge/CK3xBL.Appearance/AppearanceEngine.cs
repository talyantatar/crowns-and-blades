// Everything the provider needs, loaded once from the CK3 install, the active playset and bridge/data, plus the
// save reading pipeline (container -> meta -> gamestate index) and the per-character fact join.
using System.Diagnostics;
using CK3xBL.Appearance.Cache;
using CK3xBL.Appearance.Coa;
using CK3xBL.Appearance.Data;
using CK3xBL.Appearance.Game;
using CK3xBL.Appearance.Genes;
using CK3xBL.Appearance.Hero;
using CK3xBL.Appearance.Save;

namespace CK3xBL.Appearance;

/// <summary>One save as read: meta always, the gamestate index for text saves.</summary>
internal sealed class SaveState
{
    public required string Path;
    public required SaveHeader Header;
    public required SaveMeta Meta;
    public SaveIndex? Index;
    public bool Binary;
    public CoaModel? TitleCoa;
    public CoaModel? HouseCoa;
    public TimeSpan ReadTime;
    public string FileName => System.IO.Path.GetFileName(Path);
}

internal sealed class AppearanceEngine
{
    public GameFiles? Files { get; private init; }
    public GeneDb? Genes { get; private init; }
    public PortraitPalettes? Palettes { get; private init; }
    public EthnicityDb? Ethnicities { get; private init; }
    public required NamedColors Colors { get; init; }
    public required GameCoaLibrary GameCoas { get; init; }
    public required CoaTextures Textures { get; init; }
    public required CoaRenderer Renderer { get; init; }
    public required BannerBuilder Banners { get; init; }
    public required AppearanceBuilder Looks { get; init; }
    public required EquipmentPlanner Equipment { get; init; }
    public required BridgeData.CultureTables Cultures { get; init; }

    public static AppearanceEngine Load(string? ck3GameDir, string? ck3DocumentsDir, string? dataDir, Action<string> log)
    {
        var sw = Stopwatch.StartNew();
        var files = GameFiles.Locate(ck3GameDir, ck3DocumentsDir, log);
        var data = BridgeData.Open(dataDir, log);
        GeneDb? genes = null;
        PortraitPalettes? palettes = null;
        EthnicityDb? ethnicities = null;
        if (files != null)
        {
            genes = Try(() => GeneDb.Load(files), "gene database", log);
            palettes = Try(() => PortraitPalettes.Load(files, log), "portrait palettes", log);
            ethnicities = Try(() => EthnicityDb.Load(files), "ethnicities", log);
        }
        var colors = Try(() => NamedColors.Load(files), "named colours", log) ?? NamedColors.Load(null);
        var gameCoas = Try(() => GameCoaLibrary.Load(files, colors), "game coats of arms", log) ?? GameCoaLibrary.Load(null, colors);
        var textures = new CoaTextures(files);
        // bridge/data tables: a missing or malformed table degrades that feature only.
        var skins = Try(() => data?.LoadSkins(), "bl_skins_human.json", log) ?? new[] { new BlSkin(), new BlSkin() };
        var kits = Try(() => data?.LoadKits(), "bl_ruler_kits.json", log);
        var (rosters, shields, banners) = kits is { } k ? k : (new(), new(), new());
        var (armors, cultureBanners) = Try(() => data?.LoadCatalog(), "bl_catalog.json", log) is { } cat ? cat : (new(), new());
        var engine = new AppearanceEngine
        {
            Files = files,
            Genes = genes,
            Palettes = palettes,
            Ethnicities = ethnicities,
            Colors = colors,
            GameCoas = gameCoas,
            Textures = textures,
            Renderer = new CoaRenderer(textures),
            Banners = new BannerBuilder(Try(() => data?.LoadBannerPalette(), "bl_banner_colors.json", log) ?? new(),
                                        Try(() => data?.LoadNamedColorMap(), "ck3_named_colors_to_bl.json", log) ?? new(),
                                        cultureBanners,
                                        Try(() => data?.EmblemIconTable(), "ck3_emblem_to_bl_icon.json", log), textures),
            Looks = new AppearanceBuilder(genes, palettes, skins, Try(() => data?.LoadMorphMap(), "ck3_gene_to_bl_face.json", log) ?? new(),
                                          HairStyleTable.Load(Try(() => data?.HairTable(), "ck3_hair_to_bl.json", log))),
            Equipment = new EquipmentPlanner(rosters, shields, banners, armors),
            Cultures = Try(() => data?.LoadCultureTables(), "mapping.json", log) ?? new BridgeData.CultureTables(),
        };
        log($"engine ready in {sw.ElapsedMilliseconds} ms: game {(files?.GameDir ?? "MISSING")}" +
            $"{(files is { ModNames.Count: > 0 } ? " + mods " + string.Join(", ", files.ModNames) : "")}, data {(data?.Directory ?? "MISSING")}, " +
            $"{genes?.Inheritable.Count ?? 0} inheritable genes, {ethnicities?.Count ?? 0} ethnicities, {gameCoas.Count} game CoAs, " +
            $"{rosters.Count} kit rosters, palettes {(palettes?.Complete == true ? "ok" : "missing")}");
        return engine;
    }

    /// <summary>Runs a loader over files outside our control; any failure is logged and the feature degrades.</summary>
    private static T? Try<T>(Func<T?> f, string what, Action<string> log)
    {
        try { return f(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log($"{what} unavailable: {e.GetType().Name}: {e.Message}");
            return default;
        }
    }

    // ------------------------------------------------------------------ saves

    /// <summary>Reads meta and (for text saves) the gamestate index. Throws on unreadable files.</summary>
    public SaveState ReadSave(string path, Action<string> log)
    {
        var sw = Stopwatch.StartNew();
        using var fs = SaveContainer.OpenShared(path);
        var header = SaveContainer.TryReadHeader(fs) ?? throw new InvalidDataException("not a CK3 save (bad header)");
        var metaBytes = SaveContainer.ReadMeta(fs, header);
        var tokens = new CoaTokenMap(); // per save: calibrated on this save's own coats of arms
        var meta = MetaReader.Read(metaBytes, header.BinaryMeta, tokens, Colors);
        if (meta.PortraitsVersion is int pv && pv != GeneDb.ExpectedPortraitsVersion)
            log($"save portraits_version {pv} differs from the expected {GeneDb.ExpectedPortraitsVersion}; stored dna may decode wrongly");
        var gamestate = SaveContainer.ReadGamestate(fs, header, out bool binary);
        SaveIndex index;
        if (binary)
        {
            index = BinaryGamestateScanner.Scan(gamestate, Colors, tokens, log);
            meta.RenameCoas(tokens);
        }
        else index = TextGamestateScanner.Scan(gamestate, Colors);
        var state = new SaveState
        {
            Path = path,
            Header = header,
            Meta = meta,
            Index = index,
            Binary = binary,
            TitleCoa = CoaParser.Parse(meta.TitleCoaNode, "meta:primary_title", Colors, GameCoas.Node),
            HouseCoa = CoaParser.Parse(meta.HouseCoaNode, "meta:house", Colors, GameCoas.Node),
        };
        state.ReadTime = sw.Elapsed;
        log($"read {state.FileName} ({header}) in {sw.ElapsedMilliseconds} ms: {meta.GameVersion} {meta.Date}, player {meta.Player?.Id} " +
            $"'{meta.PlayerName}' of house {meta.HouseName} ({meta.Player?.Genes.Count ?? 0} genes, age {meta.Player?.Age:0.00}), heir {meta.Heir?.Id}, " +
            (binary ? "binary gamestate: " : "") + index.Summary());
        return state;
    }

    // ------------------------------------------------------------------ facts join

    /// <summary>Facts about a character from the current save, gaps filled from the cache.</summary>
    public CharacterFacts? Facts(SaveState? state, AppearanceCache cache, long id)
    {
        var fresh = state != null ? FactsFromSave(state, id) : null;
        var cached = cache.Character(id);
        if (fresh == null) return cached;
        if (cached == null) return fresh;
        if (fresh.GeneSource == null && cached.GeneSource != null)
        {
            fresh.GeneSource = cached.GeneSource; fresh.Genes = cached.Genes;
            fresh.PortraitAge = cached.PortraitAge; fresh.PortraitDate = cached.PortraitDate;
        }
        if (fresh.ParentGenes.Count == 0) fresh.ParentGenes = cached.ParentGenes;
        fresh.FirstName ??= cached.FirstName;
        fresh.Birth ??= cached.Birth;
        fresh.Female ??= cached.Female;
        fresh.Ethnicity ??= cached.Ethnicity;
        fresh.CultureKey ??= cached.CultureKey;
        fresh.Heritage ??= cached.Heritage;
        fresh.ClothingGfx ??= cached.ClothingGfx;
        if (fresh.CultureEthnicities.Count == 0) fresh.CultureEthnicities = cached.CultureEthnicities;
        if (fresh.HouseId < 0) fresh.HouseId = cached.HouseId;
        fresh.PrimaryTitleKey ??= cached.PrimaryTitleKey;
        fresh.TitleCoa ??= cached.TitleCoa;
        fresh.HouseCoa ??= cached.HouseCoa;
        return fresh;
    }

    public CharacterFacts? FactsFromSave(SaveState s, long id)
    {
        var f = new CharacterFacts { Id = id, SeenInSave = s.FileName };
        bool any = false;
        var portrait = s.Meta.Player?.Id == id ? s.Meta.Player : s.Meta.Heir?.Id == id ? s.Meta.Heir : null;
        if (portrait != null)
        {
            any = true;
            f.GeneSource = "meta_portrait";
            f.Genes = portrait.Genes.Serialize();
            f.PortraitAge = portrait.Age;
            f.PortraitDate = s.Meta.Date?.ToString();
            f.Female = portrait.Female;
        }
        if (s.Meta.Player?.Id == id)
        {
            f.TitleCoa = s.TitleCoa;
            f.HouseCoa = s.HouseCoa;
        }
        var ix = s.Index;
        if (ix?.Character(id) is CharacterRecord c)
        {
            any = true;
            f.FirstName = c.FirstName;
            f.Birth = c.Birth;
            f.Female ??= c.Female;
            f.Ethnicity = c.Ethnicity;
            if (ix.Cultures.TryGetValue(c.Culture, out var cul))
            {
                f.CultureKey = cul.Key; f.Heritage = cul.Heritage; f.ClothingGfx = cul.ClothingGfx;
                f.CultureEthnicities = cul.Ethnicities.ToList();
            }
            f.HouseId = c.House;
            if (ix.Titles.TryGetValue(c.PrimaryTitle, out var t)) f.PrimaryTitleKey = t.Key;
            if (f.GeneSource == null && c.Dna != null && Genes != null && GeneSet.FromDna(c.Dna, Genes, out _) is GeneSet dna)
            {
                f.GeneSource = "save_dna";
                f.Genes = dna.Serialize();
            }
            if (ix.Parents.TryGetValue(id, out var parents) && Genes != null)
                foreach (long p in parents)
                {
                    if (ParentGenes(s, ix, p) is GeneSet pg) f.ParentGenes.Add(pg.Serialize());
                }
            if (ix.Coas != null)
            {
                f.TitleCoa ??= ix.Coas.Get(ix.TitleCoaId(f.PrimaryTitleKey), "title:" + f.PrimaryTitleKey);
                if (f.HouseId >= 0) f.HouseCoa ??= ix.Coas.Get(ix.HouseCoaId(f.HouseId), "house:" + f.HouseId);
            }
        }
        return any ? f : null;
    }

    private GeneSet? ParentGenes(SaveState s, SaveIndex ix, long parent)
    {
        var portrait = s.Meta.Player?.Id == parent ? s.Meta.Player : s.Meta.Heir?.Id == parent ? s.Meta.Heir : null;
        if (portrait != null) return portrait.Genes;
        return ix.Character(parent)?.Dna is string dna && Genes != null ? GeneSet.FromDna(dna, Genes, out _) : null;
    }

    // ------------------------------------------------------------------ coats of arms

    /// <summary>A title's CoA: current save, cache, then the predefined CoA in the game files.</summary>
    public CoaModel? TitleCoa(SaveState? state, AppearanceCache cache, string? titleKey)
    {
        if (string.IsNullOrEmpty(titleKey)) return null;
        return SavedTitleCoa(state, titleKey) ?? cache.TitleCoa(titleKey) ?? GameCoas.Get(titleKey, "game:" + titleKey);
    }

    /// <summary>A title's CoA from the save's own CoA database (text or binary gamestate), or null.</summary>
    public static CoaModel? SavedTitleCoa(SaveState? state, string? titleKey)
    {
        var ix = state?.Index;
        if (string.IsNullOrEmpty(titleKey) || ix?.Coas == null) return null;
        return ix.TitleCoaId(titleKey) is long id and >= 0 ? ix.Coas.Get(id, "title:" + titleKey) : null;
    }

    /// <summary>Bannerlord culture for a CK3 culture (mapping.json culture -> heritage -> unit gfx), else the side's.</summary>
    public string BlCulture(Core.Model.CultureInfo? c, string sideCulture)
    {
        if (c?.Culture is string k && Cultures.ByCulture.TryGetValue(k, out var a)) return a;
        if (c?.Heritage is string h && Cultures.ByHeritage.TryGetValue(h, out var b)) return b;
        if (c?.UnitGfx is string u && Cultures.ByUnitGfx.TryGetValue(u, out var d)) return d;
        return string.IsNullOrEmpty(sideCulture) ? Cultures.Default : sideCulture;
    }
}
