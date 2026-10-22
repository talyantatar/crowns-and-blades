// CK3 characters -> Bannerlord heroes (HeroSpec skeletons, PROTOCOL §3.2). Appearance is left to CK3xBL.Appearance.
//   * Every army commander and knight on a side is a hero. A CK3 ruler can only be with an army as its
//     commander or as a knight, so that is what "present" means; C|PRIMARY and C|PLAYER alone put the player on the field only as
//     the stand-in below (PROTOCOL §5.2 emits C|PLAYER whenever the player is otherwise unlisted, e.g. when only the realm fights).
//   * Avatar: the player character when present in the battle; otherwise the commander of the player's (largest) army
//     ("If you aren't with the army, you play its commander"); when no army of his side has a commander,
//     the player's own character from his C record (C|PLAYER, or C|PRIMARY flagged as the player; PROTOCOL §15) stands in and
//     leads the side as its general. Not with the armies in CK3, he is never harmed by the outcome and never acts as the side's
//     CK3 commander for the knights pass.
//   * General: the player is general when commanding an army on the side, or when the side's primary participant and present;
//     otherwise a sergeant under the side commander (the commander of the primary participant's army, else the largest army's).
//   * The enemy general gets the highest Level on his side, because Bannerlord picks the AI general by GetPower().
using CK3xBL.Core.Model;
using CK3xBL.Mapping.Data;
using CK3xBL.Mapping.Resolution;
using CK3xBL.Protocol;

namespace CK3xBL.Mapping.Planning;

public sealed class PlannedHero
{
    public required CharacterSnapshot Character { get; init; }
    public required SideKey Side { get; init; }
    public required string Role { get; init; }            // player | commander | knight
    public required bool IsAvatar { get; init; }
    public HeroSpec Spec { get; set; } = new();
}

public sealed class HeroAssignment
{
    public required SideKey PlayerSide { get; init; }
    public required bool PlayerIsGeneral { get; init; }
    public required List<PlannedHero> Heroes { get; init; }
    public PlannedHero? Avatar { get; init; }
    public PlannedHero? AttackerGeneral { get; init; }
    public PlannedHero? DefenderGeneral { get; init; }
    public PlannedHero? General(SideKey side) => side == SideKey.Attacker ? AttackerGeneral : DefenderGeneral;

    /// <summary>The character of a side's general when he is with the armies in CK3 (commander or knight), for the knights pass: a
    /// stand-in player (PROTOCOL §15) leads in Bannerlord only, so the side keeps CK3's commander (or none).</summary>
    public CharacterSnapshot? Ck3Commander(SideKey side) => General(side)?.Character is { } c && HeroPlanner.IsPresent(c) ? c : null;
}

public sealed class HeroPlanner
{
    private static readonly Dictionary<string, string> KitPrefix = new(StringComparer.Ordinal)
    {
        ["vlandia"] = "vla", ["empire"] = "emp", ["sturgia"] = "stu", ["battania"] = "bat", ["aserai"] = "ase", ["khuzait"] = "khu",
    };
    private static readonly string[] ImperialKingKits = { "n_emp", "s_emp", "w_emp" };
    private const string FallbackKit = "emp_bat_template_medium";

    private readonly MappingTables _t;
    private readonly TroopResolver _troops;
    private readonly ForcePlanner _forces;

    public HeroPlanner(MappingTables tables, TroopResolver troops, ForcePlanner forces)
    {
        _t = tables;
        _troops = troops;
        _forces = forces;
    }

    public static bool IsPlayerCharacter(BattleSnapshot battle, CharacterSnapshot c) =>
        c.IsPlayer || (battle.PlayerCk3Id != 0 && c.Ref.Ck3Id == battle.PlayerCk3Id);

    /// <summary>Physically in the battle: commanding an army or fighting as a knight.</summary>
    public static bool IsPresent(CharacterSnapshot c) => c.IsCommander || c.IsKnight;

    /// <summary>The player's own character put on the field as the avatar although he neither commands an army nor rides as a knight
    /// (PROTOCOL §15: no army of his side has a commander to stand in).</summary>
    public static bool IsStandInPlayer(BattleSnapshot battle, CharacterSnapshot c) => IsPlayerCharacter(battle, c) && !IsPresent(c);

