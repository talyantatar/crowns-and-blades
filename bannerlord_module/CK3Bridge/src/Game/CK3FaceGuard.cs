using System;
using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace CK3Bridge
{
    /// <summary>
    /// Hero spawn fix-ups in OnAgentBuild (runs after Mission.BuildAgent):
    ///  - re-applies the CK3 body when anything changed it (native GetRandomBodyProperties with min == max is not
    ///    guaranteed exact, and another OnComputeTroopBodyProperties handler could swap it), then re-equips exactly as
    ///    Mission.BuildAgent does so the skin meshes are rebuilt for the new body;
    ///  - applies HeroSpec.healthMultiplier (prowess, knight effectiveness) to the hero's health pool, then
    ///    buffs.startHealthFraction (a hero who starts wounded, PROTOCOL.md §13.4 item 5).
    /// </summary>
    internal sealed class CK3FaceGuard : MissionLogic
    {
        private readonly Dictionary<BasicCharacterObject, CreatedHero> _heroes;

        public CK3FaceGuard(Dictionary<BasicCharacterObject, CreatedHero> heroes)
        {
            _heroes = heroes;
        }

        public override void OnAgentBuild(Agent agent, Banner banner)
        {
            base.OnAgentBuild(agent, banner);
            try
            {
                FixUp(agent);
            }
            catch (Exception e)
            {
                BridgeIo.Log("face guard failed: " + e);   // cosmetic: never let it reach the mission loop
            }
        }

        private void FixUp(Agent agent)
        {
            if (agent.Character == null || !_heroes.TryGetValue(agent.Character, out CreatedHero hero)) return;

            BodyProperties target = TargetBody(agent, hero);
            if (agent.BodyPropertiesValue.StaticProperties != target.StaticProperties)
            {
                BridgeIo.Log($"face guard: re-applied the CK3 body of {hero.Spec.Key} ({agent.Character.StringId})");
                agent.UpdateBodyProperties(target);
                agent.EquipItemsFromSpawnEquipment(neededBatchedItems: true, prepareImmediately: agent == Agent.Main, useFaceCache: false, faceCacheID: 0);
            }

            if (hero.HealthMultiplier != 1f)
            {
                agent.BaseHealthLimit *= hero.HealthMultiplier;
                agent.HealthLimit *= hero.HealthMultiplier;
                agent.Health = agent.HealthLimit;
            }
            if (BuffMath.TryStartHealth(agent.HealthLimit, CK3BuffRegistry.Hero(agent)?.Buffs, out float health)) agent.Health = health;
        }

        /// <summary>
        /// The CK3 body as the engine dresses it. Bare-headed, or under headgear that leaves the hair alone, that is exactly
        /// <see cref="CreatedHero.Body"/>. Under headgear that covers hair, the engine computes the spawn body for that
        /// equipment (BasicCharacterObject.GetBodyProperties passes Equipment.HairCoverType to FaceGen.GetRandomBodyProperties,
        /// and the hero's range is min = max = Body), so the guard takes the same computation,
        /// with the agent's own equipment and seed, instead of putting uncovered hair back under the helmet.
        /// </summary>
        private static BodyProperties TargetBody(Agent agent, CreatedHero hero)
        {
            Equipment equipment = agent.SpawnEquipment;
            if (equipment == null || equipment.HairCoverType == ArmorComponent.HairCoverTypes.None) return hero.Body;
            BodyProperties dressed = hero.Character.GetBodyProperties(equipment, agent.BodyPropertiesSeed);
            return new BodyProperties(hero.Body.DynamicProperties, dressed.StaticProperties);
        }
    }
}
