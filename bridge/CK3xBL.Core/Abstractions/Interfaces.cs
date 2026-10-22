// Seams between the bridge libraries. Each library implements its interface; CK3xBL (the exe) wires them together.
// Keep these signatures stable: several components are built in parallel against them.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CK3xBL.Core.Model;
using CK3xBL.Protocol;

namespace CK3xBL.Core.Abstractions;

/// <summary>CK3xBL.Mapping: CK3 battle -> Bannerlord request, and Bannerlord result -> CK3 outcome.</summary>
public interface IBattleMapper
{
    /// Builds the full request (scene, season, troops, heroes without appearance details, banners from culture). Appearance and
    /// CoA banners are filled in afterwards by IAppearanceProvider.Enrich.
    BattleRequest BuildRequest(BattleSnapshot battle, MapperOptions options);

    /// Converts a Bannerlord result into what CK3 must apply (per army losses, per character fates, outcome, summary numbers).
    Ck3Outcome BuildOutcome(BattleSnapshot battle, BattleRequest request, BattleResult result, MapperOptions options);

    /// The same for a clash that continues an earlier one (PROTOCOL §14.6): kill targets and r4..r6 are cumulative over the
    /// clashes. <paramref name="battle"/> is the snapshot the request was planned from (the capped one for a continuation).
    /// Without a parent it is the overload above, so mappers without battle-clock support keep working.
    Ck3Outcome BuildOutcome(BattleSnapshot battle, BattleRequest request, BattleResult result, MapperOptions options, ParentLink? parent) =>
        parent is null
            ? BuildOutcome(battle, request, result, options)
            : throw new NotSupportedException($"{GetType().Name} cannot build the outcome of a continuation clash");

    /// Plans a second (or later) clash of a steered CK3 combat (PROTOCOL §14.6): every parent army is capped at its end state of
    /// the earlier clashes, fallen characters are removed, and the parent's scene, season, coats of arms and presentation are kept.
    ContinuationPlan BuildContinuation(BattleSnapshot live, ParentLink parent, MapperOptions options) =>
        throw new NotSupportedException($"{GetType().Name} cannot plan a continuation clash");
}

/// <summary>The earlier clash a reinforcement clash continues (PROTOCOL §14.6), as the bridge planned and resolved it.</summary>
public sealed class ParentLink
{
    public required BattleSnapshot Planned;           // the snapshot the parent's request and outcome were built from
    public required BattleRequest Request;
    public required Ck3Outcome Outcome;
    public ISet<int> FallenTags = new HashSet<int>(); // character tags captured or killed (fate 3/4) in any earlier clash
    /// <summary>The first clash's planned snapshot; null when <see cref="Planned"/> is the first clash. The mod stores an army's
    /// steering-start state once, at the first clash it fights, so from clash 3 on the outcome needs it.</summary>
    public BattleSnapshot? Root;
}

/// <summary>A planned continuation clash: the request to fight, and the capped snapshot it was planned from (the outcome of the
/// clash is built from <see cref="Capped"/>, not from the live snapshot).</summary>
public sealed class ContinuationPlan
{
    public required BattleRequest Request;
    public required BattleSnapshot Capped;
}

public sealed class MapperOptions
{
    public int MaxAgentsTotal = 1000;                 // Bannerlord agents across both sides (excluding heroes)
    public int MinAgentsPerSide = 40;
    public int BattleSize = 1000;                     // simultaneous agents on the field (Bannerlord's largest battle size)
    public bool PlayerDeathIsReal;                    // hardcore: Bannerlord death of the player avatar can kill the CK3 ruler
    public double KilledToCk3DeadShareWinner = 0.45;  // share of BL "killed" that become CK3 losses for the winner side
    public double KilledToCk3DeadShareLoser = 0.65;
    public double RoutedToCk3DeadShareLoser = 0.25;
    public int ReturnCountdownSeconds = 10;
    public string Deployment = "order_of_battle";
    public string DataDirectory = "";                 // bridge/data
    public BattleClockOptions BattleClock = new();
    // Strength balance (PROTOCOL §17): the side that Bannerlord would make weaker than CK3 does gets damage and health.
    public bool StrengthBalance = true;
    public double StrengthTiersPerDoubling = 3;       // Bannerlord troop tiers per doubling of an agent's worth (calibrated in play)
    public double StrengthMaxFactor = 4;              // largest strength factor (damage and health x2 each)
    // Costumes (PROTOCOL §21): troops wear the armour of their CK3 culture, era and role, at the stock protection.
    public bool Costumes = true;
    // PROTOCOL §26: language of the cards and Bannerlord texts, "en" or "ru" (the bridge sets it from CK3's language).
    public string Language = "en";
    // PROTOCOL §40: how a relief battle (BattleSnapshot.Relief) is fought, from the player's choice: "" in the field, "defend" behind
    // the walls after breaking into the holding, "camp" as a strike on the besiegers' camp.
    public string ReliefMode = "";
    // PROTOCOL §43: Bannerlord loads the war elephants built from Burning Empires; elephant regiments ride them.
    public bool Elephants;
    // PROTOCOL §44: Bannerlord loads War Sails and CK3Bridge.Naval; landings and battles at sea are fought on ships.
    public bool Naval;
}