    /// <summary>Not physically in the CK3 combat: <c>ck3xbl_c_absent</c> when the mod logged it, else neither commander nor knight.
    /// An absent player is never killed, wounded or captured by the Bannerlord outcome (PROTOCOL §15).</summary>
    public static bool IsAbsent(CharacterSnapshot c) => c.Absent ?? !IsPresent(c);

    public static SideKey PlayerSideOf(BattleSnapshot battle)
    {
        if (battle.PlayerSide is { } ps) return ps.Key;
        foreach (var side in battle.Sides)
            if (side.Characters.Any(c => IsPlayerCharacter(battle, c))) return side.Key;
        foreach (var side in battle.Sides)
            if (battle.PlayerCk3Id != 0 && (side.Primary?.Ck3Id == battle.PlayerCk3Id || side.Armies.Any(a => a.Owner?.Ck3Id == battle.PlayerCk3Id)))
                return side.Key;
        // Nothing identifies the player (no C|PLAYER record): assume the defenders, since armies usually come to the player's realm.
        return SideKey.Defender;
    }

    public HeroAssignment Plan(BattleSnapshot battle, SidePlan attacker, SidePlan defender)
    {
        var playerSide = PlayerSideOf(battle);
        var plans = new[] { attacker, defender };
        var own = playerSide == SideKey.Attacker ? attacker : defender;

        var avatarChar = PickAvatar(battle, own.Snapshot);
        // One C record per character (PROTOCOL §5.2), so a primary participant riding as a knight is known from the S record only.
        bool avatarIsPrimary = avatarChar is not null && (avatarChar.IsPrimary || own.Snapshot.Primary?.Ck3Id == avatarChar.Ref.Ck3Id);
        // The player standing in for a commander-less host leads it (PROTOCOL §15).
        bool standIn = avatarChar is not null && IsStandInPlayer(battle, avatarChar);
        bool playerIsGeneral = avatarChar is null || standIn || avatarChar.IsCommander || (avatarIsPrimary && IsPresent(avatarChar!));
        CharacterSnapshot? ownGeneral = playerIsGeneral ? avatarChar : SideCommander(own.Snapshot, exclude: avatarChar);
        if (!playerIsGeneral && ownGeneral is null) { playerIsGeneral = true; ownGeneral = avatarChar; }

        var heroes = new List<PlannedHero>();
        PlannedHero? avatar = null;
        var generals = new Dictionary<SideKey, PlannedHero?>();
        foreach (var plan in plans)
        {
            var side = plan.Snapshot;
            var general = side.Key == playerSide ? ownGeneral : SideCommander(side, exclude: null);
            var candidates = HeroCandidates(side).ToList();
            if (standIn && side.Key == playerSide && !candidates.Contains(avatarChar!)) candidates.Insert(0, avatarChar!);
            foreach (var c in candidates)
            {
                bool isAvatar = ReferenceEquals(c, avatarChar);
                var h = new PlannedHero
                {
                    Character = c, Side = side.Key, IsAvatar = isAvatar,
                    Role = isAvatar && IsPlayerCharacter(battle, c) ? "player" : c.IsCommander ? "commander" : "knight",
                };
                heroes.Add(h);
                if (isAvatar) avatar = h;
                if (ReferenceEquals(c, general)) generals[side.Key] = h;
            }
        }

        // Keys: the avatar is always h0, then attacker heroes before defender heroes.
        heroes = heroes.OrderByDescending(h => h.IsAvatar).ThenBy(h => h.Side).ToList();
        var names = UniqueNames(heroes);
        for (int i = 0; i < heroes.Count; i++)
            heroes[i].Spec = BuildSpec(battle, heroes[i].Side == SideKey.Attacker ? attacker : defender, heroes[i], "h" + i, names[i]);

        foreach (var key in new[] { SideKey.Attacker, SideKey.Defender })
        {
            if (generals.GetValueOrDefault(key) is not { } g) continue;
            int top = heroes.Where(h => h.Side == key && !ReferenceEquals(h, g)).Select(h => h.Spec.Level).DefaultIfEmpty(0).Max();
            g.Spec.Level = Math.Min(62, Math.Max(g.Spec.Level, top + 3));
        }

        return new HeroAssignment
        {
            PlayerSide = playerSide, PlayerIsGeneral = playerIsGeneral, Heroes = heroes, Avatar = avatar,
            AttackerGeneral = generals.GetValueOrDefault(SideKey.Attacker), DefenderGeneral = generals.GetValueOrDefault(SideKey.Defender),
        };
    }

