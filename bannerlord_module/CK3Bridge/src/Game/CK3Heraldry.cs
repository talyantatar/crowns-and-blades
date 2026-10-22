using System;
using System.Collections.Generic;
using TaleWorlds.Core;

namespace CK3Bridge
{
    /// <summary>
    /// Heroes' own arms (PROTOCOL.md §25). Every agent of a custom battle shows its origin's Banner on heraldic shields and
    /// garments, and the stock CustomBattleAgentOrigin returns the side's banner (its SetBanner throws). This supplier wraps the
    /// stock one and hands out a <see cref="HeroOrigin"/> for each hero with arms: the same origin in every respect but the Banner.
    /// Team colours stay the side's (MissionCombatantsLogic takes them from the combatant). One wrapper per stock origin, so the
    /// mission sees the same object every time it asks.
    /// </summary>
    internal sealed class CK3HeraldrySupplier : IMissionTroopSupplier
    {
        private readonly IMissionTroopSupplier _inner;
        private readonly Dictionary<BasicCharacterObject, Banner> _arms;
        private readonly Dictionary<IAgentOriginBase, IAgentOriginBase> _wrapped = new Dictionary<IAgentOriginBase, IAgentOriginBase>();

        private CK3HeraldrySupplier(IMissionTroopSupplier inner, Dictionary<BasicCharacterObject, Banner> arms)
        {
            _inner = inner;
            _arms = arms;
        }

        /// <summary>The wrapped supplier, or <paramref name="inner"/> itself when no hero of the side has arms of their own.</summary>
        public static IMissionTroopSupplier Wrap(IMissionTroopSupplier inner, BuiltSide side, string label)
        {
            var arms = new Dictionary<BasicCharacterObject, Banner>();
            foreach (CreatedHero hero in side.Heroes)
                if (hero.Banner != null) arms[hero.Character] = hero.Banner;
            if (arms.Count == 0) return inner;
            BridgeIo.Log($"{label}: {arms.Count} hero(es) carry their own arms");
            return new CK3HeraldrySupplier(inner, arms);
        }

        private IAgentOriginBase Map(IAgentOriginBase origin)
        {
            if (origin?.Troop == null || !_arms.TryGetValue(origin.Troop, out Banner arms)) return origin;
            if (!_wrapped.TryGetValue(origin, out IAgentOriginBase hero)) _wrapped[origin] = hero = new HeroOrigin(origin, arms);
            return hero;
        }

        private IEnumerable<IAgentOriginBase> MapAll(IEnumerable<IAgentOriginBase> origins)
        {
            var list = new List<IAgentOriginBase>();
            if (origins == null) return list;
            foreach (IAgentOriginBase o in origins) list.Add(Map(o));
            return list;
        }

        public IEnumerable<IAgentOriginBase> SupplyTroops(int numberToAllocate) => MapAll(_inner.SupplyTroops(numberToAllocate));
        public IAgentOriginBase SupplyOneTroop() => Map(_inner.SupplyOneTroop());
        public IEnumerable<IAgentOriginBase> GetAllTroops() => MapAll(_inner.GetAllTroops());
        public BasicCharacterObject GetGeneralCharacter() => _inner.GetGeneralCharacter();
        public int GetNumberOfPlayerControllableTroops() => _inner.GetNumberOfPlayerControllableTroops();
        public int NumRemovedTroops => _inner.NumRemovedTroops;
        public int NumTroopsNotSupplied => _inner.NumTroopsNotSupplied;
        public bool AnyTroopRemainsToBeSupplied => _inner.AnyTroopRemainsToBeSupplied;

        /// <summary>A stock origin with the hero's own banner; everything else (casualty callbacks included) goes to the stock one.</summary>
        private sealed class HeroOrigin : IAgentOriginBase
        {
            private readonly IAgentOriginBase _inner;
            private Banner _banner;

            public HeroOrigin(IAgentOriginBase inner, Banner banner)
            {
                _inner = inner;
                _banner = banner;
            }

            public bool IsUnderPlayersCommand => _inner.IsUnderPlayersCommand;
            public bool IsInSameArmyAsPlayer => _inner.IsInSameArmyAsPlayer;
            public uint FactionColor => _inner.FactionColor;
            public uint FactionColor2 => _inner.FactionColor2;
            public IBattleCombatant BattleCombatant => _inner.BattleCombatant;
            public int UniqueSeed => _inner.UniqueSeed;
            public int Seed => _inner.Seed;
            public Banner Banner => _banner;
            public BasicCharacterObject Troop => _inner.Troop;
            public bool HasThrownWeapon => _inner.HasThrownWeapon;
            public bool HasHeavyArmor => _inner.HasHeavyArmor;
            public bool HasShield => _inner.HasShield;
            public bool HasSpear => _inner.HasSpear;
            public void SetWounded() => _inner.SetWounded();
            public void SetKilled() => _inner.SetKilled();
            public void SetRouted(bool isOrderRetreat) => _inner.SetRouted(isOrderRetreat);
            public void OnAgentRemoved(float agentHealth) => _inner.OnAgentRemoved(agentHealth);
            public void OnScoreHit(BasicCharacterObject victim, BasicCharacterObject formationCaptain, int damage, bool isFatal, bool isTeamKill, WeaponComponentData attackerWeapon) =>
                _inner.OnScoreHit(victim, formationCaptain, damage, isFatal, isTeamKill, attackerWeapon);
            public void SetBanner(Banner banner)
            {
                if (banner != null) _banner = banner;
            }
            public TroopTraitsMask GetTraitsMask() => _inner.GetTraitsMask();
        }
    }
}
