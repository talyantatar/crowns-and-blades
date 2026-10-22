// config.json: everything a user may want to change (an installed copy keeps it in %LOCALAPPDATA%\ck3xbl, a source checkout in
// bridge/; see BridgePaths.Detect). Missing keys take the defaults below; unknown keys are ignored. The file is written with every
// key present on first start so it documents itself.
using CK3xBL.Core.Abstractions;
using CK3xBL.Core.Delivery;
using CK3xBL.Core.Json;

namespace CK3xBL.Core.Config;

public enum BridgeMode
{
    /// <summary>Achievement-capable: release CK3 launch, keypad result channel, never the console (PROTOCOL §0).</summary>
    Normal,
    /// <summary>CK3 in -debug_mode, run-file + console result channel (keypad as fallback).</summary>
    Dev,
}

public sealed class BridgeConfig
{
    public BridgeMode Mode = BridgeMode.Normal;
    public Ck3Settings Ck3 = new();
    public BannerlordSettings Bannerlord = new();
    public BattleSettings Battle = new();
    public DeliverySettings Delivery = new();
    public UxSettings Ux = new();
    public BattleClockSettings BattleClock = new();

    public MapperOptions ToMapperOptions(string dataDirectory) => new()
    {
        MaxAgentsTotal = Battle.MaxAgentsTotal,
        MinAgentsPerSide = Battle.MinAgentsPerSide,
        BattleSize = Battle.BattleSize,
        PlayerDeathIsReal = Battle.PlayerDeathIsReal,
        KilledToCk3DeadShareWinner = Battle.KilledToCk3DeadShareWinner,
        KilledToCk3DeadShareLoser = Battle.KilledToCk3DeadShareLoser,
        RoutedToCk3DeadShareLoser = Battle.RoutedToCk3DeadShareLoser,
        ReturnCountdownSeconds = Battle.ReturnCountdownSeconds,
        Deployment = Battle.Deployment,
        DataDirectory = dataDirectory,
        StrengthBalance = Battle.StrengthBalance,
        StrengthTiersPerDoubling = Math.Clamp(Battle.StrengthTiersPerDoubling, 1, 10),
        StrengthMaxFactor = Math.Clamp(Battle.StrengthMaxFactor, 1, 4),
        Costumes = Battle.Costumes,
        Language = CK3xBL.Core.Ck3.Ck3Language.Resolve(Battle.Language, Ck3.UserDir),
        BattleClock = new BattleClockOptions
        {
            Enabled = BattleClock.Enabled,
            PaceDays = Math.Clamp(BattleClock.PaceDays, KeypadLayout.MinPaceDays, KeypadLayout.MaxPaceDays),
            ReinforcementClash = BattleClock.ReinforcementClash,
        },
    };
}

public sealed class Ck3Settings
{
    /// <summary>Install folder (contains binaries/ck3.exe). Empty = auto-detect (Steam, then other stores).</summary>
    public string GameDir = "";
    /// <summary>User folder (contains logs/, run/, save games/). Empty = Documents\Paradox Interactive\Crusader Kings III.</summary>
    public string UserDir = "";
    /// <summary>Add -continuelastsave when the tray launches CK3.</summary>
    public bool ContinueLastSave;
    /// <summary>The OUT channel file in logs/: game.log (info_log, PROTOCOL §1) or debug.log.</summary>
    public string LogFile = "game.log";
    public int LogPollMilliseconds = 75;
}

