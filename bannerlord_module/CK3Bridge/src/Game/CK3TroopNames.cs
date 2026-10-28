using System;
using System.Collections.Generic;
using System.Reflection;
using CK3xBL.Protocol;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

namespace CK3Bridge
{
    /// <summary>
    /// Soldiers' names in CK3's language (TroopRequest.Name): a regiment shows its CK3 name ("Конные самураи"), levies and retainers
    /// their kind and role, instead of the stock troop's name ("Khuzait Spearman", or an English name a module never translated).
    /// Agent.Name has no setter; the name lives in Agent._name, set right after the agent is built. Cosmetic: a failure leaves the
    /// stock name and is logged once.
    /// </summary>
    internal sealed class CK3TroopNames
    {
        private static readonly FieldInfo NameField =
            typeof(Agent).GetField("_name", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly Dictionary<string, TextObject> _byTroop = new Dictionary<string, TextObject>(StringComparer.Ordinal);
        private readonly Dictionary<string, TextObject> _byGroup = new Dictionary<string, TextObject>(StringComparer.Ordinal);
        private bool _failureLogged;

        public CK3TroopNames(BattleRequest request)
        {
            if (request == null) return;
            foreach (SideRequest side in new[] { request.Attacker, request.Defender })
            {
                if (side?.Troops == null) continue;
                string label = side == request.Attacker ? "attacker" : "defender";
                foreach (TroopRequest troop in side.Troops)
                {
                    if (troop?.Group == null || string.IsNullOrWhiteSpace(troop.Name)) continue;
                    string name = Naming.SanitizeDisplayName(GameFonts.Fold(troop.Name), null);
                    if (string.IsNullOrEmpty(name)) continue;
                    var text = new TextObject("{=!}" + name);
                    _byTroop[label + "\n" + troop.Group + "\n" + troop.Id] = text;
                    if (!_byGroup.ContainsKey(label + "\n" + troop.Group)) _byGroup[label + "\n" + troop.Group] = text;
                }
            }
            if (_byGroup.Count > 0 && NameField == null) BridgeIo.Log("troop names: Agent._name not found; soldiers keep their stock names");
        }

        /// <summary>Gives the agent its group's name; a troop made for this battle (another mount) falls back to the group's.</summary>
        public void Apply(Agent agent, BattleSide side, string group)
        {
            if (NameField == null || group == null || _byGroup.Count == 0 || agent?.Character == null) return;
            string label = side == BattleSide.Attacker ? "attacker" : "defender";
            if (!_byTroop.TryGetValue(label + "\n" + group + "\n" + agent.Character.StringId, out TextObject name) &&
                !_byGroup.TryGetValue(label + "\n" + group, out name)) return;
            try
            {
                NameField.SetValue(agent, name);
            }
            catch (Exception e)
            {
                if (!_failureLogged) BridgeIo.Log("troop names: naming failed: " + e);
                _failureLogged = true;
            }
        }
    }
}
