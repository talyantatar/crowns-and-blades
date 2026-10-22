using NavalDLC.ComponentInterfaces;
using NavalDLC.GameComponents;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace CK3Bridge.Naval
{
    /// <summary>The CK3 combat effects (CK3CombatHooks) on the naval damage model, which every battle uses with War Sails loaded.</summary>
    public sealed class CK3NavalAgentApplyDamageModel : NavalDLCCustomAgentApplyDamageModel
    {
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

    /// <summary>
    /// The custom battle's initialization model reads the land battle's DefaultBattleMissionAgentSpawnLogic to decide on the order of
    /// battle; a naval mission has none, and the read threw at the ships' deployment plan (bench 2026-10-19: NullReferenceException in
    /// CanPlayerSideDeployWithOrderOfBattleAux). At sea there is no order of battle; on land the stock answer stands.
    /// </summary>
    public sealed class CK3NavalBattleInitializationModel : CustomBattleInitializationModel
    {
        protected override bool CanPlayerSideDeployWithOrderOfBattleAux() =>
            Mission.Current?.GetMissionBehavior<DefaultBattleMissionAgentSpawnLogic>() != null && base.CanPlayerSideDeployWithOrderOfBattleAux();
    }

    /// <summary>The CK3 combat effects (CK3CombatHooks) on the naval agent-stat model, which every battle uses with War Sails loaded.</summary>
    public sealed class CK3NavalAgentStatCalculateModel : NavalCustomBattleAgentStatCalculateModel
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
}
