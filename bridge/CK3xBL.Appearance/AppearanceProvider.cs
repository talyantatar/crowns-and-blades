// CK3xBL.Appearance entry point (IAppearanceProvider, docs/PROTOCOL.md §3.2 and §7): turns the CK3 characters and coats
// of arms of a battle into Bannerlord hero looks, kits and banners.
//   Start  loads game data in the background and watches CK3 saves (passive, achievement-neutral reading only).
//   Enrich fills HeroSpec.Appearance/Equipment/Skills/Name/Age and SideRequest.Banner + Presentation CoA PNGs.
//          It never throws: each hero and side falls back independently (stock appearance, culture banner).
using System.Diagnostics;
using CK3xBL.Appearance.Cache;
using CK3xBL.Appearance.Coa;
using CK3xBL.Appearance.Data;
using CK3xBL.Appearance.Genes;
using CK3xBL.Appearance.Hero;
using CK3xBL.Appearance.Save;
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Model;
using CK3xBL.Protocol;

namespace CK3xBL.Appearance;

public sealed class AppearanceProvider : IAppearanceProvider, IDisposable
{
    private readonly AppearanceLog _log = new();
    private readonly object _engineLock = new();
    private AppearanceEngine? _engine;
    private Task<AppearanceEngine>? _engineTask;
    private SaveWatcher? _watcher;
    private AppearanceCache _cache = new(Path.Combine(DefaultCacheDir(), "appearance_cache.json"));
    private volatile SaveState? _state;
    private string _cacheDir = DefaultCacheDir();
    private string? _docsDir, _gameDir;
    private string? _trustNote;   // last logged verdict on whether the save matches the live campaign

    /// <summary>bridge/data folder; discovered next to the executable or its ancestors when null.</summary>
    public string? DataDirectory { get; set; }
    /// <summary>Heroes carry their own arms (PROTOCOL §25); off = every hero shows the side's banner.</summary>
    public bool HeroArms { get; set; } = true;

    /// <summary>Optional extra log sink (the component always logs to &lt;cacheDir&gt;/appearance.log).</summary>
    public Action<string>? Log { get => _log.Sink; set => _log.Sink = value; }

    /// <summary>Edge length of the rendered coat-of-arms PNGs.</summary>
    public int CoaPngSize { get; set; } = 256;

