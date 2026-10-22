using System;
using System.Collections.Generic;
using System.Reflection;
using CK3xBL.Protocol;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace CK3Bridge
{
    /// <summary>
    /// Reorders the stock CustomBattleTroopSupplier (PROTOCOL.md §3.6, <see cref="SpawnOrder"/>). Its constructor fills the private
    /// queue _characters (TaleWorlds PriorityQueue with GenericComparer: highest priority first) and AllocateTroop(s) only dequeue
    /// from it, so replacing the queue right after construction changes the order and nothing else. Without the field (another
    /// game version) the stock order stays and one line is logged.
    /// </summary>
    internal static class CK3SpawnOrder
    {
        private static readonly FieldInfo Queue =
            typeof(CustomBattleTroopSupplier).GetField("_characters", BindingFlags.Instance | BindingFlags.NonPublic);

        public static void Apply(CustomBattleTroopSupplier supplier, BuiltSide side, string label)
        {
            try
            {
                if (Queue == null || !(Queue.GetValue(supplier) is PriorityQueue<float, BasicCharacterObject> stock))
                {
                    BridgeIo.Log($"{label}: spawn order left stock (CustomBattleTroopSupplier._characters not found)");
                    return;
                }
                var available = new Dictionary<BasicCharacterObject, int>();
                foreach (KeyValuePair<float, BasicCharacterObject> held in stock)   // read only: on any failure the stock queue stays
                {
                    BasicCharacterObject c = held.Value;
                    if (c != null) available[c] = (available.TryGetValue(c, out int n) ? n : 0) + 1;
                }

                var entries = new List<KeyValuePair<BasicCharacterObject, KeyValuePair<string, int>>>();
                if (side.Request?.Troops != null)
                    foreach (TroopRequest troop in side.Request.Troops)
                    {
                        if (troop == null || troop.Count <= 0 || string.IsNullOrEmpty(troop.Id)) continue;
                        BasicCharacterObject c = MBObjectManager.Instance.GetObject<BasicCharacterObject>(troop.Id);
                        if (c == null || c.IsHero) continue;
                        entries.Add(new KeyValuePair<BasicCharacterObject, KeyValuePair<string, int>>(c, new KeyValuePair<string, int>(RequestRules.GroupKey(troop), troop.Count)));
                    }

                var queue = new PriorityQueue<float, BasicCharacterObject>(new GenericComparer<float>());
                int troops = 0;
                foreach (KeyValuePair<BasicCharacterObject, float> agent in SpawnOrder.Plan(entries, available))
                {
                    queue.Enqueue(agent.Value, agent.Key);
                    available[agent.Key]--;
                    troops++;
                }
                int heroes = 0;
                foreach (KeyValuePair<BasicCharacterObject, int> rest in available)
                    for (int i = 0; i < rest.Value; i++, heroes++) queue.Enqueue(SpawnOrder.HeroPriority, rest.Key);   // heroes, the stock general
                Queue.SetValue(supplier, queue);
                BridgeIo.Log($"{label}: spawn order professionals first, levies last ({troops} troops, {heroes} heroes first)");
            }
            catch (Exception e)
            {
                BridgeIo.Log($"{label}: spawn order failed, stock order kept: {e.Message}");
            }
        }
    }
}
