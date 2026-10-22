using System;
using System.Collections.Generic;
using System.Linq;
using CK3xBL.Protocol;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace CK3Bridge
{
    /// <summary>
    /// The CK3 buffs of the battle being fought (PROTOCOL.md §13.4): side buffs by team side, hero
    /// entries by hero character, group buffs by (side, group key) and, once the ledger has assigned an agent, by agent. Filled
    /// by <see cref="BattleSession.Start"/> after the armies are built and before the mission opens; emptied when the session
    /// ends, fails to open, or the game ends. Main thread only (the models below run on the main thread).
    ///
    /// Spawn order (Mission.SpawnAgent, v1.4.8): stats and initial morale are computed inside
    /// Agent.Build, before any OnAgentBuild, so the models look heroes up by character and sides by team only; group buffs
    /// are read at hit time, or applied by <see cref="BattleTracker"/> when it tags the agent.
    /// Every value is clamped once here (<see cref="BuffMath"/>), so nothing a request carries can throw or explode later.
    /// </summary>
    internal static class CK3BuffRegistry
    {
        /// <summary>What a hero carries besides his side's and group's buffs.</summary>
        internal sealed class HeroEntry
        {
            public string Key;
            public float DamageMultiplier = 1f;
            public HeroBuffs Buffs;

            /// <summary>The parsed heroes[].captainOf; null = stock captain assignment.</summary>
            public FormationClass? CaptainOf;
        }

        private static readonly Dictionary<BattleSideEnum, SideBuffs> Sides = new Dictionary<BattleSideEnum, SideBuffs>();
        private static readonly Dictionary<BasicCharacterObject, HeroEntry> Heroes = new Dictionary<BasicCharacterObject, HeroEntry>();
        private static readonly Dictionary<(BattleSideEnum, string), GroupBuffs> GroupsByKey = new Dictionary<(BattleSideEnum, string), GroupBuffs>();
        private static readonly Dictionary<Agent, GroupBuffs> GroupsByAgent = new Dictionary<Agent, GroupBuffs>();
        private static readonly HashSet<string> ReportedFailures = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>An acclaimed knight's aura (PROTOCOL.md §42): his side and formation, and whether he has left the field.</summary>
        private sealed class AuraEntry
        {
            public string Key;
            public BattleSideEnum Side;
            public FormationClass Formation;
            public AuraSpec Aura;
            public bool Fallen;
        }

        private static readonly Dictionary<BasicCharacterObject, AuraEntry> AurasByKnight = new Dictionary<BasicCharacterObject, AuraEntry>();
        private static readonly Dictionary<(BattleSideEnum, FormationClass), AuraSpec> LiveAuras = new Dictionary<(BattleSideEnum, FormationClass), AuraSpec>();

        public static bool Active { get; private set; }

        public static void Begin(BattleRequest request, BattleArmies armies)
        {
            End();
            int heroesBuffed = 0;
            var captains = new List<string>();
            foreach (BuiltSide side in new[] { armies.Attacker, armies.Defender })
            {
                string label = side.Side == BattleSide.Attacker ? "attacker" : "defender";
                SideBuffs sideBuffs = BuffMath.Sanitize(side.Request.Buffs);
                if (sideBuffs != null) Sides[side.EngineSide] = sideBuffs;
                AddGroups(side, label);
                foreach (CreatedHero hero in side.Heroes)
                {
                    HeroEntry entry = HeroEntryFor(hero.Spec, label);
                    Heroes[hero.Character] = entry;
                    if (entry.DamageMultiplier != 1f || entry.Buffs != null) heroesBuffed++;
                    if (entry.CaptainOf.HasValue) captains.Add($"{entry.Key} -> {entry.CaptainOf.Value}");
                    AuraSpec aura = BuffMath.Sanitize(hero.Spec.Aura);
                    if (aura != null && BuffMath.TryParseCaptainOf(aura.Formation, out CaptainFormation formation))
                        AurasByKnight[hero.Character] = new AuraEntry { Key = hero.Spec.Key, Side = side.EngineSide, Formation = (FormationClass)(int)formation, Aura = aura };
                }
            }
            RefreshAuras();
            Active = true;
            string sides = Sides.Count == 0 ? "none" : string.Join(" + ", Sides.Keys.Select(s => s == BattleSideEnum.Attacker ? "attacker" : "defender"));
            string auras = AurasByKnight.Count == 0 ? "none" : string.Join(", ", AurasByKnight.Values.Select(a =>
                $"{a.Key} -> {a.Formation} (melee x{a.Aura.MeleeDamageMult:0.###}, ranged x{a.Aura.RangedDamageMult:0.###}, charge x{a.Aura.ChargeDamageMult:0.###}, " +
                $"taken x{a.Aura.DamageTakenMult:0.###}, aim x{a.Aura.RangedInaccuracyMult:0.###}, morale +{a.Aura.InitialMoraleAdd:0})"));
            BridgeIo.Log($"{request.Id}: combat buffs: side buffs {sides}, {GroupsByKey.Count} group(s), {heroesBuffed} hero(es) buffed, " +
                         "captains: " + (captains.Count == 0 ? "stock" : string.Join(", ", captains)) + "; knights' auras: " + auras);
        }

        private static void RefreshAuras()
        {
            LiveAuras.Clear();
            foreach (var g in AurasByKnight.Values.Where(a => !a.Fallen).GroupBy(a => (a.Side, a.Formation)))
                LiveAuras[g.Key] = BuffMath.Strongest(g.Select(a => a.Aura));
        }

        /// <summary>The base formation a troop fights in: its own formation's class, else the class of its kind (PROTOCOL.md §42).</summary>
        private static FormationClass FormationOf(Agent agent)
        {
            FormationClass c = agent.Formation != null ? agent.Formation.FormationIndex : agent.Character.DefaultFormationClass;
            return c.FallbackClass();
        }

        /// <summary>The live aura over a troop (never a hero, never a mount); null when no standing knight leads its formation.</summary>
        public static AuraSpec AuraOf(Agent agent)
        {
            if (!Active || LiveAuras.Count == 0 || agent == null || !agent.IsHuman || agent.IsHero || agent.Character == null || agent.Team == null) return null;
            if (Heroes.ContainsKey(agent.Character)) return null;
            return LiveAuras.TryGetValue((agent.Team.Side, FormationOf(agent)), out AuraSpec aura) ? aura : null;
        }

        /// <summary>
        /// An acclaimed knight left the field (killed, knocked out, fled): his aura ends, and the troops of his formation lose heart
        /// (his moraleLoss), as at a captain's fall; their aim goes back to their own.
        /// </summary>
        public static void KnightLeft(Agent agent, Mission mission)
        {
            if (!Active || agent == null || !agent.IsHuman || agent.Character == null || !AurasByKnight.TryGetValue(agent.Character, out AuraEntry entry) || entry.Fallen) return;
            entry.Fallen = true;
            RefreshAuras();
            int shaken = 0;
            foreach (Agent troop in mission.Agents)
            {
                if (!troop.IsActive() || !troop.IsHuman || troop.IsHero || troop.Team == null || troop.Team.Side != entry.Side) continue;
                if (FormationOf(troop) != entry.Formation) continue;
                if (entry.Aura.MoraleLoss > 0f) troop.SetMorale(Math.Max(0f, troop.GetMorale() - entry.Aura.MoraleLoss));
                if (entry.Aura.RangedInaccuracyMult != 1f) troop.UpdateAgentProperties();
                shaken++;
            }
            BridgeIo.Log($"knight {entry.Key} left the field: his aura over the {entry.Formation} ends; {shaken} troop(s) lose {entry.Aura.MoraleLoss:0} morale");
        }

        public static void End()
        {
            Active = false;
            Sides.Clear();
            Heroes.Clear();
            GroupsByKey.Clear();
            GroupsByAgent.Clear();
            ReportedFailures.Clear();
            AurasByKnight.Clear();
            LiveAuras.Clear();
        }

        /// <summary>
        /// The ledger gave <paramref name="agent"/> to <paramref name="group"/> (BattleTracker, OnAgentBuild): hits see the
        /// group's buffs from now on. Returns them so the caller can apply the spawn-time part (health, morale); null = none.
        /// </summary>
        public static GroupBuffs TagAgentGroup(Agent agent, BattleSideEnum side, string group)
        {
            if (!Active || agent == null || group == null || !GroupsByKey.TryGetValue((side, group), out GroupBuffs buffs)) return null;
            GroupsByAgent[agent] = buffs;
            return buffs;
        }

        public static SideBuffs Side(Agent agent)
        {
            if (!Active || agent?.Team == null) return null;
            return Sides.TryGetValue(agent.Team.Side, out SideBuffs buffs) ? buffs : null;
        }

        /// <summary>The hero entry of a human agent built for a CK3 hero; null for everyone else (mounts included).</summary>
        public static HeroEntry Hero(Agent agent)
        {
            if (!Active || agent == null || !agent.IsHuman || agent.Character == null) return null;
            return Heroes.TryGetValue(agent.Character, out HeroEntry entry) ? entry : null;
        }

        public static FormationClass? CaptainOf(Agent agent) => Hero(agent)?.CaptainOf;

        /// <summary>Everything <paramref name="agent"/> carries; an unbuffed (default) value for null or outside a CK3 battle.</summary>
        public static AgentBuffs For(Agent agent)
        {
            if (!Active || agent == null) return default;
            HeroEntry hero = Hero(agent);
            GroupsByAgent.TryGetValue(agent, out GroupBuffs group);
            return new AgentBuffs(Side(agent), hero?.DamageMultiplier ?? 1f, hero?.Buffs, group, hero == null ? AuraOf(agent) : null);
        }

        /// <summary>A model or logic failed inside an engine callback: logged once per place and battle, the stock value is used.</summary>
        public static void Report(string where, Exception e)
        {
            if (ReportedFailures.Add(where)) BridgeIo.Log($"combat buffs: {where} failed, stock value used (logged once): {e}");
        }

        /// <summary>groupBuffs of one side; a key its troops do not carry is logged and ignored (PROTOCOL.md §13.4 item 3).</summary>
        private static void AddGroups(BuiltSide side, string label)
        {
            Dictionary<string, GroupBuffs> groups = BuffMath.GroupBuffsOf(side.Request,
                key => BridgeIo.Log($"{label}: groupBuffs key '{key}' names no requested troop group; ignored"));
            foreach (KeyValuePair<string, GroupBuffs> kv in groups) GroupsByKey[(side.EngineSide, kv.Key)] = kv.Value;
        }

        private static HeroEntry HeroEntryFor(HeroSpec spec, string label)
        {
            var entry = new HeroEntry
            {
                Key = spec.Key,
                DamageMultiplier = BuffMath.HeroDamageMultiplier(spec.DamageMultiplier),
                Buffs = BuffMath.Sanitize(spec.Buffs),
            };
            // Parsed once here, so the captain logic never handles request text inside an engine callback.
            if (BuffMath.TryParseCaptainOf(spec.CaptainOf, out CaptainFormation formation)) entry.CaptainOf = (FormationClass)(int)formation;
            else if (spec.CaptainOf != null) BridgeIo.Log($"{label}: hero {spec.Key}: captainOf '{spec.CaptainOf}' is not Infantry, Ranged, Cavalry or HorseArcher; ignored");
            return entry;
        }
    }

    /// <summary>
    /// The CK3 combat effects on Bannerlord's damage and agent-stat models, applied on top of whatever the base model computed. Shared
    /// by the custom battle models below and, with War Sails, by the naval ones of CK3Bridge.Naval (PROTOCOL.md §44), which must
    /// derive from the naval models instead.
    /// </summary>
    public static class CK3CombatHooks
    {
        /// <summary>Momentum a cleaving swing keeps after cutting through a man (PROTOCOL §33): the next one still takes a real blow.</summary>
        public const float CleaveMomentumKept = 0.75f;

        /// <summary>
        /// Every CK3 damage factor in one place (PROTOCOL.md §13.4 item 4): sides, heroes and groups, on post-armour damage of
        /// every hit (AgentApplyDamageModel.CalculateDamage ends with ApplyGeneralDamageModifiers, which the stock model leaves
        /// alone). Falls, friendly fire and hits on mounts keep the stock damage; a horse charge is credited to the rider.
        /// </summary>
        public static float GeneralDamage(float damage, in AttackInformation attackInformation, in AttackCollisionData collisionData)
        {
            if (!CK3BuffRegistry.Active || collisionData.IsFallDamage || attackInformation.IsFriendlyFire || attackInformation.IsVictimAgentMount)
                return damage;
            try
            {
                Agent attacker = attackInformation.IsAttackerAgentMount ? attackInformation.AttackerAgent?.RiderAgent : attackInformation.AttackerAgent;
                Agent victim = attackInformation.VictimAgent;
                bool missile = collisionData.IsMissile;
                bool charge = collisionData.IsHorseCharge || (!missile && attacker != null && attacker.HasMount);
                return damage * BuffMath.HitMultiplier(CK3BuffRegistry.For(attacker), CK3BuffRegistry.For(victim), missile, charge,
                    victimIsHero: victim != null && victim.IsHero);
            }
            catch (Exception e)
            {
                CK3BuffRegistry.Report(nameof(GeneralDamage), e);
                return damage;
            }
        }

        /// <summary>
        /// PROTOCOL §33: a hero with heroes[].buffs.cleave (CK3 prowess over 30) swings through the man he hits into the next one,
        /// like the "cut through" mods: an unblocked, unparried melee swing that strikes a man is sliced through instead of stopping.
        /// Blocks, parries, thrusts, missiles and hits on the world keep the stock reaction.
        /// </summary>
        public static void CollisionReaction(Agent attacker, Agent defender, in AttackCollisionData collisionData, ref MeleeCollisionReaction colReaction)
        {
            try
            {
                if (Cleaves(attacker, defender, in collisionData)) colReaction = MeleeCollisionReaction.SlicedThrough;
            }
            catch (Exception e)
            {
                CK3BuffRegistry.Report(nameof(CollisionReaction), e);
            }
        }

        public static float RemainingMomentum(float momentum, float originalMomentum, Agent attacker, Agent victim, in AttackCollisionData collisionData)
        {
            try
            {
                if (Cleaves(attacker, victim, in collisionData)) return Math.Max(momentum, originalMomentum * CleaveMomentumKept);
            }
            catch (Exception e)
            {
                CK3BuffRegistry.Report(nameof(RemainingMomentum), e);
            }
            return momentum;
        }

        private static bool Cleaves(Agent attacker, Agent victim, in AttackCollisionData collisionData)
        {
            if (!CK3BuffRegistry.Active || attacker == null || victim == null || collisionData.IsMissile || !collisionData.IsColliderAgent) return false;
            if (collisionData.AttackBlockedWithShield || collisionData.CollisionResult != CombatCollisionResult.StrikeAgent) return false;
            if (collisionData.StrikeType != (int)StrikeType.Swing || collisionData.IsHorseCharge || collisionData.IsFallDamage) return false;
            Agent striker = attacker.IsMount ? attacker.RiderAgent : attacker;
            return CK3BuffRegistry.Hero(striker)?.Buffs?.Cleave == true;
        }

        /// <summary>
        /// Hero buffs that are agent stats: speed, swing speed, and a skilled AI. UpdateAgentStats recomputes from scratch every time
        /// it runs, so the factors are applied after base and never compound. Skill bonuses travel in heroes[].skills.
        /// </summary>
        public static void AgentStats(Agent agent, AgentDrivenProperties agentDrivenProperties)
        {
            try
            {
                HeroBuffs hero = CK3BuffRegistry.Hero(agent)?.Buffs;
                if (hero == null) return;
                agentDrivenProperties.MaxSpeedMultiplier *= hero.SpeedMult;
                agentDrivenProperties.CombatMaxSpeedMultiplier *= hero.SpeedMult;
                agentDrivenProperties.SwingSpeedMultiplier *= hero.SwingSpeedMult;
                agentDrivenProperties.ThrustOrRangedReadySpeedMultiplier *= hero.SwingSpeedMult;
                if (hero.AiLevel > 0f && !agent.IsPlayerControlled) SharpenAi(agentDrivenProperties, hero.AiLevel);
            }
            catch (Exception e)
            {
                CK3BuffRegistry.Report(nameof(AgentStats), e);
            }
        }

        /// <summary>
        /// A skilled AI hero (PROTOCOL.md §28): blocks and parries in time, reads feints and wrong-side blocks, keeps attacking and
        /// chambers, waits less and shoots straighter. Bannerlord's own values (from skills and the combat AI option) are moved towards
        /// their best by <paramref name="level"/>.
        /// </summary>
        private static void SharpenAi(AgentDrivenProperties p, float level)
        {
            p.AIBlockOnDecideAbility = BuffMath.AiRaise(p.AIBlockOnDecideAbility, level);
            p.AIParryOnDecideAbility = BuffMath.AiRaise(p.AIParryOnDecideAbility, level);
            p.AIParryOnAttackAbility = BuffMath.AiRaise(p.AIParryOnAttackAbility, level);
            p.AIParryOnAttackingContinueAbility = BuffMath.AiRaise(p.AIParryOnAttackingContinueAbility, level);
            p.AIDecideOnRealizeEnemyBlockingAttackAbility = BuffMath.AiRaise(p.AIDecideOnRealizeEnemyBlockingAttackAbility, level);
            p.AIRealizeBlockingFromIncorrectSideAbility = BuffMath.AiRaise(p.AIRealizeBlockingFromIncorrectSideAbility, level);
            p.AIAttackOnDecideChance = BuffMath.AiRaise(p.AIAttackOnDecideChance, level);
            p.AIAttackOnParryChance = BuffMath.AiRaise(p.AIAttackOnParryChance, level);
            p.AIDecideOnAttackChance = BuffMath.AiRaise(p.AIDecideOnAttackChance, level);
            p.AiTryChamberAttackOnDecide = BuffMath.AiRaise(p.AiTryChamberAttackOnDecide, level);
            p.AIEstimateStunDurationPrecision = BuffMath.AiRaise(p.AIEstimateStunDurationPrecision, level);
            p.AISetNoAttackTimerAfterBeingHitAbility = BuffMath.AiRaise(p.AISetNoAttackTimerAfterBeingHitAbility, level);
            p.AISetNoAttackTimerAfterBeingParriedAbility = BuffMath.AiRaise(p.AISetNoAttackTimerAfterBeingParriedAbility, level);
            p.AISetNoDefendTimerAfterHittingAbility = BuffMath.AiRaise(p.AISetNoDefendTimerAfterHittingAbility, level);
            p.AISetNoDefendTimerAfterParryingAbility = BuffMath.AiRaise(p.AISetNoDefendTimerAfterParryingAbility, level);
            p.AiRandomizedDefendDirectionChance = BuffMath.AiLower(p.AiRandomizedDefendDirectionChance, level);
            p.AiShooterError = BuffMath.AiLower(p.AiShooterError, level);
            p.AiMovementDelayFactor = BuffMath.AiLower(p.AiMovementDelayFactor, level);
            p.AIHoldingReadyMaxDuration = BuffMath.AiLower(p.AIHoldingReadyMaxDuration, level);
        }

        public static float WeaponInaccuracy(float value, Agent agent, WeaponComponentData weapon)
        {
            if (weapon == null || !weapon.IsRangedWeapon) return value;
            try
            {
                HeroBuffs hero = CK3BuffRegistry.Hero(agent)?.Buffs;
                if (hero != null) return value * hero.RangedInaccuracyMult;
                // PROTOCOL §42: the knight leading the formation steadies its shooters' aim.
                AuraSpec aura = CK3BuffRegistry.AuraOf(agent);
                return aura != null ? value * aura.RangedInaccuracyMult : value;
            }
            catch (Exception e)
            {
                CK3BuffRegistry.Report(nameof(WeaponInaccuracy), e);
                return value;
            }
        }

        /// <summary>Knock-down and knock-back resistance plus the hero's knockdownResistanceAdd.</summary>
        public static float KnockResistance(float value, Agent agent)
        {
            HeroBuffs hero = CK3BuffRegistry.Hero(agent)?.Buffs;
            return hero == null ? value : BuffMath.Resistance(value, hero.KnockdownResistanceAdd);
        }

        /// <summary>Half the knock-down bonus: staying in the saddle is the rider's skill more than his grit.</summary>
        public static float DismountResistance(float value, Agent agent)
        {
            HeroBuffs hero = CK3BuffRegistry.Hero(agent)?.Buffs;
            return hero == null ? value : BuffMath.Resistance(value, 0.5f * hero.KnockdownResistanceAdd);
        }
    }

    /// <summary>The CK3 combat effects on the custom battle damage model (<see cref="CK3CombatHooks"/>).</summary>
    public sealed class CK3AgentApplyDamageModel : CustomAgentApplyDamageModel
    {
        public const float CleaveMomentumKept = CK3CombatHooks.CleaveMomentumKept;

        public override float ApplyGeneralDamageModifiers(in AttackInformation attackInformation, in AttackCollisionData collisionData, float baseDamage) =>
            CK3CombatHooks.GeneralDamage(base.ApplyGeneralDamageModifiers(in attackInformation, in collisionData, baseDamage), in attackInformation, in collisionData);

        public override void DecideWeaponCollisionReaction(in Blow registeredBlow, in AttackCollisionData collisionData, Agent attacker, Agent defender,
            in MissionWeapon attackerWeapon, bool isFatalHit, bool isShruggedOff, float momentumRemaining, out MeleeCollisionReaction colReaction)
        {
            base.DecideWeaponCollisionReaction(in registeredBlow, in collisionData, attacker, defender, in attackerWeapon, isFatalHit, isShruggedOff,
                momentumRemaining, out colReaction);
            CK3CombatHooks.CollisionReaction(attacker, defender, in collisionData, ref colReaction);
        }

        public override float CalculateRemainingMomentum(float originalMomentum, in Blow b, in AttackCollisionData collisionData, Agent attacker, Agent victim,
            in MissionWeapon attackerWeapon, bool isCrushThrough) =>
            CK3CombatHooks.RemainingMomentum(base.CalculateRemainingMomentum(originalMomentum, in b, in collisionData, attacker, victim, in attackerWeapon, isCrushThrough),
                originalMomentum, attacker, victim, in collisionData);
    }

    /// <summary>The CK3 combat effects on the custom battle agent-stat model (<see cref="CK3CombatHooks"/>).</summary>
    public sealed class CK3AgentStatCalculateModel : CustomBattleAgentStatCalculateModel
    {
        public override void UpdateAgentStats(Agent agent, AgentDrivenProperties agentDrivenProperties)
        {
            base.UpdateAgentStats(agent, agentDrivenProperties);
            CK3CombatHooks.AgentStats(agent, agentDrivenProperties);
        }

        public override float GetWeaponInaccuracy(Agent agent, WeaponComponentData weapon, int weaponSkill) =>
            CK3CombatHooks.WeaponInaccuracy(base.GetWeaponInaccuracy(agent, weapon, weaponSkill), agent, weapon);

        public override float GetKnockDownResistance(Agent agent, StrikeType strikeType = StrikeType.Invalid) =>
            CK3CombatHooks.KnockResistance(base.GetKnockDownResistance(agent, strikeType), agent);

        public override float GetKnockBackResistance(Agent agent) => CK3CombatHooks.KnockResistance(base.GetKnockBackResistance(agent), agent);

        public override float GetDismountResistance(Agent agent) => CK3CombatHooks.DismountResistance(base.GetDismountResistance(agent), agent);
    }


    /// <summary>
    /// Initial morale + side and hero initialMoraleAdd (CommonAIComponent.InitializeMorale then clamps to 15..100); heroes with
    /// noPanic never flee for low morale (CommonAIComponent.CanPanic asks CanPanicDueToMorale first).
    /// </summary>
    public sealed class CK3BattleMoraleModel : CustomBattleMoraleModel
    {
        public override float GetEffectiveInitialMorale(Agent agent, float baseMorale)
        {
            float morale = base.GetEffectiveInitialMorale(agent, baseMorale);
            return CK3BuffRegistry.Active ? BuffMath.InitialMorale(morale, CK3BuffRegistry.For(agent)) : morale;
        }

        public override bool CanPanicDueToMorale(Agent agent)
        {
            return base.CanPanicDueToMorale(agent) && !(CK3BuffRegistry.Hero(agent)?.Buffs?.NoPanic ?? false);
        }
    }
}