    /// <summary>Commanders (largest army first), then knights (highest prowess first).</summary>
    private static IEnumerable<CharacterSnapshot> HeroCandidates(SideSnapshot side) =>
        side.Characters
            .Where(IsPresent)
            .OrderByDescending(c => c.IsCommander)
            .ThenByDescending(c => c.IsCommander ? ArmyOf(side, c)?.Size ?? 0 : c.Prowess)
            .ThenBy(c => c.Tag);

    private static CharacterSnapshot? PickAvatar(BattleSnapshot battle, SideSnapshot side)
    {
        var player = side.Characters.FirstOrDefault(c => IsPlayerCharacter(battle, c) && IsPresent(c));
        if (player is not null) return player;
        foreach (var army in side.Armies.OrderByDescending(a => battle.PlayerCk3Id != 0 && a.Owner?.Ck3Id == battle.PlayerCk3Id).ThenByDescending(a => a.Size))
            if (CommanderOf(side, army) is { } cmd) return cmd;
        // No commander to stand in: the player himself, from his own C record on this side (PROTOCOL §15).
        var self = side.Characters.FirstOrDefault(c => IsPlayerCharacter(battle, c));
        if (self is not null) return self;
        return side.Characters.Where(c => c.IsKnight).OrderByDescending(c => c.Prowess).ThenBy(c => c.Tag).FirstOrDefault();
    }

    /// <summary>The commander of the primary participant's army, else of the largest army, else the best remaining hero.</summary>
    private static CharacterSnapshot? SideCommander(SideSnapshot side, CharacterSnapshot? exclude)
    {
        long primary = side.Primary?.Ck3Id ?? 0;
        foreach (var army in side.Armies.OrderByDescending(a => primary != 0 && a.Owner?.Ck3Id == primary).ThenByDescending(a => a.Size))
            if (CommanderOf(side, army) is { } cmd && !ReferenceEquals(cmd, exclude)) return cmd;
        return side.Characters
            .Where(c => (c.IsCommander || c.IsKnight) && !ReferenceEquals(c, exclude))
            .OrderByDescending(c => c.IsCommander).ThenByDescending(c => c.Martial).ThenBy(c => c.Tag)
            .FirstOrDefault();
    }

    private static CharacterSnapshot? CommanderOf(SideSnapshot side, ArmySnapshot army) =>
        side.Characters.FirstOrDefault(c => c.IsCommander && army.Commander is not null && c.Ref.Ck3Id == army.Commander.Ck3Id)
        ?? side.Characters.FirstOrDefault(c => c.IsCommander && c.ArmyTag != 0 && c.ArmyTag == army.Tag);

    private static ArmySnapshot? ArmyOf(SideSnapshot side, CharacterSnapshot c) =>
        side.Armies.FirstOrDefault(a => a.Commander is not null && a.Commander.Ck3Id == c.Ref.Ck3Id)
        ?? side.Armies.FirstOrDefault(a => c.ArmyTag != 0 && a.Tag == c.ArmyTag);

    // ---- names -------------------------------------------------------------------------------------------------------