/// <summary>The battle clock (PROTOCOL §14): CK3's own combat inflicts the Bannerlord result.</summary>
public sealed class BattleClockOptions
{
    public bool Enabled = true;                       // false = always payload version 3 (the result is applied at once)
    public int PaceDays = 12;                         // combat day the steering ends by, 6..13 (keypad r198); the mod default
    public bool ReinforcementClash = true;            // offer a second Bannerlord clash when the Bannerlord loser is reinforced
    public int MaxPayloadVersion = 4;                 // the delivery's ICk3Delivery.MaxPayloadVersion (3 after an old mod refused v4)
}

/// <summary>What CK3 must apply for a battle (PROTOCOL §2.1). Channel-independent.</summary>
public sealed class Ck3Outcome
{
    public int Bid;
    public long ProvinceId;
    public int OutcomeCode;                           // 1 attacker wins, 2 defender wins, 3 abort, 4 cancel
    public List<ArmyLoss> Armies = new();
    public List<CharacterFate> Characters = new();
    public int MyLost, EnemyLost, MyKills, PrestigeBonus;   // r4..r6 cumulative over the clashes in payload 4; r7 per clash
    public string Summary = "";                       // human-readable, for the overlay/title card ("Victory at X ...")
    public bool PlayerWon;
    public List<CharacterAward> Awards = new();       // at most 8 reach CK3 (PROTOCOL §13.5); order = priority

    // Battle clock (PROTOCOL §14.2). The defaults are payload version 3: the result is applied at once, as before.
    public int PayloadVersion = 3;                    // 3 = apply at once; 4 = steer CK3's combat. A keypad re-send to an old mod lowers 4 to 3
    public int AttackerKillTarget;                    // r8: enemy dead CK3 must count for the attacker side, cumulative over clashes
    public int DefenderKillTarget;                    // r9: the same for the defender side
    public int PaceDays;                              // r198: combat day the steering ends by, 6..13; 0 = the mod's default (12)
    public int ParentBid;                             // the steering bid this clash continues; 0 = first clash
    public int Clash = 1;                             // 1 = first clash, 2 = first reinforcement clash, ...
    public List<GroupLoss> GroupLosses = new();       // CK3 dead per Bannerlord troop group in this clash (caps a later clash)

    // Single combat (PROTOCOL §18): payload 3 with r2 = the winner and r8 = 1 when the loser was killed in Bannerlord.
    public bool IsDuel;
    public bool DuelLoserKilled;

    // Battle feats (PROTOCOL §32): what the CK3 player did in person in this clash (null = he did not fight it), and the feats
    // the feat ledger grants for it, sent in r7 as PrestigeBonus + FeatBase × FeatMask.
    public BattleFeats? Feats;
    public int FeatMask;
}

/// <summary>The CK3 player's own deeds in one decided field clash (PROTOCOL §32), the input of the feat ledger (FeatLedger).</summary>
public sealed class BattleFeats
{
    public long Ck3Id;                                // the player character: feats are counted per character
    public int Kills;                                 // enemies he struck down himself in this clash
    public bool Won;
    public bool Defending;                            // his side defends the CK3 combat
    public double EnemyToOwn;                         // CK3 enemy strength / own strength at the start
    public string Terrain = "";                       // rough | forest | desert | "" (other)
    public bool Winter;
    public bool SlewCommander;                        // he downed a commander of the enemy side himself
    public bool Wounded;                              // the battle wounded him (he fell, or ended under 10 % health)
    public int HeroesDowned;                          // enemy heroes (knights, lords, commanders) he downed himself
    public bool AtSea;                                // a battle at sea (PROTOCOL §44)
    public bool Landing;                              // a landing: the attackers came straight off the sea
}

/// <summary>CK3 dead of one Bannerlord troop group in one clash (PROTOCOL §14.6).</summary>
public sealed class GroupLoss
{
    public SideKey Side;
    public string GroupKey = "";                      // TroopRequest.Group verbatim ("levy#12", "maa:bowmen#12#16706", ...)
    public int Dead;
}

public sealed class ArmyLoss
{
    public int ArmyTag;
    public int LevyLossPermille;                      // 0..1000 of the army's levies lost (payload 3)
    public int LevySurvivors;                         // levies left, absolute men (payload 4: r11 + 14i), less what the fix restores
                                                      // to the indexed MaA types without a pair; unindexed types keep CK3's losses
    public List<MaaLoss> Maa = new();                 // at most 6 per army (largest first); others keep levy-like proportional loss
}

public sealed class MaaLoss
{
    public string MaaKey = "";
    public int MaaIndex;                              // 1-based index in bridge/data/ck3_maa_index.json
    public int SurvivorSharePermille;                 // survivors as share of the regiments' MAX men, 0..1000
}

