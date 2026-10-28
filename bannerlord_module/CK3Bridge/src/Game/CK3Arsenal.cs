using System;
using System.Collections.Generic;
using System.Linq;
using CK3xBL.Protocol;
using TaleWorlds.Core;
using TaleWorlds.Core.ImageIdentifiers;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace CK3Bridge
{
    /// <summary>
    /// The Arsenal (PROTOCOL.md §51): an arena where the player tries on what he may carry (request.arsenal: his CK3 battle gear,
    /// equipped or not, his kit and ammunition). Nobody gets hurt; the sparring partner stands still. The slot menu opens at the start
    /// and on I: a slot, then its item (pictures from Bannerlord's own thumbnails); the player's agent is re-dressed at once (a horse
    /// only in the next battle). "Done" ends the mission; what he wears then goes back as result.loadout (BattleTracker).
    /// </summary>
    internal sealed class CK3Arsenal : MissionLogic
    {
        private static readonly string[] WeaponSlots = { "Item0", "Item1", "Item2", "Item3" };
        private static readonly string[] Slots = { "Item0", "Item1", "Item2", "Item3", "Head", "Body", "Gloves", "Leg", "Cape", "Horse" };
        private const string DoneId = "done";

        private readonly ArsenalSpec _spec;
        private readonly bool _ru;
        private readonly Dictionary<string, string> _loadout;
        private readonly Dictionary<string, ArsenalItem> _items = new Dictionary<string, ArsenalItem>(StringComparer.Ordinal);
        private bool _ready;
        private bool _menuOpen;
        private bool _done;

        public CK3Arsenal(ArsenalSpec spec, string language)
        {
            _spec = spec;
            _ru = language == "ru";
            _loadout = new Dictionary<string, string>(spec.Current ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            foreach (ArsenalItem item in spec.Items ?? new List<ArsenalItem>())
                if (!string.IsNullOrEmpty(item?.Id) && Item(item.Id) != null && !_items.ContainsKey(item.Id)) _items[item.Id] = item;
        }

        /// <summary>The loadout as it stands (slot -> item id, "" = empty); every slot the Arsenal knows.</summary>
        public Dictionary<string, string> Loadout()
        {
            var loadout = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string slot in Slots) loadout[slot] = _loadout.TryGetValue(slot, out string id) ? id ?? "" : "";
            return loadout;
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            try
            {
                if (_done) return;
                Agent me = Mission.MainAgent;
                if (!_ready)
                {
                    if (me == null || !me.IsActive()) return;
                    _ready = true;
                    Calm();
                    BridgeIo.Log($"arsenal: {_items.Count} item(s) offered to {_spec.Hero}");
                    InformationManager.DisplayMessage(new InformationMessage(T("The Arsenal: press I to choose your gear", "Арсенал: I — выбор снаряжения")));
                    OpenSlots();
                    return;
                }
                if (!_menuOpen && !InformationManager.IsAnyInquiryActive() && Input.IsKeyPressed(InputKey.I)) OpenSlots();
            }
            catch (Exception e)
            {
                BridgeIo.Log("arsenal: tick failed: " + e);
            }
        }

        /// <summary>Nobody can be hurt, and everyone but the player stands still.</summary>
        private void Calm()
        {
            foreach (Agent agent in Mission.Agents.ToList())
            {
                if (!agent.IsHuman) continue;
                agent.SetMortalityState(Agent.MortalityState.Invulnerable);
                if (agent == Mission.MainAgent) continue;
                agent.SetAutomaticTargetSelection(false);
                agent.SetWatchState(Agent.WatchState.Patrolling);
                agent.SetIsAIPaused(true);
            }
        }

        // ---- menus ---------------------------------------------------------------------------------------------------

        private void OpenSlots()
        {
            var elements = new List<InquiryElement>();
            foreach (string slot in Slots)
            {
                string id = _loadout.TryGetValue(slot, out string worn) ? worn : "";
                ItemObject item = Item(id);
                string title = $"{SlotName(slot)}: {(item != null ? Name(item) : T("empty", "пусто"))}";
                elements.Add(new InquiryElement(slot, title, item != null ? new ItemImageIdentifier(item, "") : null, true, Hint(item)));
            }
            elements.Add(new InquiryElement(DoneId, T("Done: keep this loadout", "Готово: сохранить набор"), null, true, ""));
            Show(new MultiSelectionInquiryData(T("The Arsenal", "Арсенал"), T("Choose a slot.", "Выберите слот."), elements, true, 1, 1,
                T("Choose", "Выбрать"), T("Close", "Закрыть"), chosen =>
                {
                    _menuOpen = false;
                    string slot = chosen?.FirstOrDefault()?.Identifier as string;
                    if (slot == DoneId) Done();
                    else if (slot != null) OpenItems(slot);
                }, _ => _menuOpen = false));
        }

        private void OpenItems(string slot)
        {
            string kind = Array.IndexOf(WeaponSlots, slot) >= 0 ? "Weapon" : slot;
            var elements = new List<InquiryElement> { new InquiryElement("", T("— empty —", "— пусто —"), null, true, "") };
            foreach (ArsenalItem a in _items.Values.Where(i => i.Slot == kind))
            {
                ItemObject item = Item(a.Id);
                string title = Name(item) + (a.Source == "artifact" ? T(" (artifact)", " (артефакт)") : "");
                elements.Add(new InquiryElement(a.Id, title, new ItemImageIdentifier(item, ""), true, Hint(item)));
            }
            Show(new MultiSelectionInquiryData(SlotName(slot), T("Choose what to carry.", "Выберите, что взять."), elements, true, 1, 1,
                T("Take", "Взять"), T("Back", "Назад"), chosen =>
                {
                    _menuOpen = false;
                    if (chosen?.FirstOrDefault()?.Identifier is string id) Put(slot, id);
                    OpenSlots();
                }, _ => { _menuOpen = false; OpenSlots(); }));
        }

        private void Show(MultiSelectionInquiryData data)
        {
            _menuOpen = true;
            MBInformationManager.ShowMultiSelectionInquiry(data, false);
        }

        // ---- dressing ------------------------------------------------------------------------------------------------

        /// <summary>Puts the item in the slot, and on the player's agent at once (weapons in hand, armour on the body).</summary>
        private void Put(string slot, string id)
        {
            // One item, one weapon slot: taking a weapon already carried elsewhere moves it here.
            if (id.Length > 0 && Array.IndexOf(WeaponSlots, slot) >= 0)
                foreach (string other in WeaponSlots)
                    if (other != slot && _loadout.TryGetValue(other, out string there) && there == id) { _loadout[other] = ""; Dress(other, ""); }
            _loadout[slot] = id;
            BridgeIo.Log($"arsenal: {slot} = {(id.Length > 0 ? id : "(empty)")}");
            if (slot == "Horse")
            {
                InformationManager.DisplayMessage(new InformationMessage(T("The horse comes with you in the next battle.", "Конь будет с вами в следующем бою.")));
                return;
            }
            Dress(slot, id);
        }

        private void Dress(string slot, string id)
        {
            Agent me = Mission.MainAgent;
            if (me == null || !me.IsActive()) return;
            ItemObject item = Item(id);
            EquipmentIndex index = Equipment.GetEquipmentIndexFromOldEquipmentIndexName(slot);
            var element = item != null ? new EquipmentElement(item, Modifier(id, item)) : EquipmentElement.Invalid;
            if (Array.IndexOf(WeaponSlots, slot) >= 0)
            {
                if (!me.Equipment[index].IsEmpty) me.RemoveEquippedWeapon(index);
                if (item == null) return;
                var weapon = new MissionWeapon(item, element.ItemModifier, null);
                me.EquipWeaponWithNewEntity(index, ref weapon);
                me.TryToWieldWeaponInSlot(index, Agent.WeaponWieldActionType.WithAnimation, false);
                return;
            }
            Equipment body = me.SpawnEquipment.Clone();
            body[index] = element;
            me.UpdateSpawnEquipmentAndRefreshVisuals(body);
        }

        private void Done()
        {
            if (_done) return;
            _done = true;
            BridgeIo.Log("arsenal: done, loadout " + string.Join(", ", Loadout().Select(p => $"{p.Key}={p.Value}")));
            Mission.EndMission();
        }

        // ---- texts ---------------------------------------------------------------------------------------------------

        private ItemModifier Modifier(string id, ItemObject item) =>
            _items.TryGetValue(id, out ArsenalItem a) && Enum.TryParse(a.Quality ?? "", out ItemQuality q) ? HeroFactory.ModifierOf(item, q) : null;

        private static ItemObject Item(string id) => string.IsNullOrEmpty(id) ? null : MBObjectManager.Instance.GetObject<ItemObject>(id);

        private static string Name(ItemObject item) => item?.Name?.ToString() ?? "";

        /// <summary>The numbers a player picks a weapon or armour by.</summary>
        private string Hint(ItemObject item)
        {
            if (item == null) return "";
            var lines = new List<string> { Name(item), T("Tier ", "Уровень ") + ((int)item.Tier + 1) };
            WeaponComponentData w = item.PrimaryWeapon;
            if (w != null)
            {
                if (w.SwingDamage > 0) lines.Add(T("Swing ", "Рубящий ") + w.SwingDamage);
                if (w.ThrustDamage > 0) lines.Add(T("Thrust ", "Колющий ") + w.ThrustDamage);
                if (w.MissileSpeed > 0) lines.Add(T("Missile speed ", "Скорость снаряда ") + w.MissileSpeed);
                if (w.WeaponLength > 0) lines.Add(T("Length ", "Длина ") + w.WeaponLength);
                if (w.MaxDataValue > 0 && (item.Type == ItemObject.ItemTypeEnum.Shield || item.Type == ItemObject.ItemTypeEnum.Arrows || item.Type == ItemObject.ItemTypeEnum.Bolts))
                    lines.Add((item.Type == ItemObject.ItemTypeEnum.Shield ? T("Hit points ", "Прочность ") : T("Ammo ", "Боезапас ")) + w.MaxDataValue);
            }
            ArmorComponent armor = item.ArmorComponent;
            if (armor != null)
            {
                int total = armor.HeadArmor + armor.BodyArmor + armor.ArmArmor + armor.LegArmor;
                if (total > 0) lines.Add(T("Armour ", "Броня ") + total);
            }
            HorseComponent horse = item.HorseComponent;
            if (horse != null) lines.Add(T("Speed ", "Скорость ") + horse.Speed + T(", charge ", ", натиск ") + horse.ChargeDamage);
            if (item.Weight > 0) lines.Add(T("Weight ", "Вес ") + item.Weight.ToString("0.#"));
            return string.Join("\n", lines);
        }

        private string SlotName(string slot)
        {
            switch (slot)
            {
                case "Item0": return T("Weapon 1", "Оружие 1");
                case "Item1": return T("Weapon 2", "Оружие 2");
                case "Item2": return T("Weapon 3", "Оружие 3");
                case "Item3": return T("Weapon 4", "Оружие 4");
                case "Head": return T("Head", "Голова");
                case "Body": return T("Body", "Тело");
                case "Gloves": return T("Hands", "Руки");
                case "Leg": return T("Legs", "Ноги");
                case "Cape": return T("Cape", "Плащ");
                case "Horse": return T("Horse", "Конь");
                default: return slot;
            }
        }

        private string T(string en, string ru) => _ru ? ru : en;
    }
}
