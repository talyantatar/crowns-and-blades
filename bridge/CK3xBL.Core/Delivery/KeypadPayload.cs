// The register payload both IN channels share (PROTOCOL §2.2): Ck3Outcome -> 200 integer registers + checksum, and the keypad
// key sequence that stores them through CK3's modder hotkeys.
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Logs;

namespace CK3xBL.Core.Delivery;

public static class KeypadLayout
{
    public const int RegisterCount = 200;
    /// <summary>r0. Version 3 carries the position-weighted checksum (PROTOCOL §10.3); the mod still accepts version 2 (plain sum).</summary>
    public const int PayloadVersion = 3;
    public const int LegacyPayloadVersion = 2;
    /// <summary>r0 of the battle clock (PROTOCOL §14.2): CK3 steers its own combat to the result. Same checksum as version 3.</summary>
    public const int SteerPayloadVersion = 4;
    /// <summary>Version 4 only: the kill targets (enemy dead CK3 must count for each side) and the steering hint (pace day).</summary>
    public const int AttackerKillTarget = 8, DefenderKillTarget = 9, SteerHint = 198;
    /// <summary>Version 3 of a single combat (PROTOCOL §18): 1 when the loser was killed in Bannerlord.</summary>
    public const int DuelLoserKilled = 8;
    /// <summary>The pace day r198 may name; 0 = the mod's default (12).</summary>
    public const int MinPaceDays = 6, MaxPaceDays = 13;
    /// <summary>Version 3: register i is weighted (i mod WeightCycle) + 1.</summary>
    public const int WeightCycle = 16;
    public const int Version = 0, Bid = 1, Outcome = 2, Province = 3;
    public const int MyLost = 4, EnemyLost = 5, MyKills = 6, PrestigeBonus = 7;
    public const int ArmyBase = 10, ArmyStride = 14, ArmySlots = 8, MaaPerArmy = 6;
    public const int CharacterBase = 130, CharacterStride = 3, CharacterSlots = 20;
    /// <summary>Award slots (PROTOCOL §13.5): slot k = (tag, value) at AwardBlockA + 2k for k 0..3 and AwardBlockB + 2(k − 4) for
    /// k 4..7, in registers that payload 3 left free. Value A = rolls + 10 × glory/5 + 1000 × prestige/25.</summary>
    public const int AwardBlockA = 122, AwardBlockB = 190, AwardSlotsPerBlock = 4, AwardSlots = 8, AwardStride = 2;
    public const int MaxAwardRolls = 3, MaxAwardGloryUnits = 20, MaxAwardPrestigeUnits = 40;
    public const int AwardGloryUnit = 5, AwardPrestigeUnit = 25;
    public const int Checksum = 199;
    public const int Modulus = 9973;
    /// <summary>Registers hold values below 10^6 (CK3 fixed point).</summary>
    public const int MaxValue = 999_999;

    /// <summary>First register (the tag) of award slot k; the value follows it.</summary>
    public static int AwardSlot(int k) =>
        k < AwardSlotsPerBlock ? AwardBlockA + AwardStride * k : AwardBlockB + AwardStride * (k - AwardSlotsPerBlock);
}

/// <summary>A key of the CK3 keypad (all pressed with Ctrl+Shift+Alt held).</summary>
public enum KeypadKey
{
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    /// <summary>F1: r[idx] = acc, idx += 1, acc = 0.</summary>
    Store,
    /// <summary>F2: verify checksum and bid, set ck3xbl_ready, log IN|OK / IN|BAD.</summary>
    Commit,
    /// <summary>F3: all registers 0, idx = 0, acc = 0.</summary>
    Reset,
    /// <summary>F5: idx = acc, acc = 0.</summary>
    Seek,
}

public sealed class KeypadPayload
{
    public int[] Registers { get; }

    private KeypadPayload(int[] registers) => Registers = registers;

    public int Bid => Registers[KeypadLayout.Bid];

    /// <summary>The payload version r0 (3 or 4).</summary>
    public int Version => Registers[KeypadLayout.Version];

