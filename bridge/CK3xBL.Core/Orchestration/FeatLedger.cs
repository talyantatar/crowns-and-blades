// Battle feats (PROTOCOL §32): the CK3 player's own deeds in Bannerlord field battles earn CK3 traits and modifiers. The ledger keeps
// the running counts per player character (feats.json in the shared folder) and turns one clash's deeds into the feat mask the mod
// applies (r7 = prestige bonus + FeatBase × mask). The mod never grants a trait twice (it adds trait experience instead) and refreshes
// a modifier, so a count that stays over its threshold may send its bit again.
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Json;

namespace CK3xBL.Core.Orchestration;

[Flags]
public enum Feat
{
    None = 0,
    // Feat modifiers are for life with three ranks (PROTOCOL §45): every bit sent raises the feat's rank in CK3.
    Reaper = 1,              // 50+ kills in one battle: ck3xbl_feat_reaper
    Carnage = 2,             // 100+ kills in one battle: ck3xbl_feat_carnage, +200 prestige
    Legend = 4,              // 1000, 2500, 5000 kills in a lifetime: ck3xbl_feat_legend
    CommanderSlayer = 8,     // downed an enemy commander himself: +150 prestige, ck3xbl_feat_slayer
    Defender = 16,           // 3 defensive wins against a larger enemy: unyielding_defender
    Attacker = 32,           // 5 wins in attack: aggressive_attacker
    Miracle = 64,            // a win against twice his numbers or more: +300 prestige, ck3xbl_feat_miracle
    Invulnerable = 128,      // 10 battles in a row without a wound: ck3xbl_feat_invulnerable
    Scars = 256,             // 5, 10, 20 battles that wounded him: ck3xbl_feat_scars
    Winter = 512,            // 3 winter wins: winter_soldier
    TerrainRough = 1024,     // terrain code in bits 10..11: 1 rough_terrain_expert (3 wins in hills, mountains, wetlands),
    TerrainForest = 2048,    // 2 forest_fighter (forest, taiga, jungle), 3 desert_warrior (desert, drylands, oasis)
    TerrainDesert = TerrainRough | TerrainForest,
    // The second mask (bits 12..18), sent in r6 as kills + 1000 × (mask >> 12).
    LastStand = 1 << 12,     // a defensive win against three times his numbers: ck3xbl_feat_last_stand, +250 prestige
    KnightHunter = 1 << 13,  // 5+ enemy heroes downed by his hand in one battle: ck3xbl_feat_knight_hunter
    BloodyHarvest = 1 << 14, // 250+ kills in one battle: ck3xbl_feat_bloody_harvest, +300 prestige
    Veteran = 1 << 15,       // 25, 50, 100 battles fought in person: ck3xbl_feat_veteran
    SeaWolf = 1 << 16,       // 3, 6, 10 battles at sea won: ck3xbl_feat_sea_wolf
    FirstToBoard = 1 << 17,  // a battle at sea won with 10+ kills of his own: ck3xbl_feat_first_to_board
    CoastTerror = 1 << 18,   // 5, 10, 20 landings won as the attacker: ck3xbl_feat_coast_terror
}

public sealed class FeatRecord
{
    public int Kills;
    public int DefensiveWins;
    public int AttackWins;
    public int WinterWins;
    public Dictionary<string, int> TerrainWins = new();
    public int UnwoundedStreak;
    public int WoundedBattles;
    public int BattlesFought;
    public int SeaWins;
    public int LandingWins;
    public List<string> Battles = new();               // battle ids already counted (a clash is counted once, whatever re-delivery)
}

public static class FeatLedger
{
    /// <summary>r7 = prestige bonus (0..225) + FeatBase × mask (0..4095): at most 925,695, under the register limit.</summary>
    public const int FeatBase = 226;
    public const int MaxPrestigeBonus = FeatBase - 1;
    public const int ReaperKills = 50, CarnageKills = 100, BloodyHarvestKills = 250, DefensiveWinsNeeded = 3, AttackWinsNeeded = 5,
        TerrainWinsNeeded = 3, UnwoundedNeeded = 10, KnightsNeeded = 5, BoardingKills = 10;
    public const double MiracleOdds = 2.0, LastStandOdds = 3.0;
    /// <summary>Lifetime counts whose crossing earns a rank of the feat (I, II, III).</summary>
    public static readonly int[] LegendKills = { 1000, 2500, 5000 }, WoundsNeeded = { 5, 10, 20 }, VeteranBattles = { 25, 50, 100 },
        SeaWinsNeeded = { 3, 6, 10 }, LandingWinsNeeded = { 5, 10, 20 };
    /// <summary>The first mask rides in r7 (12 bits), the rest in r6 above the kills.</summary>
    public const int FirstMaskBits = 12, KillsBase = 1000;
    private const int RememberedBattles = 200;

