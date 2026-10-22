using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using CK3xBL.Protocol;
using TaleWorlds.Core;
using TaleWorlds.ObjectSystem;

namespace CK3Bridge
{
    /// <summary>A CK3 character turned into a registered Bannerlord hero for one battle.</summary>
    internal sealed class CreatedHero
    {
        public HeroSpec Spec;
        public BattleSide Side;
        public BasicCharacterObject Character;
        public BodyProperties Body;
        public float HealthMultiplier = 1f;
        public Banner Banner;                  // the hero's own arms (PROTOCOL.md §25); null = the side's banner
    }

    /// <summary>
    /// Creates heroes through the same Deserialize path the XML loader uses (MBObjectManager.CreateObjectFromXmlNode,
    /// then BasicCharacterObject.Deserialize) and unregisters them after the battle.
    /// Rules:
    ///  - fresh id per battle without dots; an existing id would be re-deserialized and grow its equipment roster;
    ///  - every reference (culture, roster, item, skill) is validated first: an unknown one silently creates an empty
    ///    "presumed" object that breaks at spawn;
    ///  - &lt;Equipments&gt; always holds an EquipmentSet or EquipmentRoster (overrides alone throw);
    ///  - no &lt;face&gt; node: the exact body is set with UpdatePlayerCharacterBodyProperties (no culture-dependent float
    ///    parsing in BodyProperties.FromXmlNode);
    ///  - is_hero: fixed first battle equipment, exact face (MissionFaceCacheView skips heroes), spawns first, captain.
    /// </summary>
    internal static class HeroFactory
    {
        private static readonly HashSet<string> EquipmentSlots = new HashSet<string>(StringComparer.Ordinal)
        {
            "Item0", "Item1", "Item2", "Item3", "Item4", "Head", "Body", "Leg", "Gloves", "Cape", "Horse", "HorseHarness",
        };

        private static readonly HashSet<string> FormationClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Infantry", "Ranged", "Cavalry", "HorseArcher", "Skirmisher", "HeavyInfantry", "LightCavalry", "HeavyCavalry",
        };

