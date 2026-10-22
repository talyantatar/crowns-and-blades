using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using TaleWorlds.Core;
using TaleWorlds.ObjectSystem;

namespace CK3Bridge
{
    /// <summary>
    /// PROTOCOL.md §43: a troop on another mount (a war elephant). A battle character is created per troop and mount, through the same
    /// XML path as heroes (<see cref="HeroFactory"/>): the troop's culture, level, skills, face range and every battle equipment set,
    /// with the horse and harness slots replaced. Mounts are chosen before the spawn: re-equipping a built mount made it invisible.
    /// Released with the battle's heroes.
    /// </summary>
    internal static class MountedTroopFactory
    {
        private static readonly string[] Slots =
            { "Item0", "Item1", "Item2", "Item3", "Item4", "Head", "Body", "Leg", "Gloves", "Cape", "Horse", "HorseHarness" };

        /// <summary>The troop on <paramref name="mountId"/> (with <paramref name="harnessId"/>, may be null); null when the mount is not
        /// loaded (the troop then rides its own horse).</summary>
        public static BasicCharacterObject Create(string battleId, int serial, BasicCharacterObject troop, string mountId, string harnessId)
        {
            MBObjectManager om = MBObjectManager.Instance;
            ItemObject mount = om.GetObject<ItemObject>(mountId ?? "");
            if (mount == null || !Equipment.IsItemFitsToSlot(EquipmentIndex.Horse, mount)) return null;
            ItemObject harness = string.IsNullOrEmpty(harnessId) ? null : om.GetObject<ItemObject>(harnessId);
            if (harness != null && !Equipment.IsItemFitsToSlot(EquipmentIndex.HorseHarness, harness)) harness = null;

            string id = Naming.HeroObjectId(battleId, "mnt" + serial.ToString(CultureInfo.InvariantCulture));
            var doc = new XmlDocument();
            XmlElement node = doc.CreateElement("NPCCharacter");
            node.SetAttribute("id", id);
            node.SetAttribute("name", "{=!}" + troop.Name);
            if (troop.Culture != null) node.SetAttribute("culture", "Culture." + troop.Culture.StringId);
            node.SetAttribute("is_female", troop.IsFemale ? "true" : "false");
            node.SetAttribute("level", Math.Max(1, troop.Level).ToString(CultureInfo.InvariantCulture));
            node.SetAttribute("default_group", "Cavalry");
            node.SetAttribute("occupation", "Soldier");
            doc.AppendChild(node);

            if (troop.BodyPropertyRange != null)
            {
                XmlElement face = doc.CreateElement("face");
                XmlElement template = doc.CreateElement("face_key_template");
                template.SetAttribute("value", "BodyProperty." + troop.BodyPropertyRange.StringId);
                face.AppendChild(template);
                node.AppendChild(face);
            }

            XmlElement skills = doc.CreateElement("skills");
            foreach (SkillObject skill in om.GetObjectTypeList<SkillObject>())
            {
                int value = troop.GetSkillValue(skill);
                if (value <= 0) continue;
                XmlElement s = doc.CreateElement("skill");
                s.SetAttribute("id", skill.StringId);
                s.SetAttribute("value", value.ToString(CultureInfo.InvariantCulture));
                skills.AppendChild(s);
            }
            if (skills.HasChildNodes) node.AppendChild(skills);

            XmlElement equipments = doc.CreateElement("Equipments");
            foreach (Equipment set in troop.BattleEquipments)
            {
                XmlElement roster = doc.CreateElement("EquipmentRoster");
                foreach (string slot in Slots)
                {
                    EquipmentIndex index = Equipment.GetEquipmentIndexFromOldEquipmentIndexName(slot);
                    ItemObject item = slot == "Horse" ? mount : slot == "HorseHarness" ? harness : set[index].Item;
                    if (item == null) continue;
                    XmlElement e = doc.CreateElement("equipment");
                    e.SetAttribute("slot", slot);
                    e.SetAttribute("id", "Item." + item.StringId);
                    roster.AppendChild(e);
                }
                equipments.AppendChild(roster);
            }
            if (!equipments.HasChildNodes) return null;
            node.AppendChild(equipments);

            try
            {
                return (BasicCharacterObject)om.CreateObjectFromXmlNode(node);
            }
            catch (Exception e)
            {
                HeroFactory.Release(om.GetObject<BasicCharacterObject>(id));
                BridgeIo.Log($"mounted troop {troop.StringId} on {mountId} failed ({e.Message}); it rides its own horse");
                return null;
            }
        }
    }
}