    private static bool Crossed(int before, int after, int[] marks) => marks.Any(m => before < m && after >= m);

    /// <summary>Counts one clash into <paramref name="record"/> and returns the feats it earns. A battle id counted before earns nothing.</summary>
    public static Feat Count(FeatRecord record, BattleFeats deeds, string battleId)
    {
        if (!string.IsNullOrEmpty(battleId))
        {
            if (record.Battles.Contains(battleId)) return Feat.None;
            record.Battles.Add(battleId);
            if (record.Battles.Count > RememberedBattles) record.Battles.RemoveRange(0, record.Battles.Count - RememberedBattles);
        }
        var feats = Feat.None;
        int kills = Math.Max(0, deeds.Kills);
        int killsBefore = record.Kills;
        record.Kills += kills;
        if (kills >= BloodyHarvestKills) feats |= Feat.BloodyHarvest;
        if (kills >= CarnageKills) feats |= Feat.Carnage;
        else if (kills >= ReaperKills) feats |= Feat.Reaper;
        if (Crossed(killsBefore, record.Kills, LegendKills)) feats |= Feat.Legend;
        if (deeds.SlewCommander) feats |= Feat.CommanderSlayer;
        if (deeds.HeroesDowned >= KnightsNeeded) feats |= Feat.KnightHunter;
        if (Crossed(record.BattlesFought, ++record.BattlesFought, VeteranBattles)) feats |= Feat.Veteran;

        if (deeds.Won)
        {
            if (deeds.Defending && deeds.EnemyToOwn >= LastStandOdds) feats |= Feat.LastStand;
            if (deeds.AtSea)
            {
                if (Crossed(record.SeaWins, ++record.SeaWins, SeaWinsNeeded)) feats |= Feat.SeaWolf;
                if (kills >= BoardingKills) feats |= Feat.FirstToBoard;
            }
            if (deeds.Landing && !deeds.Defending && Crossed(record.LandingWins, ++record.LandingWins, LandingWinsNeeded)) feats |= Feat.CoastTerror;
            if (deeds.Defending && deeds.EnemyToOwn > 1 && ++record.DefensiveWins >= DefensiveWinsNeeded) feats |= Feat.Defender;
            if (!deeds.Defending && ++record.AttackWins >= AttackWinsNeeded) feats |= Feat.Attacker;
            if (deeds.EnemyToOwn >= MiracleOdds) feats |= Feat.Miracle;
            if (deeds.Winter && ++record.WinterWins >= TerrainWinsNeeded) feats |= Feat.Winter;
            if (deeds.Terrain is "rough" or "forest" or "desert")
            {
                record.TerrainWins[deeds.Terrain] = record.TerrainWins.GetValueOrDefault(deeds.Terrain) + 1;
                if (record.TerrainWins[deeds.Terrain] >= TerrainWinsNeeded)
                    feats |= deeds.Terrain switch { "rough" => Feat.TerrainRough, "forest" => Feat.TerrainForest, _ => Feat.TerrainDesert };
            }
        }

        if (deeds.Wounded)
        {
            record.UnwoundedStreak = 0;
            if (Crossed(record.WoundedBattles, ++record.WoundedBattles, WoundsNeeded)) feats |= Feat.Scars;
        }
        else if (++record.UnwoundedStreak >= UnwoundedNeeded)
        {
            feats |= Feat.Invulnerable;
            record.UnwoundedStreak = 0;   // the next ten battles renew it
        }
        return feats;
    }

    /// <summary>Reads the ledger file, counts the clash for its player character and writes the file back. Never throws: a ledger
    /// that cannot be read or written costs the feats of this battle, not the battle.</summary>
    public static Feat CountInFile(string path, BattleFeats deeds, string battleId, out string? problem)
    {
        problem = null;
        try
        {
            var all = (File.Exists(path) ? ProtocolJson.TryRead<Dictionary<string, FeatRecord>>(path) : null) ?? new();
            string key = deeds.Ck3Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!all.TryGetValue(key, out var record)) all[key] = record = new FeatRecord();
            var feats = Count(record, deeds, battleId);
            ProtocolJson.WriteAtomic(path, all, indented: true);
            return feats;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            problem = e.Message;
            return Feat.None;
        }
    }

    /// <summary>The r7 value: the prestige bonus with the first 12 bits of the feat mask above it.</summary>
    public static int Register7(int prestigeBonus, int featMask) =>
        Math.Clamp(prestigeBonus, 0, MaxPrestigeBonus) + FeatBase * (Math.Max(0, featMask) & ((1 << FirstMaskBits) - 1));

    /// <summary>The r6 value (PROTOCOL §45): the player's kills (0..999) with the feat mask's bits 12..18 above them.</summary>
    public static int Register6(int kills, int featMask) =>
        Math.Clamp(kills, 0, KillsBase - 1) + KillsBase * ((Math.Max(0, featMask) >> FirstMaskBits) & 127);
}
