using System;
using System.Collections.Generic;
using CK3xBL.Protocol;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace CK3Bridge
{
    /// <summary>
    /// Costumes (PROTOCOL.md §21): dresses each agent of a troop group in the armour of its CK3 look. Every armour slot the stock
    /// troop fills (head, body, legs, gloves, cape) gets an item of the look with about the same armour, so the
    /// troop looks like its CK3 culture, era and role and fights as the stock troop did. Weapons, shields and horses stay stock;
    /// a slot the stock troop leaves empty stays empty. A HorseHarness slot in a look is ignored.
    ///
    /// Items the game did not load (Open Source Armory not installed) or that do not fit the slot are left out when the pools are
    /// built. Called from BattleTracker.Register, in OnAgentBuild; the swap goes through Agent.UpdateSpawnEquipmentAndRefreshVisuals,
    /// which the armour of every later hit is read from.
    /// </summary>
    internal sealed class CK3Costumes
    {
        private struct Candidate
        {
            public ItemObject Item;
            public float Weight;
            public int Armor;
        }

        private static readonly KeyValuePair<string, EquipmentIndex>[] HumanSlots =
        {
            new KeyValuePair<string, EquipmentIndex>("Head", EquipmentIndex.Head),
            new KeyValuePair<string, EquipmentIndex>("Body", EquipmentIndex.Body),
            new KeyValuePair<string, EquipmentIndex>("Leg", EquipmentIndex.Leg),
            new KeyValuePair<string, EquipmentIndex>("Gloves", EquipmentIndex.Gloves),
            new KeyValuePair<string, EquipmentIndex>("Cape", EquipmentIndex.Cape),
        };

        /// <summary>A candidate is "about the same" within max(MinTolerance, Tolerance x stock armour).</summary>
        private const float Tolerance = 0.15f;
        private const int MinTolerance = 2;
        /// <summary>When nothing is that close: the nearest few within the wider band, else the stock item stays.</summary>
        private const float WideTolerance = 0.35f;
        private const int MinWideTolerance = 5;
        private const int NearestFew = 3;

        private readonly Dictionary<string, Dictionary<string, List<Candidate>>> _pools =
            new Dictionary<string, Dictionary<string, List<Candidate>>>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _lookByGroup = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Candidate>> _near = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
        private int _dressed;
        private bool _failureLogged;

        public CK3Costumes(BattleRequest request)
        {
            if (request?.Looks == null) return;
            int listed = 0, loaded = 0;
            MBObjectManager om = MBObjectManager.Instance;
            foreach (KeyValuePair<string, LookSpec> look in request.Looks)
            {
                if (look.Value?.Slots == null) continue;
                var slots = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, List<LookItem>> slot in look.Value.Slots)
                {
                    if (!TryIndex(slot.Key, out EquipmentIndex index) || slot.Value == null) continue;
                    var list = new List<Candidate>();
                    foreach (LookItem entry in slot.Value)
                    {
                        if (entry == null || string.IsNullOrEmpty(entry.Id) || !(entry.Weight > 0f)) continue;
                        listed++;
                        ItemObject item = om.GetObject<ItemObject>(entry.Id);
                        if (item == null || !item.HasArmorComponent || !Equipment.IsItemFitsToSlot(index, item)) continue;
                        list.Add(new Candidate { Item = item, Weight = entry.Weight, Armor = ArmorOf(item) });
                    }
                    loaded += list.Count;
                    if (list.Count > 0) slots[slot.Key] = list;
                }
                if (slots.Count > 0) _pools[look.Key] = slots;
            }
            foreach (SideRequest side in new[] { request.Attacker, request.Defender })
            {
                if (side?.Troops == null) continue;
                string label = side == request.Attacker ? "attacker" : "defender";
                foreach (TroopRequest troop in side.Troops)
                    if (troop?.Group != null && troop.Look != null && _pools.ContainsKey(troop.Look)) _lookByGroup[label + "\n" + troop.Group] = troop.Look;
            }
            BridgeIo.Log($"costumes: {_pools.Count} looks for {_lookByGroup.Count} groups, {loaded} of {listed} items loaded");
        }

        public int Dressed => _dressed;

        /// <summary>The group has a look to dress its agents in.</summary>
        public bool HasLook(BattleSide side, string group) =>
            group != null && _lookByGroup.ContainsKey((side == BattleSide.Attacker ? "attacker" : "defender") + "\n" + group);

        /// <summary>Dresses one agent of <paramref name="group"/>; false when nothing changed (no look, nothing near enough).</summary>
        public bool Dress(Agent agent, BattleSide side, string group)
        {
            if (group == null || _lookByGroup.Count == 0) return false;
            string label = side == BattleSide.Attacker ? "attacker" : "defender";
            if (!_lookByGroup.TryGetValue(label + "\n" + group, out string look)) return false;
            return DressSlots(agent, look, null, hero: false);
        }

        /// <summary>
        /// Dresses a hero in their look, leaving every slot their Equipment.Overrides sets (CK3 gear, artifacts) alone. A lord's kit is
        /// heavier than most of a look's items, so a hero takes the nearest few by armour whatever the distance: always dressed in their
        /// culture and era, as close to the kit's protection as the look allows.
        /// </summary>
        public void DressHero(Agent agent, HeroSpec spec)
        {
            if (spec?.Look == null) return;
            if (DressSlots(agent, spec.Look, spec.Equipment?.Overrides, hero: true)) BridgeIo.Log($"costumes: hero {spec.Key} dressed in {spec.Look}");
        }

        private bool DressSlots(Agent agent, string look, Dictionary<string, string> keep, bool hero)
        {
            if (!_pools.TryGetValue(look, out var slots)) return false;
            try
            {
                Equipment stock = agent.SpawnEquipment;
                if (stock == null) return false;
                Equipment dressed = stock.Clone(false);
                bool changed = false;
                foreach (KeyValuePair<string, EquipmentIndex> slot in HumanSlots)
                    if (keep == null || !keep.ContainsKey(slot.Key)) changed |= Swap(dressed, slot.Value, look, slot.Key, slots, hero);
                if (changed)
                {
                    agent.UpdateSpawnEquipmentAndRefreshVisuals(dressed);
                    _dressed++;
                }
                // Horses are left alone: re-equipping a built mount made it invisible in play (rider floating), so the harness stays stock.
                return changed;
            }
            catch (Exception e)
            {
                // Cosmetic: the agent keeps (or gets back to) its stock armour; logged once per battle.
                if (!_failureLogged) BridgeIo.Log("costumes: dressing failed: " + e);
                _failureLogged = true;
                return false;
            }
        }

        private bool Swap(Equipment equipment, EquipmentIndex index, string look, string slot, Dictionary<string, List<Candidate>> slots, bool hero)
        {
            ItemObject stock = equipment[index].Item;
            if (stock == null || !stock.HasArmorComponent || !slots.TryGetValue(slot, out List<Candidate> pool)) return false;
            List<Candidate> near = hero ? Nearest(look, slot, stock, pool) : Near(look, slot, stock, pool);
            if (near.Count == 0) return false;
            ItemObject pick = Pick(near);
            if (pick == null || pick == stock) return false;
            if (index == EquipmentIndex.HorseHarness && pick.ArmorComponent.FamilyType != stock.ArmorComponent.FamilyType) return false;
            equipment[index] = new EquipmentElement(pick, null, null, false);
            return true;
        }

        /// <summary>The look's items of about the stock item's armour (cached per look, slot and stock item).</summary>
        private List<Candidate> Near(string look, string slot, ItemObject stock, List<Candidate> pool)
        {
            string key = look + "\n" + slot + "\n" + stock.StringId;
            if (_near.TryGetValue(key, out List<Candidate> cached)) return cached;
            int armor = ArmorOf(stock);
            int family = slot == "HorseHarness" ? stock.ArmorComponent.FamilyType : int.MinValue;
            float tolerance = Math.Max(MinTolerance, Tolerance * armor);
            float wide = Math.Max(MinWideTolerance, WideTolerance * armor);
            var near = new List<Candidate>();
            var fallback = new List<Candidate>();
            foreach (Candidate c in pool)
            {
                if (family != int.MinValue && c.Item.ArmorComponent.FamilyType != family) continue;
                int d = Math.Abs(c.Armor - armor);
                if (d <= tolerance) near.Add(c);
                else if (d <= wide) fallback.Add(c);
            }
            if (near.Count == 0 && fallback.Count > 0)
            {
                fallback.Sort((a, b) => Math.Abs(a.Armor - armor).CompareTo(Math.Abs(b.Armor - armor)));
                near.AddRange(fallback.GetRange(0, Math.Min(NearestFew, fallback.Count)));
            }
            _near[key] = near;
            return near;
        }

        /// <summary>A hero's choice: the look's <see cref="NearestFew"/> items closest to the stock item's armour, at any distance.</summary>
        private List<Candidate> Nearest(string look, string slot, ItemObject stock, List<Candidate> pool)
        {
            string key = "hero\n" + look + "\n" + slot + "\n" + stock.StringId;
            if (_near.TryGetValue(key, out List<Candidate> cached)) return cached;
            int armor = ArmorOf(stock);
            var sorted = new List<Candidate>(pool);
            sorted.Sort((a, b) => Math.Abs(a.Armor - armor).CompareTo(Math.Abs(b.Armor - armor)));
            List<Candidate> near = sorted.GetRange(0, Math.Min(NearestFew, sorted.Count));
            _near[key] = near;
            return near;
        }

        private static ItemObject Pick(List<Candidate> near)
        {
            float total = 0f;
            foreach (Candidate c in near) total += c.Weight;
            float roll = MBRandom.RandomFloat * total;
            foreach (Candidate c in near)
            {
                roll -= c.Weight;
                if (roll <= 0f) return c.Item;
            }
            return near[near.Count - 1].Item;
        }

        private static int ArmorOf(ItemObject item)
        {
            ArmorComponent a = item.ArmorComponent;
            return a == null ? 0 : a.HeadArmor + a.BodyArmor + a.LegArmor + a.ArmArmor;
        }

        private static bool TryIndex(string slot, out EquipmentIndex index)
        {
            switch (slot)
            {
                case "Head": index = EquipmentIndex.Head; return true;
                case "Body": index = EquipmentIndex.Body; return true;
                case "Leg": index = EquipmentIndex.Leg; return true;
                case "Gloves": index = EquipmentIndex.Gloves; return true;
                case "Cape": index = EquipmentIndex.Cape; return true;
                default: index = EquipmentIndex.None; return false;
            }
        }
    }
}