    /// <summary>Unique across both sides: clashes get " of &lt;title&gt;", then a counter.</summary>
    private static List<string> UniqueNames(List<PlannedHero> heroes)
    {
        var baseNames = heroes.Select(h =>
        {
            string n = Text.SanitizeName(h.Character.Ref.Name);
            return n.Length > 0 ? n : h.Role == "knight" ? "Knight" : "Commander";
        }).ToList();
        var result = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < heroes.Count; i++)
        {
            string n = baseNames[i];
            bool clash = baseNames.Count(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase)) > 1 || used.Contains(n);
            if (clash)
            {
                string title = Text.SanitizeName(Text.TitleKeyToName(heroes[i].Character.Ref.TitleKey ?? heroes[i].Character.PrimaryTitleKey));
                if (title.Length > 0) n = $"{n} of {title}";
            }
            string unique = n;
            for (int k = 2; used.Contains(unique); k++) unique = $"{n} ({k})";
            used.Add(unique);
            result.Add(unique);
        }
        return result;
    }

    // ---- spec --------------------------------------------------------------------------------------------------------

    // Balance (CK3 stats -> Bannerlord heroes).
    // Soft caps (2026-10-20): up to prowess 40 (damage) / 50 (health) / 34 (skills) as before, then half as fast up to prowess 100,
    // so a champion of 88 is still far above one of 29 (live b322: 88 vs 29 fought as near equals, every factor capped at ~40).
    public const double ProwessDamagePerPoint = 0.025, ProwessDamageKnee = 40, ProwessDamageMax = 2.75;   // x(1 + 0.025 P), then +0.0125/pt
    public const double ProwessHealthKnee = 50, ProwessHealthMax = 5;                                    // 1 + P/25 to x3, then +0.04/pt
    public const int SkillKnee = 330, SkillMax = 400;                                                     // 60 + 8 P to 330, then +1/pt
    public const double HealthBase = 5, HealthPerPoint = 0.06, HealthFactorMin = 0.7, HealthFactorMax = 1.5;   // pool x(1 + 0.06 (health - 5)), 0.7..1.5

    public const double ProwessSpeedPerPoint = 0.004, ProwessSpeedMax = 1.10;            // speed x(1 + 0.004 P), at most +10 %
    // AI skill 0.25 + 0.025 P, 0.25..1 (prowess 30 = 1). Until 2026-10-18 the best AI came at prowess 20, so a prowess 14 knight
    // parried like a champion and a prowess 38 player needed two minutes to beat him in the lists.
    public const double AiLevelBase = 0.25, AiLevelPerPoint = 0.025;
    /// <summary>PROTOCOL §33: heroes with more CK3 prowess than this cut through the men they hit.</summary>
    public const int CleaveProwess = 30;

    /// <summary>How well an AI-controlled hero fights in Bannerlord (PROTOCOL §28): 0.25 at no prowess, 1 (the best) from prowess 30.</summary>
    public static double AiLevel(int prowess) => Math.Round(Math.Clamp(AiLevelBase + AiLevelPerPoint * Math.Max(0, prowess), AiLevelBase, 1), 3);

    /// <summary>Speed factor of a hero from CK3 prowess: 1 + 0.4 % per point, 1..1.1 (negative prowess does not lower it).</summary>
    public static double ProwessSpeed(int prowess) => Math.Clamp(1 + ProwessSpeedPerPoint * Math.Max(0, prowess), 1, ProwessSpeedMax);

    /// <summary>Damage factor of a hero from CK3 prowess: 1 + 0.025 per point to x2 at 40, then 0.0125 per point to x2.75 at 100
    /// (negative prowess does not lower it).</summary>
    public static double ProwessDamage(int prowess)
    {
        double p = Math.Max(0, prowess);
        double f = p <= ProwessDamageKnee ? 1 + ProwessDamagePerPoint * p
            : 1 + ProwessDamagePerPoint * ProwessDamageKnee + ProwessDamagePerPoint / 2 * (p - ProwessDamageKnee);
        return Math.Clamp(f, 1, ProwessDamageMax);
    }

    /// <summary>Health-pool factor from CK3 prowess: 1 + P/25 to x3 at 50, then the same 0.04 per point to x5 at 100.</summary>
    public static double ProwessHealth(int prowess) => Math.Clamp(1 + Math.Max(0, prowess) / 25.0, 1, ProwessHealthMax);

    /// <summary>Weapon skill from CK3 prowess: 60 + 8 per point to 330 (prowess 34), then 1 per point to 400.</summary>
    public static int WeaponSkill(int prowess)
    {
        int band = 60 + 8 * prowess;
        if (band <= SkillKnee) return Math.Clamp(band, 60, SkillKnee);
        int kneeProwess = (SkillKnee - 60 + 7) / 8;
        return Math.Clamp(SkillKnee + (prowess - kneeProwess), SkillKnee, SkillMax);
    }

    /// <summary>Health-pool factor from the CK3 character health value (base 5): 1 + 0.06 x (health - 5), 0.7..1.5; not finite = neutral.</summary>
    public static double HealthFactor(double? health) =>
        health is { } v && double.IsFinite(v) ? Math.Clamp(1 + HealthPerPoint * (v - HealthBase), HealthFactorMin, HealthFactorMax) : 1;

    /// <summary>
    /// The CK3-stat balance (prowess damage, character health, commander martial, age) runs only for a snapshot whose CK3 mod logs
    /// character health (ck3xbl_c_health); an older mod keeps the earlier numbers.
    /// </summary>
    public static bool StatBalance(CharacterSnapshot c) => c.Health.HasValue;

    private HeroSpec BuildSpec(BattleSnapshot battle, SidePlan plan, PlannedHero h, string key, string name)
    {
        var c = h.Character;
        var culture = _forces.CharacterCulture(battle, plan, c);
        var (roster, formation, dismount) = Kit(c, culture);
        var equipment = new EquipmentSpec { Roster = roster };
        if (dismount)
        {
            // Empty override = leave the slot empty (PROTOCOL §3.2 addition): this culture's nobles fight on foot.
            equipment.Overrides["Horse"] = "";
            equipment.Overrides["HorseHarness"] = "";
        }

        int weapon = WeaponSkill(c.Prowess);                        // skill band of the prowess rule
        int command = Math.Clamp(10 * c.Martial, 0, 300);            // martial -> Tactics (AI tactic unlocks at 20/50)
        return new HeroSpec
        {
            Key = key,
            Ck3Id = c.Ref.Ck3Id,
            Tag = c.Tag,
            Role = h.Role,
            IsPlayer = h.IsAvatar,
            Name = name,
            Female = c.Female,
            Age = c.Age > 0 ? Math.Clamp(c.Age, 16, 99) : 30,
            Level = Math.Clamp(10 + c.Prowess + 2 * c.Tier, 10, 40),
            Formation = formation,
            Captain = true,
            Equipment = equipment,
            Skills = new Dictionary<string, int>
            {
                ["OneHanded"] = weapon, ["TwoHanded"] = weapon, ["Polearm"] = weapon, ["Bow"] = weapon, ["Crossbow"] = weapon,
                ["Throwing"] = weapon, ["Riding"] = weapon, ["Athletics"] = weapon, ["Tactics"] = command, ["Leadership"] = command,
            },
            // Health pool: prowess (1 + P/25, 1..5) x CK3 character health. Damage: prowess.
            HealthMultiplier = (float)Math.Clamp(ProwessHealth(c.Prowess) * HealthFactor(c.Health), 0.5, CK3xBL.Mapping.Buffs.KnightBuffPlanner.HeroDurabilityCap),
            DamageMultiplier = StatBalance(c) ? (float)ProwessDamage(c.Prowess) : 1f,
        };
    }

    /// <summary>
    /// Ruler/lord roster by rank, culture and sex: kings and emperors wear the king kits, dukes the heavy
    /// lord kit, everyone else the medium kit; women the lady kits. Formation follows the culture's knight line, so nobles of
    /// foot cultures (early Norse, forest Africa, ...) fight dismounted.
    /// </summary>
    private (string Roster, string Formation, bool Dismount) Kit(CharacterSnapshot c, CultureChoice culture)
    {
        string prefix = KitPrefix.GetValueOrDefault(culture.BlCulture, "emp");
        string sex = c.Female ? "f" : "m";
        var candidates = new List<string>();
        if (c.Tier >= 4)
        {
            string kingPrefix = prefix == "emp" ? ImperialKingKits[(int)(Math.Abs(c.Ref.Ck3Id) % ImperialKingKits.Length)] : prefix;
            candidates.Add($"{kingPrefix}_king_template_bat_{sex}");
        }
        if (c.Female) candidates.Add($"{prefix}_bat_template_lady");
        else if (c.Tier == 3) candidates.Add($"{prefix}_bat_template_heavy");
        candidates.Add($"{prefix}_bat_template_medium");
        candidates.Add(FallbackKit);
        string roster = candidates.First(_t.Kits.ContainsKey);
        bool kitHasHorse = _t.Kits[roster].HasHorse;

        var knights = _troops.ResolveKnights(culture.Profile, culture.Era);
        var lead = knights.OrderByDescending(k => k.Weight).Select(k => _t.Troops.GetValueOrDefault(k.Troop)).FirstOrDefault(t => t is not null);
        string formation = lead?.FormationFromGear ?? lead?.Formation ?? "Cavalry";
        bool mounted = formation is "Cavalry" or "HorseArcher";
        if (mounted && !kitHasHorse) formation = "Infantry";
        return (roster, formation, !mounted && kitHasHorse);
    }
}