    public static string DefaultCacheDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProtocolInfo.FolderName);

    public void Start(string ck3DocumentsDir, string ck3GameDir, string cacheDir)
    {
        try
        {
            Stop();
            _state = null;
            _trustNote = null;
            _docsDir = ck3DocumentsDir;
            _gameDir = ck3GameDir;
            _cacheDir = string.IsNullOrWhiteSpace(cacheDir) ? DefaultCacheDir() : cacheDir;
            Directory.CreateDirectory(_cacheDir);
            _log.SetFile(Path.Combine(_cacheDir, "appearance.log"));
            _cache = new AppearanceCache(Path.Combine(_cacheDir, "appearance_cache.json"));
            _cache.Load(_log.Write);
            _log.Write($"start: docs '{ck3DocumentsDir}', game '{ck3GameDir}', cache '{_cacheDir}'");
            lock (_engineLock)
            {
                _engine = null;
                _engineTask = Task.Run(() => AppearanceEngine.Load(_gameDir, _docsDir, DataDirectory, _log.Write));
            }
            if (Directory.Exists(ck3DocumentsDir))
            {
                _watcher = new SaveWatcher(ck3DocumentsDir, OnSaveReady, _log.Write);
                _watcher.Start();
            }
            else _log.Write("CK3 documents folder not found: appearance comes from the cache and live records only");
        }
        catch (Exception e)
        {
            _log.Write($"start failed: {e}");
        }
    }

    public void Stop()
    {
        try
        {
            _watcher?.Dispose();
            _watcher = null;
        }
        catch (Exception e)
        {
            _log.Write($"stop: {e.Message}");
        }
    }

    public void Dispose() => Stop();

    /// <summary>The loaded engine; waits for the background load (or loads now when Start was never called).</summary>
    private AppearanceEngine Engine()
    {
        lock (_engineLock)
        {
            if (_engine != null) return _engine;
            _engineTask ??= Task.Run(() => AppearanceEngine.Load(_gameDir, _docsDir, DataDirectory, _log.Write));
        }
        var engine = _engineTask.GetAwaiter().GetResult();
        lock (_engineLock) _engine = engine;
        return engine;
    }

    // ------------------------------------------------------------------ saves

    /// <summary>Reads a save the watcher found. False when it could not be read (the watcher then retries).</summary>
    private bool OnSaveReady(string path)
    {
        try
        {
            var engine = Engine();
            var state = engine.ReadSave(path, _log.Write);
            // The cache is rebuilt from the save before the save is published, so a battle never pairs it with a stale cache.
            RefreshCache(engine, state);
            _state = state;
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Saves are outside our control (half-written autosaves, other mods' formats): never let one take the bridge down.
            _log.Write($"save {Path.GetFileName(path)} not read: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>Re-reads one save synchronously (self-test, diagnostics).</summary>
    internal SaveState? LoadSaveNow(string path)
    {
        OnSaveReady(path);
        return _state;
    }

    internal SaveState? CurrentSave => _state;

    private void RefreshCache(AppearanceEngine engine, SaveState state)
    {
        var meta = state.Meta;
        if (_cache.SetSave(state.Path, state.Index?.PlaythroughId, meta.Date?.ToString(), meta.GameVersion, meta.Player?.Id ?? -1, meta.Heir?.Id ?? -1))
            _log.Write($"{state.FileName} belongs to another playthrough (id {state.Index?.PlaythroughId ?? "unknown"}, player {meta.Player?.Id}): appearance cache cleared");
        var ids = _cache.TrackedIds().ToHashSet();
        if (meta.Player != null) ids.Add(meta.Player.Id);
        if (meta.Heir != null) ids.Add(meta.Heir.Id);
        foreach (long id in ids)
            if (engine.FactsFromSave(state, id) is CharacterFacts f)
                _cache.Put(engine.Facts(state, _cache, id) ?? f);
        _cache.Save(_log.Write);
    }

    // ------------------------------------------------------------------ battle

    /// <summary>What a battle may use: the current save and the cache, or neither when they belong to another campaign.</summary>
    private sealed record Context(AppearanceEngine Engine, SaveState? State, AppearanceCache Cache, BattleSnapshot Battle, bool Trusted);

    public void Enrich(BattleSnapshot battle, BattleRequest request)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var ctx = MakeContext(Engine(), battle);
            var seen = new List<long>();
            foreach (var (snap, req) in new[] { (battle.Attacker, request.Attacker), (battle.Defender, request.Defender) })
            {
                if (req == null) continue;
                foreach (var hero in req.Heroes)
                {
                    try
                    {
                        EnrichHero(ctx, req, hero);
                        seen.Add(hero.Ck3Id);
                    }
                    catch (Exception e)
                    {
                        _log.Write($"hero {hero.Key} ({hero.Ck3Id}) kept the mapper's spec: {e.GetType().Name}: {e.Message}");
                    }
                }
                if (snap.Primary != null) seen.Add(snap.Primary.Ck3Id);
            }
            UniqueNames(request);
            EnrichBanners(ctx, request);
            if (HeroArms) EnrichHeroBanners(ctx, request);
            if (ctx.Trusted)
            {
                if (ctx.Cache.Track(seen) && ctx.State != null)
                    foreach (long id in seen.Distinct())
                        if (ctx.Engine.Facts(ctx.State, ctx.Cache, id) is CharacterFacts f) ctx.Cache.Put(f);
                ctx.Cache.Save(_log.Write);
            }
        }
        catch (Exception e)
        {
            _log.Write($"enrich failed, request left as built by the mapper: {e}");
        }
        _log.Write($"enrich {request.Id} done in {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Character ids are reused between campaigns, and the newest save may belong to another one (the current campaign has
    /// not been saved yet). The save, or without a save the cache, is used only on positive evidence that it describes the
    /// live player (<see cref="DescribesPlayer"/>); otherwise this battle uses neither (stand-in faces, coats of arms from
    /// the game files) and writes nothing to the cache.
    /// </summary>
    private Context MakeContext(AppearanceEngine engine, BattleSnapshot battle)
    {
        var state = _state;
        long playerId = battle.PlayerCk3Id;
        var live = FindCharacter(battle, playerId);
        string verdict;
        if (state != null)
        {
            bool savedPlayer = playerId > 0 && (state.Meta.Player?.Id == playerId || state.Meta.Heir?.Id == playerId);
            if (DescribesPlayer(engine.FactsFromSave(state, playerId), savedPlayer, live, battle, out verdict))
            {
                // RefreshCache rebuilt the cache from this save; if it has not (yet), the cache is not evidence either.
                bool cacheOk = _cache.Describes(state.Path, state.Index?.PlaythroughId, state.Meta.Date?.ToString());
                NoteTrust($"{state.FileName} used for player {playerId}: {verdict}{(cacheOk ? "" : "; cache not built from it, not used")}");
                return new Context(engine, state, cacheOk ? _cache : AppearanceCache.InMemory(), battle, Trusted: true);
            }
            verdict = $"{state.FileName}: {verdict}";
        }
        else
        {
            var (cachedPlayer, cachedHeir) = _cache.SavePlayers;
            bool savedPlayer = playerId > 0 && (cachedPlayer == playerId || cachedHeir == playerId);
            if (DescribesPlayer(_cache.Character(playerId), savedPlayer, live, battle, out verdict))
            {
                NoteTrust($"no save read yet; appearance cache used for player {playerId}: {verdict}");
                return new Context(engine, null, _cache, battle, Trusted: true);
            }
            verdict = $"no save read yet, appearance cache: {verdict}";
        }
        NoteTrust($"{verdict}: save and cache ignored for player {playerId} (stand-in faces, game-file coats of arms)");
        return new Context(engine, null, AppearanceCache.InMemory(), battle, Trusted: false);
    }

    /// <summary>
    /// Positive evidence that recorded facts (save or cache) describe the live player: the player is the recorded player or
    /// heir and, when the live record is there, agrees with them; or, for anyone else, a record whose sex and birth date
    /// were both checked against the live record. Binary saves hold neither for ordinary characters, so they need the
    /// player/heir match.
    /// </summary>
    private static bool DescribesPlayer(CharacterFacts? facts, bool savedPlayer, CharacterSnapshot? live, BattleSnapshot battle, out string why)
    {
        if (battle.PlayerCk3Id <= 0) { why = "no live player id"; return false; }
        if (live == null)
        {
            why = savedPlayer ? "the player is the recorded player/heir (no live record to compare)" : "the player is neither the recorded player nor heir";
            return savedPlayer;
        }
        if (facts == null) { why = "the player is not in it"; return false; }
        if (!Consistent(facts, live, battle)) { why = "the live player disagrees with it (sex/age): another campaign?"; return false; }
        if (savedPlayer) { why = "the recorded player/heir agrees with the live record"; return true; }
        bool compared = facts.Female != null && CK3Date.Parse(facts.Birth) != null && battle.Year > 0 && live.Age > 0;
        why = compared ? "the player's sex and birth date agree" : "nothing to compare the live player with";
        return compared;
    }

    /// <summary>Logs the trust verdict when it changes (not once per battle).</summary>
    private void NoteTrust(string note)
    {
        if (note == _trustNote) return;
        _trustNote = note;
        _log.Write(note);
    }

    /// <summary>Do save facts describe the same person as the live record (sex, age by birth date, portrait age)?</summary>
    private static bool Consistent(CharacterFacts facts, CharacterSnapshot live, BattleSnapshot battle)
    {
        if (facts.Female is bool f && f != live.Female) return false;
        if (battle.Year <= 0 || live.Age <= 0) return true;
        double battleYears = new CK3Date(battle.Year, Math.Max(1, battle.Month), Math.Max(1, battle.Day)).AsYears;
        if (CK3Date.Parse(facts.Birth) is CK3Date birth && Math.Abs(battleYears - birth.AsYears - live.Age) > 1.5) return false;
        if (facts.GeneSource == "meta_portrait" && facts.PortraitAge > 0 && CK3Date.Parse(facts.PortraitDate) is CK3Date meta)
        {
            double realAtSave = live.Age - (battleYears - meta.AsYears);
            double apparent = facts.PortraitAge * 100;
            // Portraits look younger with graceful aging, never older.
            if (realAtSave < -1 || apparent > realAtSave + 3 || apparent < realAtSave - 40) return false;
        }
        return true;
    }

    private void EnrichHero(Context ctx, SideRequest req, HeroSpec hero)
    {
        if (hero.Ck3Id <= 0) return;   // not a CK3 character (stock Bannerlord general): nothing to look up
        var engine = ctx.Engine;
        var battle = ctx.Battle;
        var live = FindCharacter(battle, hero.Ck3Id);
        var facts = engine.Facts(ctx.State, ctx.Cache, hero.Ck3Id);
        if (facts != null && live != null && !Consistent(facts, live, battle))
        {
            _log.Write($"hero {hero.Key} {hero.Ck3Id}: save facts do not match the live record (sex/age); using a stand-in");
            facts = null;
        }
        bool female = live?.Female ?? facts?.Female ?? hero.Female;
        hero.Female = female;
        int liveAge = live is { Age: > 0 } ? live.Age : hero.Age;
        var traits = (IReadOnlyCollection<string>?)live?.Traits ?? Array.Empty<string>();

        // ---- genes: meta portrait > stored dna > parent-inherited stand-in > ethnicity stand-in
        GeneSet? genes = null;
        string source = "stock";
        bool portrait = false;
        if (facts?.GeneSource != null && facts.Genes != null)
        {
            genes = GeneSet.Deserialize(facts.Genes);
            source = facts.GeneSource;
            portrait = source == "meta_portrait";
        }
        string? ethnicity = live?.Ethnicity is { Length: > 0 } le && le != "unknown" ? le : facts?.Ethnicity ?? facts?.CultureEthnicities.FirstOrDefault();
        if ((genes == null || genes.Count == 0) && engine.Ethnicities != null)
        {
            uint seed = unchecked((uint)hero.Ck3Id);
            var standIn = engine.Ethnicities.Generate(ethnicity, seed, traits);
            var parents = facts?.ParentGenes.Select(GeneSet.Deserialize).Where(g => g.Count > 0).ToList() ?? new();
            if (parents.Count > 0)
            {
                genes = EthnicityDb.Inherit(standIn, parents[0], parents.Count > 1 ? parents[1] : null, seed);
                source = "parent_inherited";
            }
            else
            {
                genes = standIn;
                source = "ethnicity_seeded";
            }
        }

        // ---- age: the live age, minus the graceful-aging offset the meta portrait showed at save time
        int age = liveAge;
        if (portrait && facts!.PortraitAge > 0 && CK3Date.Parse(facts.PortraitDate) is CK3Date metaDate && battle.Year > 0)
        {
            double battleYears = new CK3Date(battle.Year, Math.Max(1, battle.Month), Math.Max(1, battle.Day)).AsYears;
            double realAtMeta = liveAge - (battleYears - metaDate.AsYears);
            double offset = Math.Max(0, realAtMeta - facts.PortraitAge * 100);
            age = (int)Math.Round(liveAge - offset);
        }
        age = Math.Clamp(age, 16, 100);

        if (genes != null && genes.Count > 0)
        {
            var look = new LookInput
            {
                Ck3Id = hero.Ck3Id, Female = female, ApparentAge = age,
                Weight = live?.Weight ?? 0, Prowess = live?.Prowess ?? 5, Traits = traits,
                ClothingGfx = live?.Culture.ClothingGfx ?? facts?.ClothingGfx,
                Genes = genes, IsPortrait = portrait, Source = source,
            };
            hero.Appearance = engine.Looks.Build(look, out string notes);
            hero.Age = age;
            _log.Write($"hero {hero.Key} {hero.Ck3Id} '{hero.Name}': {source} ({ethnicity ?? "no ethnicity"}), age {age}: {notes}");
        }
        else
        {
            hero.Appearance = new AppearanceSpec
            {
                Source = "stock",
                Weight = (float)Math.Clamp(0.5 + (live?.Weight ?? 0) / 200.0, 0, 1),
                Build = (float)Math.Clamp(0.35 + (live?.Prowess ?? 5) / 40.0, 0, 1),
            };
            _log.Write($"hero {hero.Key} {hero.Ck3Id}: stock look (no genes: game data {(engine.Genes == null ? "missing" : "ok")})");
        }

        if (string.IsNullOrWhiteSpace(hero.Name))
            hero.Name = live?.Ref.Name is { Length: > 0 } n ? n : facts?.FirstName ?? hero.Key;

        string blCulture = engine.BlCulture(live?.Culture, req.Culture);
        int tier = live?.Tier ?? 0;
        var eq = engine.Equipment.Plan(hero.Equipment, blCulture, tier, female, hero.Captain, hero.Ck3Id,
                                       live?.Culture.Heritage ?? facts?.Heritage, out string kitNotes);
        if (eq != null)
        {
            hero.Equipment = eq;
            _log.Write($"hero {hero.Key}: {kitNotes}");
        }
        if (hero.Skills == null || hero.Skills.Count == 0)
            hero.Skills = EquipmentPlanner.Skills(live?.Prowess ?? 5, live?.Martial ?? 5, tier, blCulture);
    }

    private static CharacterSnapshot? FindCharacter(BattleSnapshot battle, long id)
    {
        if (id <= 0) return null;
        foreach (var side in battle.Sides)
            foreach (var c in side.Characters)
                if (c.Ref.Ck3Id == id) return c;
        return null;
    }

    /// <summary>PROTOCOL §3.2: names are unique across both sides and contain no '{', '}' or '.'.</summary>
    private static void UniqueNames(BattleRequest request)
    {
        var heroes = new[] { request.Attacker, request.Defender }.Where(s => s != null).SelectMany(s => s.Heroes).ToList();
        foreach (var h in heroes) h.Name = Sanitize(h.Name);
        foreach (var group in heroes.GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList())
        {
            int n = 1;
            foreach (var h in group.Skip(1)) h.Name = $"{h.Name} ({++n})";
        }
    }

    private static string Sanitize(string? name)
    {
        var s = (name ?? "").Replace("{", "").Replace("}", "").Replace(".", "").Trim();
        return s.Length == 0 ? "Unknown" : s;
    }

    // ------------------------------------------------------------------ banners

    private void EnrichBanners(Context ctx, BattleRequest request)
    {
        var engine = ctx.Engine;
        var results = new Dictionary<string, (BannerResult Banner, CoaModel Coa)>();
        foreach (var (snap, req, key) in new[] { (ctx.Battle.Attacker, request.Attacker, "attacker"), (ctx.Battle.Defender, request.Defender, "defender") })
        {
            if (req == null) continue;
            try
            {
                var coa = SideCoa(ctx, snap);
                if (coa == null)
                {
                    _log.Write($"{key}: no coat of arms found, culture banner kept");
                    continue;
                }
                var banner = engine.Banners.Build(coa);
                results[key] = (banner, coa);
                _log.Write($"{key}: banner from {coa.Source}: {banner.Notes}");
            }
            catch (Exception e)
            {
                _log.Write($"{key}: banner failed, culture banner kept: {e.GetType().Name}: {e.Message}");
            }
        }

        // A side whose leader has no arms of his own (no title, no house CoA found) but belongs to the other side's house fights
        // under that house's arms, as a CK3 family does (live 2026-10-18: a duel with one's unlanded son flew a culture banner).
        foreach (var (snap, key, other) in new[] { (ctx.Battle.Attacker, "attacker", "defender"), (ctx.Battle.Defender, "defender", "attacker") })
        {
            if (results.ContainsKey(key) || !results.TryGetValue(other, out var kin)) continue;
            string? house = SidePrimary(snap)?.HouseName;
            string? otherHouse = SidePrimary(other == "attacker" ? ctx.Battle.Attacker : ctx.Battle.Defender)?.HouseName;
            if (string.IsNullOrEmpty(house) || house != otherHouse) continue;
            results[key] = kin;
            _log.Write($"{key}: no arms of their own; the arms of their house ({house}) from {kin.Coa.Source}");
        }

        // Same rule as the module (RequestRules.IsPlayerAttacker): anything but "defender" puts the player on the attack.
        bool playerDefends = string.Equals(request.PlayerSide?.Trim(), "defender", StringComparison.OrdinalIgnoreCase);
        string mine = playerDefends ? "defender" : "attacker";
        string enemy = playerDefends ? "attacker" : "defender";
        try
        {
            ResolveClash(engine, request, results, mine, enemy);
        }
        catch (Exception e)
        {
            _log.Write($"side colour check failed, banners kept as built: {e.GetType().Name}: {e.Message}");
        }

        // Codes first, then the images: a failing PNG must not cost either side its banner.
        foreach (var (key, (banner, coa)) in results)
            (key == "attacker" ? request.Attacker : request.Defender).Banner = new BannerSpec { Code = banner.Code, Source = coa.Source };
        if (!engine.Textures.Available) return;
        foreach (var (key, (_, coa)) in results)
        {
            try
            {
                string png = engine.Renderer.RenderToFile(coa, Path.Combine(_cacheDir, "coa"), CoaPngSize);
                request.Presentation ??= new Presentation();
                if (key == "attacker") request.Presentation.AttackerCoaPng = png;
                else request.Presentation.DefenderCoaPng = png;
            }
            catch (Exception e)
            {
                _log.Write($"{key}: CoA PNG not rendered: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Makes the enemy's CK3 banner distinct from the player side's team colours: its CK3 banner, or else the culture banner
    /// the module will fly (the module's own swap would repaint every icon layer of a CK3 banner in one colour, so it must
    /// find nothing left to fix, PROTOCOL §7.2).
    /// </summary>
    private void ResolveClash(AppearanceEngine engine, BattleRequest request, Dictionary<string, (BannerResult Banner, CoaModel Coa)> results,
                              string mine, string enemy)
    {
        if (!results.TryGetValue(enemy, out var en)) return;   // a culture banner: the module's colour swap handles it
        string? clash;
        if (results.TryGetValue(mine, out var m)) clash = engine.Banners.ResolveClash(m.Banner, en.Banner);
        else
        {
            string? culture = (mine == "attacker" ? request.Attacker : request.Defender)?.Culture;
            if (engine.Banners.CultureBannerOf(culture) is not CultureBanner cb)
            {
                _log.Write($"{mine}: culture banner colours of '{culture}' unknown; the enemy banner's colours were not checked against it");
                return;
            }
            clash = engine.Banners.ResolveClash(cb.Primary, cb.Icon, en.Banner) is string c ? $"{c} (against the {culture} culture banner)" : null;
        }
        if (clash != null) _log.Write($"side colours clashed: {clash}");
    }

    /// <summary>The side primary participant's primary-title CoA (the player's from the save meta), else their house's.</summary>
    private CoaModel? SideCoa(Context ctx, SideSnapshot snap)
    {
        var primary = SidePrimary(snap);
        long primaryId = snap.Primary?.Ck3Id ?? 0;
        if (primaryId <= 0 && primary != null) primaryId = primary.Ref.Ck3Id;
        return CharacterCoa(ctx, primaryId, primary, primary?.PrimaryTitleKey ?? snap.Primary?.TitleKey);
    }

    /// <summary>The side primary participant's character record, else the first character marked primary.</summary>
    private static CharacterSnapshot? SidePrimary(SideSnapshot snap)
    {
        var primaryId = snap.Primary?.Ck3Id ?? 0;
        return snap.Characters.FirstOrDefault(c => primaryId > 0 && c.Ref.Ck3Id == primaryId)
               ?? snap.Characters.FirstOrDefault(c => c.IsPrimary);
    }

    // ------------------------------------------------------------------ hero heraldry (PROTOCOL §25)

    /// <summary>
    /// Every hero whose own arms differ from their side's banner carries them (HeroSpec.banner): the arms of their primary title,
    /// else of their house. The module shows them on the hero's heraldic shield and clothing; the side keeps its colours.
    /// </summary>
    private void EnrichHeroBanners(Context ctx, BattleRequest request)
    {
        foreach (var side in new[] { request.Attacker, request.Defender })
        {
            if (side?.Heroes == null) continue;
            foreach (var hero in side.Heroes)
            {
                if (hero == null || hero.Ck3Id <= 0) continue;
                try
                {
                    var live = FindCharacter(ctx.Battle, hero.Ck3Id);
                    var coa = CharacterCoa(ctx, hero.Ck3Id, live, live?.PrimaryTitleKey);
                    if (coa == null || coa.Source == side.Banner?.Source) continue;
                    var banner = ctx.Engine.Banners.Build(coa);
                    hero.Banner = new BannerSpec { Code = banner.Code, Source = coa.Source };
                    _log.Write($"hero {hero.Key}: arms from {coa.Source}");
                }
                catch (Exception e)
                {
                    _log.Write($"hero {hero.Key}: arms failed, side banner kept: {e.GetType().Name}: {e.Message}");
                }
            }
        }
    }

    /// <summary>A character's primary-title CoA (the facts' when it is the same title, else the save's or the game's), else their house's.</summary>
    private CoaModel? CharacterCoa(Context ctx, long characterId, CharacterSnapshot? live, string? titleKey)
    {
        var engine = ctx.Engine;
        long primaryId = characterId;
        var primary = live;
        var facts = primaryId > 0 ? engine.Facts(ctx.State, ctx.Cache, primaryId) : null;
        if (facts != null && primary != null && !Consistent(facts, primary, ctx.Battle)) facts = null;

        // The facts' title CoA (the player's: the meta CoA) is the arms of the primary title at save time: right when the
        // live record names no title or the same one.
        if (facts?.TitleCoa != null && (titleKey == null || facts.PrimaryTitleKey == titleKey))
            return facts.TitleCoa;
        // The live title in the save's own CoA database. Binary saves do not tell which title the meta CoA belongs to, and
        // the player may have gained a new primary title since the save.
        if (AppearanceEngine.SavedTitleCoa(ctx.State, titleKey) is CoaModel saved) return Remember(ctx, titleKey!, saved);
        if (facts?.TitleCoa != null && facts.PrimaryTitleKey == null) return facts.TitleCoa;
        if (engine.TitleCoa(ctx.State, ctx.Cache, titleKey) is CoaModel byTitle) return Remember(ctx, titleKey!, byTitle);
        if (facts?.HouseCoa != null) return facts.HouseCoa;
        return facts?.HouseId >= 0 ? ctx.Cache.HouseCoa(facts.HouseId) : null;
    }

    /// <summary>Keeps a title CoA read from the current save in the cache, so later binary saves can still use it.</summary>
    private static CoaModel Remember(Context ctx, string titleKey, CoaModel coa)
    {
        if (ctx.Trusted && coa.Source.StartsWith("title:", StringComparison.Ordinal)) ctx.Cache.PutTitleCoa(titleKey, coa);
        return coa;
    }
}