public sealed class BannerlordSettings
{
    /// <summary>Install folder (contains bin/Win64_Shipping_Client/Bannerlord.exe). Empty = auto-detect (Steam, then other stores).</summary>
    public string GameDir = "";
    public List<string> Modules = new() { "Native", "SandBoxCore", "CustomBattle", "CK3Bridge" };
    /// <summary>Used once if the first module list fails to start (PROTOCOL §3.1).</summary>
    public List<string> FallbackModules = new() { "Native", "SandBoxCore", "CustomBattle", "SandBox", "StoryMode", "CK3Bridge" };
    /// <summary>Load Open Source Armory and Saddlery (with the SandBox and StoryMode they need) when they are in Modules (PROTOCOL §21).</summary>
    public bool ArmoryModules = true;
    /// <summary>Load War Sails and CK3Bridge.Naval for battles at sea and landings when NavalDLC is installed (PROTOCOL §44).</summary>
    public bool NavalModules = true;
    /// <summary>Load SandBox, home of the arenas where noble single combats are fought (PROTOCOL §27).</summary>
    public bool ArenaModule = true;
    /// <summary>Give the RAM of a Bannerlord waiting hidden for the next battle back to CK3 (PROTOCOL §28).</summary>
    public bool TrimWhenIdle = true;
    /// <summary>Restart the idle Bannerlord when the memory it keeps from earlier battles grows above this many MB; 0 = never (PROTOCOL §31).</summary>
    public long RecycleAboveMb = 8000;
    /// <summary>Start Bannerlord (hidden, idle) when the bridge starts, so the first battle loads warm.</summary>
    public bool PreloadOnStart = true;
    /// <summary>Relaunch Bannerlord in the background after it exits or crashes.</summary>
    public bool KeepRunning = true;
    public double HeartbeatTimeoutSeconds = 5;
    /// <summary>Grace for states in which the module may legitimately stall its heartbeat (booting, loading).</summary>
    public double LoadingHeartbeatTimeoutSeconds = 60;
    public double BootTimeoutSeconds = 180;
    /// <summary>Hard kill only when the window has been hung this long (never otherwise).</summary>
    public double HungKillSeconds = 30;
}

public sealed class BattleSettings
{
    public int MaxAgentsTotal = 1000;
    public int MinAgentsPerSide = 40;
    /// <summary>Men on the field at once. 600 by default: 800 and 1000 crashed Bannerlord (with the armory modules) at the order of
    /// battle in three of three big battles on 2026-10-18, 300 and 600 never did. Up to 1000 by hand.</summary>
    public int BattleSize = 600;
    public bool PlayerDeathIsReal;
    public double KilledToCk3DeadShareWinner = 0.45;
    public double KilledToCk3DeadShareLoser = 0.65;
    public double RoutedToCk3DeadShareLoser = 0.25;
    /// <summary>Seconds on the Bannerlord scoreboard before returning to CK3; -1 = wait for the player.</summary>
    public int ReturnCountdownSeconds = 10;
    /// <summary>order_of_battle | auto</summary>
    public string Deployment = "order_of_battle";
    /// <summary>Drop the hidden preload when the player has not answered the CK3 event for this long (it is reloaded on demand).</summary>
    public double OfferTimeoutMinutes = 10;
    /// <summary>Give up switching to Bannerlord when the battle is not visible this long after the player's click.</summary>
    public double EnterTimeoutSeconds = 120;
    /// <summary>Even the sides out so a straight Bannerlord clash ends as CK3 would decide it (PROTOCOL §17).</summary>
    public bool StrengthBalance = true;
    /// <summary>Bannerlord troop tiers per doubling of an agent's worth (1..10); higher = elite troops count for less.</summary>
    public double StrengthTiersPerDoubling = 3;
    /// <summary>Largest strength factor a side gets (1..4; 4 = damage and health x2 each).</summary>
    public double StrengthMaxFactor = 4;
    /// <summary>Troops wear the armour of their CK3 culture, era and role, at the stock protection (PROTOCOL §21).</summary>
    public bool Costumes = true;
    /// <summary>Free CK3's RAM for Bannerlord once a battle has started (EmptyWorkingSet; PROTOCOL §24).</summary>
    public bool TrimCk3Memory = true;
    /// <summary>Fewer men on the field at once when the free memory (RAM plus page file) would not hold battleSize (PROTOCOL §31).</summary>
    public bool MemoryGuard = true;
    /// <summary>Memory (MB) left free for Windows and other programs by the memory guard.</summary>
    public int MemoryReserveMb = 2000;
    /// <summary>Your own deeds in Bannerlord battles earn CK3 traits and modifiers (PROTOCOL §32).</summary>
    public bool Feats = true;
    /// <summary>Knights and commanders carry their own CK3 arms (PROTOCOL §25).</summary>
    public bool HeroArms = true;
    /// <summary>Language of the battle cards and Bannerlord texts: auto (CK3's language), en or ru (PROTOCOL §26).</summary>
    public string Language = "auto";
}