    /// <summary>Encodes an outcome as payload <see cref="Ck3Outcome.PayloadVersion"/> (4 = battle clock, anything else = 3). Slots
    /// beyond the register map are dropped with a warning. Values are clamped to 0..999,999. Characters whose fate is "unharmed" are
    /// not written (they change nothing in CK3, PROTOCOL §13.5). Award slots follow <see cref="Ck3Outcome.Awards"/> in order without
    /// gaps; <paramref name="awardKeyBudget"/> caps the keys the award block adds to the key sequence (keypad channel), dropping
    /// awards from the last one; null = no cap (run file). Version 4 (PROTOCOL §14.2) adds the kill targets r8/r9 and the pace day
    /// r198 and writes absolute levy survivors in r11 + 14i instead of the levy loss ‰.</summary>
    public static KeypadPayload Encode(Ck3Outcome outcome, MaaIndexTable? maaIndex, ICollection<string>? warnings = null,
        int? awardKeyBudget = null)
    {
        var r = new int[KeypadLayout.RegisterCount];
        void Set(int index, long value, string what)
        {
            if (value < 0 || value > KeypadLayout.MaxValue)
            {
                warnings?.Add($"{what} = {value} is outside 0..{KeypadLayout.MaxValue}; clamped");
                value = Math.Clamp(value, 0, KeypadLayout.MaxValue);
            }
            r[index] = (int)value;
        }

        bool steer = outcome.PayloadVersion == KeypadLayout.SteerPayloadVersion;
        Set(KeypadLayout.Version, steer ? KeypadLayout.SteerPayloadVersion : KeypadLayout.PayloadVersion, "version");
        Set(KeypadLayout.Bid, outcome.Bid, "bid");
        Set(KeypadLayout.Outcome, outcome.OutcomeCode, "outcome");
        Set(KeypadLayout.Province, outcome.ProvinceId, "province");
        Set(KeypadLayout.MyLost, outcome.MyLost, "my_lost");
        Set(KeypadLayout.EnemyLost, outcome.EnemyLost, "enemy_lost");
        // r6: the player's kills (at most 999), with the second feat mask above them (PROTOCOL §45).
        Set(KeypadLayout.MyKills, Orchestration.FeatLedger.Register6(outcome.MyKills, outcome.FeatMask), "my_kills");
        // r7: the prestige bonus, with the battle feats above it (PROTOCOL §32).
        Set(KeypadLayout.PrestigeBonus, Orchestration.FeatLedger.Register7(outcome.PrestigeBonus, outcome.FeatMask), "prestige_bonus");
        // A single combat (PROTOCOL §18) is payload 3; r8 (0 there for a battle) says whether the loser was killed in Bannerlord.
        if (outcome.IsDuel && !steer) Set(KeypadLayout.DuelLoserKilled, outcome.DuelLoserKilled ? 1 : 0, "duel_loser_killed");
        if (steer)
        {
            Set(KeypadLayout.AttackerKillTarget, outcome.AttackerKillTarget, "attacker_kill_target");
            Set(KeypadLayout.DefenderKillTarget, outcome.DefenderKillTarget, "defender_kill_target");
            int pace = outcome.PaceDays;
            if (pace != 0 && pace is < KeypadLayout.MinPaceDays or > KeypadLayout.MaxPaceDays)
            {
                warnings?.Add($"pace day {pace} is outside {KeypadLayout.MinPaceDays}..{KeypadLayout.MaxPaceDays}; clamped");
                pace = Math.Clamp(pace, KeypadLayout.MinPaceDays, KeypadLayout.MaxPaceDays);
            }
            Set(KeypadLayout.SteerHint, pace, "pace_day");   // the flags (100 × f) are reserved and stay 0
        }

        // A version 4 outcome sent as version 3 (the old-mod fallback) may list an army only for its levy survivors: as version 3
        // that slot is a 0 ‰ deplete without MaA pairs, which changes nothing in CK3 and costs keys, so it is left out.
        var armies = steer ? outcome.Armies : outcome.Armies.Where(a => a.LevyLossPermille != 0 || a.Maa.Count > 0).ToList();
        if (armies.Count > KeypadLayout.ArmySlots)
            warnings?.Add($"{armies.Count} armies but only {KeypadLayout.ArmySlots} slots; extra armies keep their CK3 losses");
        for (int i = 0; i < Math.Min(armies.Count, KeypadLayout.ArmySlots); i++)
        {
            var army = armies[i];
            int b = KeypadLayout.ArmyBase + KeypadLayout.ArmyStride * i;
            Set(b, army.ArmyTag, $"army[{i}].tag");
            if (steer) Set(b + 1, army.LevySurvivors, $"army[{i}].levySurvivors");
            else Set(b + 1, Math.Clamp(army.LevyLossPermille, 0, 1000), $"army[{i}].levyLoss");
            if (army.Maa.Count > KeypadLayout.MaaPerArmy)
                warnings?.Add($"army {army.ArmyTag}: {army.Maa.Count} MaA types but only {KeypadLayout.MaaPerArmy} slots");
            int k = 0;
            foreach (var maa in army.Maa)
            {
                if (k >= KeypadLayout.MaaPerArmy) break;
                int index = maa.MaaIndex > 0 ? maa.MaaIndex : maaIndex?.IndexOf(maa.MaaKey) ?? 0;
                if (index <= 0)
                {
                    warnings?.Add($"army {army.ArmyTag}: MaA '{maa.MaaKey}' has no keypad index; skipped");
                    continue;
                }
                Set(b + 2 + 2 * k, index, $"army[{i}].maa[{k}].index");
                Set(b + 3 + 2 * k, Math.Clamp(maa.SurvivorSharePermille, 0, 1000), $"army[{i}].maa[{k}].survivors");
                k++;
            }
        }

        // Every mod version applies a character slot only when its fate is > 0, and finds a killer by tag, not through a slot.
        var characters = outcome.Characters.Where(c => c.Tag > 0 && c.Fate > 0).ToList();
        if (characters.Count > KeypadLayout.CharacterSlots)
        {
            warnings?.Add($"{characters.Count} harmed characters but only {KeypadLayout.CharacterSlots} slots; " +
                          $"{characters.Count - KeypadLayout.CharacterSlots} left out (those with a killer are kept first)");
            characters = characters.OrderByDescending(c => c.KillerTag > 0).Take(KeypadLayout.CharacterSlots).ToList();
        }
        for (int j = 0; j < characters.Count; j++)
        {
            var c = characters[j];
            int b = KeypadLayout.CharacterBase + KeypadLayout.CharacterStride * j;
            Set(b, c.Tag, $"character[{j}].tag");
            Set(b + 1, Math.Clamp(c.Fate, 0, 5), $"character[{j}].fate");
            Set(b + 2, c.KillerTag, $"character[{j}].killer");
        }

        var awards = AwardSlots(outcome.Awards, warnings);
        int keep = awardKeyBudget is { } budget ? AwardsWithinBudget(r, awards, budget) : awards.Count;
        foreach (var (tag, value) in awards.Skip(keep))
            warnings?.Add($"award for character {tag} (value {value}) dropped: the award block would exceed {awardKeyBudget} keys");
        PutAwards(r, awards, keep);

        r[KeypadLayout.Checksum] = ComputeChecksum(r, r[KeypadLayout.Version]);
        return new KeypadPayload(r);
    }