public sealed class CharacterFate
{
    public int Tag;
    public long Ck3Id;
    public int Fate;                                  // 0 unharmed, 1 wounded, 2 maimed, 3 captured, 4 killed, 5 severely wounded (§32)
    public int KillerTag;                             // 0 = none
    public int Kills;
}

/// <summary>What one hero earned in Bannerlord (PROTOCOL §13.5 award slot).</summary>
public sealed class CharacterAward
{
    public int Tag;                                   // ck3xbl_tag
    public long Ck3Id;
    public int Prestige;                              // 0..1000, multiple of 25
    public int Glory;                                 // 0..100, multiple of 5 (applied only to acclaimed knights)
    public int ProwessRolls;                          // 0..3 calls of knight_increase_prowess_chance_effect
    public string Name = "";                          // for the summary/journal
    public bool PlayerSide;
}

/// <summary>CK3xBL.Appearance: reads CK3 saves, builds hero appearances and banners.</summary>
public interface IAppearanceProvider
{
    /// Starts watching CK3 saves (continue_game.json / save games) and keeps an appearance cache keyed by internal id.
    void Start(string ck3DocumentsDir, string ck3GameDir, string cacheDir);
    void Stop();
    /// Fills HeroSpec.Appearance/Equipment/Name and SideRequest.Banner + Presentation CoA PNGs. Never throws; falls back to stock.
    void Enrich(BattleSnapshot battle, BattleRequest request);
}

/// <summary>CK3xBL.Ux: everything the player sees between the two games.</summary>
public interface ITransitionUx : IDisposable
{
    void Initialize(UxOptions options);
    /// Freeze-frame CK3, darken to the title card (title, subtitle, CoA images).
    Task ShowTitleCardAsync(TitleCard card, CancellationToken ct);
    /// Fade the overlay away (after Bannerlord reports "visible", or after CK3 is back).
    Task HideAsync(CancellationToken ct);
    /// Black result card while switching back to CK3.
    Task ShowResultCardAsync(TitleCard card, CancellationToken ct);
    /// Brings a window to the foreground using the activation ladder. Returns true when it is the foreground window.
    bool BringToFront(IntPtr hwnd);
    /// Per-process audio: ramp a process's session volume (0..1) over the given duration, remembering the user's level.
    Task RampProcessVolumeAsync(int pid, float target, TimeSpan duration, CancellationToken ct);
    Task RestoreProcessVolumeAsync(int pid, TimeSpan duration, CancellationToken ct);

    /// Keeps the title card fully up for <paramref name="hold"/>, then dissolves it over <paramref name="dissolve"/> into the
    /// battle Bannerlord reveals (PROTOCOL §13.6). The default holds, then hides as HideAsync does.
    async Task RevealAsync(TimeSpan hold, TimeSpan dissolve, CancellationToken ct)
    {
        await Task.Delay(hold, ct).ConfigureAwait(false);
        await HideAsync(ct).ConfigureAwait(false);
    }

    /// <summary>False when the last ShowTitleCardAsync did not put the card on screen (declined or failed); the orchestrator then
    /// asks the module for its own title card (PROTOCOL §13.6 <c>title_in_engine</c>).</summary>
    bool TitleCardPresented => true;
}

public sealed class UxOptions
{
    public string FontFamily = "Fondamento";          // falls back to a serif font
    public string? CK3GameDir;
}

public sealed class TitleCard
{
    public string Title = "";
    public string Subtitle = "";
    public string Detail = "";
    public string? LeftCoaPng;
    public string? RightCoaPng;
    public IntPtr FreezeFrameFrom;                    // window to freeze-frame first (CK3), or zero
}

/// <summary>How one delivery attempt ended.</summary>
public enum DeliveryStatus
{
    /// <summary>CK3 acknowledged the outcome.</summary>
    Delivered,
    /// <summary>Stopped before anything could be applied: CK3 lost the foreground, or the player held a key or mouse button. Sending
    /// the outcome again later is safe (the keypad sequence starts with a reset; the console was never opened).</summary>
    Interrupted,
    /// <summary>Not acknowledged, rejected, or CK3 is not running; not retried automatically.</summary>
    Failed,
    /// <summary>The keypad did not answer its probe: the battle window (event 0002) is not open or something covers it. Only the probe's
    /// two chords were sent; the payload was not typed into CK3's main interface. Safe to send again once the window is back.</summary>
    KeypadClosed,
}

/// <summary>Sends the outcome into CK3 (PROTOCOL §2.2 keypad or §2.3 run file).</summary>
public interface ICk3Delivery
{
    Task<DeliveryStatus> DeliverAsync(Ck3Outcome outcome, CancellationToken ct);

    /// <summary>Highest keypad payload version this channel sends for the rest of the bridge session (PROTOCOL §14.3): 4, or 3
    /// once an old mod has refused a version 4 payload.</summary>
    int MaxPayloadVersion => 4;
}