        /// <summary>Culture id -> prefix of the lord kits in sandboxcore_equipment_sets.xml (loaded in CustomGame).</summary>
        private static readonly Dictionary<string, string> KitPrefixByCulture = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "empire", "emp" }, { "vlandia", "vla" }, { "sturgia", "stu" }, { "aserai", "ase" }, { "battania", "bat" }, { "khuzait", "khu" },
        };

        public const int MinAge = 16;
        public const int MaxAge = 100;
        public const int MaxLevel = 62;
        public const int MaxSkill = 400;

        public static CreatedHero Create(string battleId, HeroSpec spec, BattleSide side, string cultureId, string displayName, int level)
        {
            MBObjectManager om = MBObjectManager.Instance;
            string id = Naming.HeroObjectId(battleId, spec.Key);
            for (int n = 2; om.GetObject<BasicCharacterObject>(id) != null; n++)
                id = Naming.HeroObjectId(battleId, spec.Key) + "_" + n;

            int age = Math.Max(MinAge, Math.Min(MaxAge, spec.Age));
            var doc = new XmlDocument();
            XmlElement node = doc.CreateElement("NPCCharacter");
            node.SetAttribute("id", id);
            node.SetAttribute("name", "{=!}" + displayName);
            node.SetAttribute("culture", "Culture." + cultureId);
            node.SetAttribute("is_female", spec.Female ? "true" : "false");
            node.SetAttribute("age", age.ToString(CultureInfo.InvariantCulture));   // Deserialize reads it with Convert.ToInt32
            node.SetAttribute("level", Math.Max(1, Math.Min(MaxLevel, level)).ToString(CultureInfo.InvariantCulture));
            node.SetAttribute("default_group", FormationClasses.Contains(spec.Formation ?? "") ? spec.Formation : "Infantry");
            node.SetAttribute("occupation", "Lord");
            node.SetAttribute("is_hero", "true");
            doc.AppendChild(node);

            XmlElement skills = SkillsNode(doc, spec);
            if (skills != null) node.AppendChild(skills);
            node.AppendChild(EquipmentNode(doc, spec, cultureId));

            BasicCharacterObject character;
            try
            {
                character = (BasicCharacterObject)om.CreateObjectFromXmlNode(node);
            }
            catch
            {
                Release(om.GetObject<BasicCharacterObject>(id));   // a half-deserialized object is already registered
                throw;
            }
            BodyProperties body;
            try
            {
                body = CK3Appearance.Build(spec.Appearance, spec.Female, age, cultureId, CK3Appearance.SeedFor(spec));
            }
            catch (Exception e)
            {
                // A face is cosmetic: never lose the battle over it.
                BridgeIo.Log($"hero {spec.Key}: face generation failed ({e.Message}); using a stock face");
                body = CK3Appearance.StockBody(spec.Female, age);
            }
            character.UpdatePlayerCharacterBodyProperties(body, character.Race, spec.Female);

            // Up to x6 (the bridge's prowess soft cap reaches x5 at prowess 100, knight effectiveness on top).
            float health = float.IsNaN(spec.HealthMultiplier) ? 1f : Math.Max(0.25f, Math.Min(6f, spec.HealthMultiplier));
            return new CreatedHero { Spec = spec, Side = side, Character = character, Body = body, HealthMultiplier = health };
        }

        private static XmlElement SkillsNode(XmlDocument doc, HeroSpec spec)
        {
            if (spec.Skills == null || spec.Skills.Count == 0) return null;
            XmlElement skills = doc.CreateElement("skills");
            foreach (KeyValuePair<string, int> skill in spec.Skills)
            {
                if (MBObjectManager.Instance.GetObject<SkillObject>(skill.Key) == null)
                {
                    BridgeIo.Log($"hero {spec.Key}: unknown skill '{skill.Key}' skipped");
                    continue;
                }
                XmlElement s = doc.CreateElement("skill");
                s.SetAttribute("id", skill.Key);
                s.SetAttribute("value", Math.Max(0, Math.Min(MaxSkill, skill.Value)).ToString(CultureInfo.InvariantCulture));
                skills.AppendChild(s);
            }
            return skills.HasChildNodes ? skills : null;
        }

        private static XmlElement EquipmentNode(XmlDocument doc, HeroSpec spec, string cultureId)
        {
            XmlElement equipments = doc.CreateElement("Equipments");
            string roster = ResolveRoster(spec, cultureId);
            if (roster != null)
            {
                XmlElement set = doc.CreateElement("EquipmentSet");
                set.SetAttribute("id", roster);
                equipments.AppendChild(set);
            }
            else
            {
                equipments.AppendChild(doc.CreateElement("EquipmentRoster"));
            }

            if (spec.Equipment?.Overrides == null) return equipments;
            foreach (KeyValuePair<string, string> slot in spec.Equipment.Overrides)
            {
                if (!EquipmentSlots.Contains(slot.Key))
                {
                    BridgeIo.Log($"hero {spec.Key}: unknown equipment slot '{slot.Key}' skipped");
                    continue;
                }
                if (slot.Value == null) continue;
                // "" empties the slot (PROTOCOL.md §6.2, e.g. "Horse": "" for nobles who fight on foot): Equipment.DeserializeNode
                // looks the id up with GetObject, gets null, and a null item fits every slot.
                if (slot.Value.Length > 0)
                {
                    ItemObject item = MBObjectManager.Instance.GetObject<ItemObject>(slot.Value);
                    if (item == null)
                    {
                        BridgeIo.Log($"hero {spec.Key}: unknown item '{slot.Value}' for {slot.Key} skipped");
                        continue;
                    }
                    if (!Equipment.IsItemFitsToSlot(Equipment.GetEquipmentIndexFromOldEquipmentIndexName(slot.Key), item))
                    {
                        BridgeIo.Log($"hero {spec.Key}: item '{slot.Value}' does not fit {slot.Key}, skipped");
                        continue;
                    }
                }
                XmlElement e = doc.CreateElement("equipment");
                e.SetAttribute("slot", slot.Key);
                e.SetAttribute("id", slot.Value.Length > 0 ? "Item." + slot.Value : "");
                equipments.AppendChild(e);
            }
            return equipments;
        }

        /// <summary>The requested roster when it exists, else the culture's lord kit (heroes always wear the first set).</summary>
        private static string ResolveRoster(HeroSpec spec, string cultureId)
        {
            string requested = spec.Equipment?.Roster;
            if (!string.IsNullOrEmpty(requested))
            {
                if (MBObjectManager.Instance.GetObject<MBEquipmentRoster>(requested) != null) return requested;
                BridgeIo.Log($"hero {spec.Key}: unknown equipment roster '{requested}', using the culture lord kit");
            }
            if (!KitPrefixByCulture.TryGetValue(cultureId, out string prefix)) prefix = "vla";
            string fallback = prefix + (spec.Female ? "_bat_template_lady" : "_bat_template_medium");
            if (MBObjectManager.Instance.GetObject<MBEquipmentRoster>(fallback) != null) return fallback;
            BridgeIo.Log($"hero {spec.Key}: lord kit '{fallback}' not found, hero keeps only the overrides");
            return null;
        }

        /// <summary>
        /// Unregisters the hero and the side objects its Deserialize registered under the same id: equipment roster,
        /// skills and body range. Call only after the mission is gone. A subclass of
        /// BasicCharacterObject could not be unregistered (MBObjectManager.UnregisterObject uses GetType()),
        /// which is why heroes are plain XML characters.
        /// </summary>
        public static void Release(BasicCharacterObject character)
        {
            if (character == null) return;
            MBObjectManager om = MBObjectManager.Instance;
            string id = character.StringId;
            MBCharacterSkills skills = character.GetDefaultCharacterSkills();
            MBBodyProperty range = character.BodyPropertyRange;
            MBEquipmentRoster roster = om.GetObject<MBEquipmentRoster>(id);
            om.UnregisterObject(character);
            if (roster != null) om.UnregisterObject(roster);
            if (skills != null && skills.StringId == id) om.UnregisterObject(skills);
            if (range != null && range.StringId == id) om.UnregisterObject(range);
        }
    }
}