    /// <summary>PROTOCOL §14.3: a mod from before the battle clock answers a version 4 payload with IN|BAD, without
    /// ck3xbl_in_maxver, and with its checksum equal to ours (the keys arrived intact; only the version was refused). Only then is the
    /// same outcome sent again as version 3. Any other IN|BAD (stale bid, overflow, a lost key) is an ordinary failure.</summary>
    public bool RefusedByOldMod(InputAck ack) =>
        Version == KeypadLayout.SteerPayloadVersion && ack is { Ok: false, MaxVersion: 0 } && ack.Sum == Registers[KeypadLayout.Checksum];

    /// <summary>The checksum r199 over r0..r198 exactly as the mod's ck3xbl_commit_effect computes it (PROTOCOL §10.3, §14.3),
    /// reduced after every term: versions 3 and 4 = Σ (r_i mod 9973) × ((i mod 16) + 1) mod 9973; version 2 = Σ r_i mod 9973. The
    /// mod logs this value as ck3xbl_in_sum. Reference: tools/gen/lib/registers.mjs payloadChecksum.</summary>
    public static int ComputeChecksum(IReadOnlyList<int> registers, int version = KeypadLayout.PayloadVersion)
    {
        long sum = 0;
        for (int i = 0; i < KeypadLayout.Checksum; i++)
        {
            long term = version == KeypadLayout.LegacyPayloadVersion
                ? registers[i]
                : (long)(registers[i] % KeypadLayout.Modulus) * (i % KeypadLayout.WeightCycle + 1);
            sum = (sum + term) % KeypadLayout.Modulus;
        }
        return (int)sum;
    }

    /// <summary>The packed award value A = R + 10·G + 1000·P (PROTOCOL §13.5): R = prowess rolls 0..3, G = glory / 5 (0..20),
    /// P = prestige / 25 (0..40), each rounded to the nearest unit. 0 = nothing to award.</summary>
    public static int AwardValue(CharacterAward award, ICollection<string>? warnings = null)
    {
        int Unit(int amount, int unit, int max, string what)
        {
            int units = (int)Math.Round(amount / (double)unit, MidpointRounding.AwayFromZero);
            if (units < 0 || units > max) warnings?.Add($"award for character {award.Tag}: {what} {amount} is outside 0..{max * unit}; clamped");
            return Math.Clamp(units, 0, max);
        }
        int rolls = Unit(award.ProwessRolls, 1, KeypadLayout.MaxAwardRolls, "prowess rolls");
        int glory = Unit(award.Glory, KeypadLayout.AwardGloryUnit, KeypadLayout.MaxAwardGloryUnits, "glory");
        int prestige = Unit(award.Prestige, KeypadLayout.AwardPrestigeUnit, KeypadLayout.MaxAwardPrestigeUnits, "prestige");
        return rolls + 10 * glory + 1000 * prestige;
    }