public sealed class DeliverySettings
{
    /// <summary>auto (normal mode: keypad; dev mode: run file, keypad fallback) | keypad | runfile</summary>
    public string Channel = "auto";
    /// <summary>Delay between injected key events (ms).</summary>
    public int KeyDelayMs = 12;
    /// <summary>Keys per chunk; the CK3 foreground check runs before every chunk and every key.</summary>
    public int ChunkSize = 12;
    /// <summary>Hold Ctrl+Shift+Alt for a whole chunk (fast) instead of pressing them for every key (slow, the verified pattern).</summary>
    public bool HoldModifiersPerChunk = true;
    public double AckTimeoutSeconds = 4;
    /// <summary>Swallow physical keyboard/mouse clicks while keys are injected (short windows, under the opaque overlay).</summary>
    public bool BlockPhysicalInput = true;
    /// <summary>Physical input must have been idle this long before injection starts.</summary>
    public int QuietInputMs = 300;
    /// <summary>Most keys the award slots (prestige, glory, prowess of heroes, PROTOCOL §13.5) may add to a keypad payload; awards
    /// beyond it are dropped from the last one. 0 = no awards over the keypad. The run file always carries every award.</summary>
    public int KeypadAwardKeyBudget = 90;
}

public sealed class UxSettings
{
    public string FontFamily = "Fondamento";
    /// <summary>Show tray balloons for battle events and problems.</summary>
    public bool Notifications = true;
    /// <summary>CK3 in a window instead of fullscreen: notice (one tray notice per CK3 start) | borderless (stage it borderless on
    /// its monitor, restored on exit) | ignore.</summary>
    public string Ck3Windowed = "notice";
}

/// <summary>The battle clock (PROTOCOL §14): after the Bannerlord fight, CK3's own combat inflicts the result over a few days, so war
/// score and the battle summary are CK3's.</summary>
public sealed class BattleClockSettings
{
    /// <summary>false = every result is applied at once, as before the battle clock (keypad payload version 3).</summary>
    public bool Enabled = true;
    /// <summary>The combat day by which CK3 has fought out the Bannerlord result, 6..13 (CK3 allows retreats from day 14).</summary>
    public int PaceDays = 12;
    /// <summary>Offer a second Bannerlord battle when the Bannerlord loser is reinforced during the steered days. false = the offer
    /// is not prepared: in CK3 choose to let the battle run its course, and the first clash's result is inflicted.</summary>
    public bool ReinforcementClash = true;
}

public static class BridgeConfigStore
{
    /// <summary>Loads the config. When the file is missing, writes a complete one from <paramref name="createDefaults"/> (which fills
    /// in auto-detected paths). A malformed file is never overwritten (the user's edits are kept); defaults are used instead and
    /// <paramref name="problem"/> says why.</summary>
    /// <summary>Reads the config without writing anything: null when the file is missing or invalid (<paramref name="problem"/> says
    /// which for an invalid one).</summary>
    public static BridgeConfig? TryLoad(string path, out string? problem)
    {
        problem = null;
        if (!File.Exists(path)) return null;
        try { return ProtocolJson.Deserialize<BridgeConfig>(File.ReadAllText(path)); }
        catch (Exception e)
        {
            problem = $"config.json is invalid ({e.Message})";
            return null;
        }
    }

    public static BridgeConfig LoadOrCreate(string path, Func<BridgeConfig> createDefaults, out string? problem)
    {
        problem = null;
        if (!File.Exists(path))
        {
            var fresh = createDefaults();
            try { ProtocolJson.WriteAtomic(path, fresh, indented: true); }
            catch (Exception e) { problem = $"could not write default config: {e.Message}"; }
            return fresh;
        }
        try
        {
            return ProtocolJson.Deserialize<BridgeConfig>(File.ReadAllText(path)) ?? createDefaults();
        }
        catch (Exception e)
        {
            problem = $"config.json is invalid ({e.Message}); using defaults";
            return createDefaults();
        }
    }
}
