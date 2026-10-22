using System;
using System.Collections.Generic;
using System.Linq;
using CK3xBL.Protocol;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace CK3Bridge
{
    /// <summary>Raised for a request that cannot be fought; the message goes to result.json "error".</summary>
    internal sealed class RequestException : Exception
    {
        public RequestException(string message) : base(message)
        {
        }
    }

    /// <summary>One side of the battle as built for the opener.</summary>
    internal sealed class BuiltSide
    {
        public BattleSide Side;
        public BattleSideEnum EngineSide;
        public SideRequest Request;
        public BasicCultureObject Culture;
        public CustomBattleCombatant Combatant;
        public BasicCharacterObject General;
        public readonly List<CreatedHero> Heroes = new List<CreatedHero>();
    }

    /// <summary>Everything a battle needs from the request, plus what must be released afterwards.</summary>
    internal sealed class BattleArmies
    {
        public BuiltSide Attacker;
        public BuiltSide Defender;
        public bool PlayerIsAttacker;
        public BasicCharacterObject PlayerCharacter;
        public bool PlayerIsGeneral;
        public readonly Dictionary<BasicCharacterObject, CreatedHero> HeroByCharacter = new Dictionary<BasicCharacterObject, CreatedHero>();
        /// <summary>Troops created for this battle on another mount (PROTOCOL.md §43), by troop, mount and harness.</summary>
        public readonly Dictionary<string, BasicCharacterObject> MountedTroops = new Dictionary<string, BasicCharacterObject>(StringComparer.Ordinal);
        public readonly GroupLedger<(BattleSideEnum, BasicCharacterObject)> Ledger = new GroupLedger<(BattleSideEnum, BasicCharacterObject)>();
        public BattleAccounting Accounting;

        public BuiltSide PlayerSide => PlayerIsAttacker ? Attacker : Defender;
        public BuiltSide EnemySide => PlayerIsAttacker ? Defender : Attacker;

        /// <summary>Unregisters every hero created for this battle. Only call once no mission references them.</summary>
        public void Release()
        {
            foreach (CreatedHero hero in HeroByCharacter.Values)
            {
                try
                {
                    HeroFactory.Release(hero.Character);
                }
                catch (Exception e)
                {
                    BridgeIo.Log($"releasing hero {hero.Spec.Key} failed: {e.Message}");
                }
            }
            HeroByCharacter.Clear();
            foreach (BasicCharacterObject troop in MountedTroops.Values)
            {
                try { HeroFactory.Release(troop); }
                catch (Exception e) { BridgeIo.Log($"releasing mounted troop {troop?.StringId} failed: {e.Message}"); }
            }
            MountedTroops.Clear();
        }
    }

    /// <summary>
    /// Turns the two SideRequests into CustomBattleCombatants: heroes, general, troops, culture, banner and team
    /// colours, with every id validated (unknown ids are skipped with a log line, never passed to the engine).
    /// </summary>
    internal static class ArmyBuilder
    {
        private const string FallbackCulture = "vlandia";

        /// <summary>Mounted culture commanders of the custom battle roster.</summary>
        private static readonly Dictionary<string, string> StockCommanderByCulture = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "empire", "commander_1" }, { "vlandia", "commander_20" }, { "sturgia", "commander_5" },
            { "battania", "commander_4" }, { "aserai", "commander_6" }, { "khuzait", "commander_3" },
        };

        public static BattleArmies Build(BattleRequest request)
        {
            if (request.Attacker == null || request.Defender == null) throw new RequestException("request needs both attacker and defender");
            var armies = new BattleArmies
            {
                PlayerIsAttacker = RequestRules.IsPlayerAttacker(request.PlayerSide),
                Accounting = new BattleAccounting(request.Id),
            };
            try
            {
                var names = new UniqueNames();
                armies.Attacker = BuildSide(request, request.Attacker, BattleSide.Attacker, armies, names);
                armies.Defender = BuildSide(request, request.Defender, BattleSide.Defender, armies, names);
                ResolvePlayer(request, armies);
                BannerFactory.ResolveClash(armies.PlayerSide.Combatant.Banner, armies.EnemySide.Combatant.Banner);
                return armies;
            }
            catch
            {
                armies.Release();
                throw;
            }
        }

        private static BuiltSide BuildSide(BattleRequest request, SideRequest sideRequest, BattleSide side, BattleArmies armies, UniqueNames names)
        {
            MBObjectManager om = MBObjectManager.Instance;
            string label = side == BattleSide.Attacker ? "attacker" : "defender";
            var built = new BuiltSide
            {
                Side = side,
                EngineSide = side == BattleSide.Attacker ? BattleSideEnum.Attacker : BattleSideEnum.Defender,
                Request = sideRequest,
                Culture = om.GetObject<BasicCultureObject>(sideRequest.Culture ?? ""),
            };
            if (built.Culture == null)
            {
                BridgeIo.Log($"{label}: unknown culture '{sideRequest.Culture}', using {FallbackCulture}");
                built.Culture = om.GetObject<BasicCultureObject>(FallbackCulture);
            }
            string cultureId = built.Culture.StringId;

            // Folded to the game font first: a letter Galahad lacks (e.g. ǫ) would draw as nothing in nameplates and cards.
            string sideName = Naming.SanitizeDisplayName(GameFonts.Fold(sideRequest.Name), built.Culture.Name.ToString());
            Banner banner = BannerFactory.ForSide(sideRequest, built.Culture, label);
            // Side must be set explicitly: CustomBattleCombatant.Side defaults to Defender (0).
            built.Combatant = new CustomBattleCombatant(new TextObject("{=!}" + sideName), built.Culture, banner) { Side = built.EngineSide };

            CreateHeroes(request, sideRequest, built, armies, names, cultureId);
            built.General = ResolveGeneral(sideRequest, built, cultureId, label);

            foreach (CreatedHero hero in built.Heroes) built.Combatant.AddCharacter(hero.Character, 1);
            if (!armies.HeroByCharacter.ContainsKey(built.General))
            {
                // A stock character as general (protocol 1 behaviour): it must also be a combatant character, because side
                // Tactics is the max over the combatant's characters (CustomBattleCombatant.GetTacticsSkillAmount).
                built.Combatant.AddCharacter(built.General, 1);
                armies.Ledger.Add((built.EngineSide, built.General), null, 1);
                armies.Accounting.ExpectUngrouped(side);
            }
            built.Combatant.SetGeneral(built.General);

            AddTroops(request.Id, sideRequest, built, armies, label);
            return built;
        }

        private static void CreateHeroes(BattleRequest request, SideRequest sideRequest, BuiltSide built, BattleArmies armies, UniqueNames names, string cultureId)
        {
            if (sideRequest.Heroes == null) return;
            var seenKeys = new HashSet<string>(armies.HeroByCharacter.Values.Select(h => h.Spec.Key), StringComparer.Ordinal);
            List<HeroSpec> specs = sideRequest.Heroes.Where(h => h != null).ToList();

            // The enemy general is the strongest hero by Level (GeneralsAndCaptainsAssignmentLogic),
            // so the requested general gets a level above every other hero of the side.
            int maxOtherLevel = specs.Where(h => h.Key != sideRequest.General).Select(h => h.Level).DefaultIfEmpty(0).Max();

            foreach (HeroSpec spec in specs)
            {
                if (string.IsNullOrEmpty(spec.Key) || !seenKeys.Add(spec.Key))
                {
                    BridgeIo.Log($"hero without a unique key skipped ({spec.Name})");
                    continue;
                }
                string name = names.Claim(Naming.SanitizeDisplayName(GameFonts.Fold(spec.Name), "Knight " + spec.Key));
                int level = spec.Key == sideRequest.General ? Math.Max(spec.Level, maxOtherLevel + 1) : spec.Level;
                CreatedHero hero = HeroFactory.Create(request.Id, spec, built.Side, cultureId, name, level);
                hero.Banner = BannerFactory.ForHero(spec);
                built.Heroes.Add(hero);
                armies.HeroByCharacter[hero.Character] = hero;
                armies.Accounting.ExpectHero(built.Side, spec);
            }
        }

        private static BasicCharacterObject ResolveGeneral(SideRequest sideRequest, BuiltSide built, string cultureId, string label)
        {
            string wanted = sideRequest.General;
            if (!string.IsNullOrEmpty(wanted))
            {
                CreatedHero byKey = built.Heroes.FirstOrDefault(h => h.Spec.Key == wanted);
                if (byKey != null) return byKey.Character;
                BasicCharacterObject stock = MBObjectManager.Instance.GetObject<BasicCharacterObject>(wanted);
                if (stock != null) return stock;
                BridgeIo.Log($"{label}: general '{wanted}' is neither a hero key nor a troop id");
            }
            CreatedHero commander = built.Heroes.FirstOrDefault(h => h.Spec.Role == "commander")
                                    ?? built.Heroes.FirstOrDefault(h => h.Spec.Role == "primary")
                                    ?? built.Heroes.FirstOrDefault();
            if (commander != null) return commander.Character;

            if (!StockCommanderByCulture.TryGetValue(cultureId, out string fallbackId)) fallbackId = "commander_1";
            BasicCharacterObject fallback = MBObjectManager.Instance.GetObject<BasicCharacterObject>(fallbackId)
                                            ?? MBObjectManager.Instance.GetObject<BasicCharacterObject>("commander_1");
            if (fallback == null) throw new RequestException($"{label}: no general available");
            BridgeIo.Log($"{label}: no general given, using stock {fallback.StringId}");
            return fallback;
        }

        private static void AddTroops(string battleId, SideRequest sideRequest, BuiltSide built, BattleArmies armies, string label)
        {
            if (sideRequest.Troops == null) return;
            foreach (TroopRequest troop in sideRequest.Troops)
            {
                if (troop == null || troop.Count <= 0) continue;
                string group = RequestRules.GroupKey(troop);
                // Unknown ids are still requested: their men never spawn and count as survivors (PROTOCOL.md §3.5).
                armies.Accounting.ExpectTroops(built.Side, group ?? "unknown", troop.Count);
                BasicCharacterObject character = string.IsNullOrEmpty(troop.Id) ? null : MBObjectManager.Instance.GetObject<BasicCharacterObject>(troop.Id);
                if (character == null)
                {
                    BridgeIo.Log($"{label}: unknown troop id '{troop.Id}' skipped ({troop.Count} men of {group})");
                    continue;
                }
                int count = troop.Count;
                if (character.IsHero && count > 1)
                {
                    BridgeIo.Log($"{label}: troop '{troop.Id}' is a unique hero; spawning 1 instead of {count}");
                    count = 1;
                }
                if (!string.IsNullOrEmpty(troop.Mount)) character = Mounted(battleId, armies, character, troop, label);
                built.Combatant.AddCharacter(character, count);
                armies.Ledger.Add((built.EngineSide, character), group, count, SpawnOrder.IsLevy(group) ? 1 : 0);
            }
        }

        /// <summary>PROTOCOL.md §43: the troop on the group's mount (one character per troop, mount and harness); the troop itself when
        /// the mount is not loaded.</summary>
        private static BasicCharacterObject Mounted(string battleId, BattleArmies armies, BasicCharacterObject troop, TroopRequest request, string label)
        {
            string key = troop.StringId + "|" + request.Mount + "|" + (request.MountHarness ?? "");
            if (armies.MountedTroops.TryGetValue(key, out BasicCharacterObject known)) return known;
            BasicCharacterObject mounted = MountedTroopFactory.Create(battleId, armies.MountedTroops.Count + 1, troop, request.Mount, request.MountHarness);
            if (mounted == null)
            {
                BridgeIo.Log($"{label}: mount '{request.Mount}' is not loaded; {troop.StringId} rides its own horse");
                return troop;
            }
            armies.MountedTroops[key] = mounted;
            BridgeIo.Log($"{label}: {troop.StringId} rides {request.Mount}{(request.MountHarness != null ? " in " + request.MountHarness : "")}");
            return mounted;
        }

        /// <summary>
        /// The avatar is the player's own hero when present, else the side's general. Sergeant mode needs a non-null side general (BannerlordMissions.OpenCustomBattleMission dereferences it).
        /// </summary>
        private static void ResolvePlayer(BattleRequest request, BattleArmies armies)
        {
            BuiltSide side = armies.PlayerSide;
            CreatedHero own = side.Heroes.FirstOrDefault(h => h.Spec.IsPlayer) ?? side.Heroes.FirstOrDefault(h => h.Spec.Role == "player");
            armies.PlayerCharacter = own?.Character ?? side.General;

            if (armies.PlayerCharacter == side.General)
            {
                armies.PlayerIsGeneral = true;
                if (!request.PlayerIsGeneral) BridgeIo.Log("playerIsGeneral=false but the player is the side's general; playing as general");
            }
            else if (request.PlayerIsGeneral)
            {
                side.General = armies.PlayerCharacter;
                side.Combatant.SetGeneral(armies.PlayerCharacter);
                armies.PlayerIsGeneral = true;
            }
            else
            {
                armies.PlayerIsGeneral = false;
            }
        }
    }
}