    /// <summary>The awards that get a slot, in priority order: tag and value > 0, at most <see cref="KeypadLayout.AwardSlots"/>.</summary>
    private static List<(int Tag, int Value)> AwardSlots(IEnumerable<CharacterAward> awards, ICollection<string>? warnings)
    {
        var slots = new List<(int Tag, int Value)>();
        foreach (var award in awards)
        {
            int value = AwardValue(award, warnings);
            if (award.Tag <= 0 || award.Tag > KeypadLayout.MaxValue || value == 0) continue;
            if (slots.Count == KeypadLayout.AwardSlots)
            {
                warnings?.Add($"award for character {award.Tag} (value {value}) dropped: only {KeypadLayout.AwardSlots} award slots");
                continue;
            }
            slots.Add((award.Tag, value));
        }
        return slots;
    }

    /// <summary>How many of the awards (from the first) fit in <paramref name="budget"/> keys: the keys a block of n awards adds to
    /// the sequence of <paramref name="registers"/> (seeks and checksum digits included) must not exceed the budget.</summary>
    private static int AwardsWithinBudget(int[] registers, List<(int Tag, int Value)> awards, int budget)
    {
        int KeyCount(int n)
        {
            var trial = (int[])registers.Clone();
            PutAwards(trial, awards, n);
            trial[KeypadLayout.Checksum] = ComputeChecksum(trial, trial[KeypadLayout.Version]);
            return KeySequence(trial).Count;
        }
        int without = KeyCount(0);
        int keep = awards.Count;
        while (keep > 0 && KeyCount(keep) - without > budget) keep--;
        return keep;
    }

    private static void PutAwards(int[] registers, List<(int Tag, int Value)> awards, int count)
    {
        for (int k = 0; k < count; k++)
        {
            int b = KeypadLayout.AwardSlot(k);
            (registers[b], registers[b + 1]) = awards[k];
        }
    }

    /// <summary>The non-zero registers (what the run-file channel sets explicitly).</summary>
    public IEnumerable<(int Index, int Value)> NonZero() =>
        Registers.Select((v, i) => (i, v)).Where(x => x.v != 0).Select(x => (x.i, x.v));

    /// <summary>Key sequence for the keypad, identical to the mod's reference encoder (tools/gen/keypad_payload.mjs): Reset (F3), then
    /// for every non-zero register a seek when it is not the next index (digits + F5) and its digits + Store (F1), then Commit (F2).</summary>
    public IReadOnlyList<KeypadKey> ToKeySequence() => KeySequence(Registers);

    private static List<KeypadKey> KeySequence(IReadOnlyList<int> registers)
    {
        var keys = new List<KeypadKey> { KeypadKey.Reset };
        int idx = 0;
        for (int target = 0; target < KeypadLayout.RegisterCount; target++)
        {
            int value = registers[target];
            if (value == 0) continue;
            if (target != idx)
            {
                keys.AddRange(Digits(target));
                keys.Add(KeypadKey.Seek);
            }
            keys.AddRange(Digits(value));
            keys.Add(KeypadKey.Store);
            idx = target + 1;
        }
        keys.Add(KeypadKey.Commit);
        return keys;
    }

    private static List<KeypadKey> Digits(int value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture).Select(ch => (KeypadKey)(ch - '0')).ToList();
}

/// <summary>bridge/data/ck3_maa_index.json: 1-based keypad index of every MaA type key (append-only table).</summary>
public sealed class MaaIndexTable
{
    private readonly Dictionary<string, int> _index;

    public MaaIndexTable(IEnumerable<string> keys)
    {
        _index = new Dictionary<string, int>(StringComparer.Ordinal);
        int i = 1;
        foreach (var k in keys) _index.TryAdd(k, i++);
    }

    public int Count => _index.Count;

    /// <summary>1-based index, or 0 when unknown.</summary>
    public int IndexOf(string maaKey) => _index.TryGetValue(maaKey, out var i) ? i : 0;

    public static MaaIndexTable Load(string path)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return new MaaIndexTable(doc.RootElement.GetProperty("keys").EnumerateArray().Select(e => e.GetString() ?? ""));
    }
}
