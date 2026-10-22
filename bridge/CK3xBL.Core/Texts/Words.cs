// Every sentence the bridge writes for the player (PROTOCOL §26): the title and result cards and the texts Bannerlord shows, in
// English or Russian (the language CK3 runs in). Names come from CK3 as they are; Russian phrasing avoids declining them
// ("Разгромите врага: <army>"), since CK3 hands the bridge only the nominative.
using System.Globalization;
using CK3xBL.Core.Ck3;

namespace CK3xBL.Core.Texts;

public class Words
{
    public static readonly Words English = new();
    public static readonly Words Russian = new RussianWords();

    /// <summary>The words of a bridge language ("ru", else English).</summary>
    public static Words For(string? language) => language == Ck3Language.Russian ? Russian : English;

    public virtual string Language => Ck3Language.English;

    public virtual string Number(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
    protected static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ---- the title card -------------------------------------------------------------------------------------------------
    public virtual string DefaultPlace => "the field";
    public virtual string BattleOf(string place) => $"The Battle of {place}";
    public virtual string Village(string county) => $"The village of {county}";
    public virtual string Near(string ground, string county) => $"{ground} near {county}";
    public virtual string Before(string ground, string place) => $"{ground} before {place}";
    public virtual string Fields => "Fields";
    public virtual string Coast => "Coast";
    public virtual string Riverbank => "Riverbank";
    public virtual string RiverCrossing => "River crossing";
    public virtual string Lakeshore => "Lakeshore";

    public virtual string Terrain(string terrain) => terrain switch
    {
        "terraced_hills" => "Terraced hills",
        "desert_mountains" => "Desert mountains",
        "coastal_sea" or "sea" => "Coast",
        _ => Cap(terrain.Replace('_', ' ')),
    };

    /// <summary>Season for display; Bannerlord's "fall" reads as CK3's "Autumn".</summary>
    public virtual string Season(string season) => season switch
    {
        "spring" => "Spring",
        "summer" => "Summer",
        "fall" => "Autumn",
        "winter" => "Winter",
        _ => Cap(season),
    };

    public virtual string When(string season, int year) => year > 0 ? $"{season} {year}" : season;
    public virtual string HostOf(string leader) => $"Host of {leader}";
    public virtual string AttackingHost => "Attacking host";
    public virtual string DefendingHost => "Defending host";
    public virtual string EnemyHost => "the enemy host";
    /// <summary>A CK3 army name as the object of a sentence ("the Raiders of Hepung"); <paramref name="name"/> is already cleaned.</summary>
    public virtual string ArmyObject(string name) =>
        name.Contains("'s ", StringComparison.Ordinal) || name.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? name : "the " + name;
    public virtual string Against(string own, string enemy) => $"{own} against {enemy}";
    public virtual string Rout(string army) => "Rout " + army;
    public virtual string HoldSiegeLines(string place) => $"Hold the siege lines before {place}";
    public virtual string BreakSiege(string place) => $"Break the siege of {place}";

    public virtual string Reinforcements => "Reinforcements arrive!";
    public virtual string FreshTroopsJoin(string host) => $"Fresh troops join the {host}";
    public virtual string BringsFreshTroops(string commander, string host) => $"{commander} brings fresh troops to the {host}";
    public virtual string Joins(string who, string host) => $"{who} joins the {host}";

    public virtual string SingleCombat => "Single Combat";
    /// <summary>The title of a tournament bout (PROTOCOL §29): 1 single combat, 2 wrestling, 3 grand melee.</summary>
    public virtual string Tourney(int kind) => kind switch { 2 => "Tournament: Wrestling", 3 => "Tournament: Grand Melee", 4 => "Tournament: Joust", 5 => "Tournament: Archery", _ => "Tournament: Single Combat" };
    public virtual string HitTargets(int par, int total) => $"Hit {par} of the {total} targets";
    public virtual string WinTheMelee => "Leave the other team in the dust";
    public virtual string You => "You";
    public virtual string YourFoe => "your foe";
    public virtual string DefeatFoe(string foe) => $"Defeat {foe}";
    public virtual string TheBand => "the band";

    /// <summary>Title and objective of a skirmish on the road by kind (PROTOCOL §20).</summary>
    public virtual (string Title, string Objective) Skirmish(int kind, string foe) => kind switch
    {
        2 => ("Deserters on the Road", $"Drive off {foe} and the deserters"),
        3 => ("An Assassin Strikes", $"Cut down {foe}"),
        4 => ("Soldiers on the Road", $"Break free of {foe}'s soldiers"),
        5 => ("Zealots on the Road", $"Drive off {foe} and the zealots"),
        _ => ("Bandits on the Road", $"Drive off {foe} and the bandits"),
    };

    /// <summary>An assault on a besieged holding (PROTOCOL §39).</summary>
    public virtual string Assault(string place) => $"The Assault on {place}";
    public virtual string TakeTheWalls => "Take the walls";
    public virtual string Landing(string place) => $"The Landing at {place}";
    public virtual string SeaBattle(string place) => $"The Sea Battle off {place}";
    public virtual string HoldTheWalls(string place) => $"The Defence of {place}";
    public virtual string DefendTheWalls => "Hold the walls";
    public virtual string StrikeTheCamp(string place) => $"The Relief of {place}";
    public virtual string BreakTheSiege => "Break the besiegers' camp";
    public virtual string BrokeIn(string lost) => $"{lost} fell breaking into the holding.";

    /// <summary>Title and objective of a skirmish at a band's camp (PROTOCOL §37).</summary>
    public virtual (string Title, string Objective) Hideout(int kind, string foe) =>
        kind == 2 ? ("The Deserters' Camp", $"Storm the camp of {foe} and the deserters") : ("The Bandits' Lair", $"Storm the lair of {foe} and the bandits");

    // ---- the outcome ----------------------------------------------------------------------------------------------------
    public virtual string DuelSlew(string foe) => $"You slew {foe} in single combat.";
    public virtual string DuelDefeated(string foe) => $"You defeated {foe} in single combat.";
    public virtual string DuelStruckDown(string foe) => $"{foe} struck you down in single combat.";
    public virtual string DuelBested(string foe) => $"{foe} bested you in single combat.";
    public virtual string DuelUndecided(string foe) => $"The single combat with {foe} will be decided in CK3.";
    public virtual string BattleUndecided(string place) => $"The Battle of {place} will be decided in CK3.";

    public virtual string SkirmishBrokenOff => "The fight on the road was broken off.";
    public virtual string SkirmishWon(string enemyLost, string myLost) => $"The road is clear: {enemyLost} of the band fell for {myLost} of your company.";
    public virtual string SkirmishLost(string myLost, string enemyLost) => $"Beaten on the road: {myLost} of your company fell, and {enemyLost} of the band.";

    public virtual string BattleBrokenOff(string place, string mine, string theirs) => $"The battle at {place} was broken off: {mine} of your men and {theirs} of the enemy fell.";
    public virtual string BattleWon(string place, string mine, string theirs) => $"Victory at {place}: {theirs} of the enemy fell for {mine} of your men.";
    public virtual string BattleLost(string place, string mine, string theirs) => $"Defeat at {place}: {mine} of your men fell, and {theirs} of the enemy.";
    public virtual string OwnKills(int kills) => kills == 1 ? " You struck down one foe yourself." : $" You struck down {Number(kills)} foes yourself.";
    public virtual string OverClashes(int clashes, string mine, string theirs) =>
        $" Over {(clashes <= 2 ? "both clashes" : $"all {clashes} clashes")}: {mine} of your men and {theirs} of the enemy.";

    public virtual string Prestige(int n) => $"+{n} prestige";
    public virtual string Glory(int n) => $"+{n} glory";
    public virtual string ProwessChances(int n) => n == 1 ? "a chance at prowess" : $"{n} chances at prowess";
    public virtual string Renown(string parts) => $"Renown: {parts}.";

    // ---- the result card ------------------------------------------------------------------------------------------------
    public virtual string CardBrokenOff(string place) => $"The fighting at {place} is broken off";
    public virtual string CardVictory(string place) => $"Victory at {place}";
    public virtual string CardDefeat(string place) => $"Defeat at {place}";
    public virtual string CardLosses(int mine, int theirs) => $"Our losses {Number(mine)} · Enemy losses {Number(theirs)}";
    /// <summary>The option of the mod's event 0002 the player clicks, as the mod names it in this language.</summary>
    public virtual string DustSettlesOption => "The dust settles...";
    public virtual string BattleContinues => $"The battle continues in Crusader Kings III. Click '{DustSettlesOption}', then resume the game.";
    public virtual string ClickInCk3 => $"Click '{DustSettlesOption}' in Crusader Kings III";
    public virtual string ClickToContinue => "Click to continue to the battle";
}

/// <summary>Russian: the words of ck3_mod's russian localization where the mod has them (the event option).</summary>
public sealed class RussianWords : Words
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public override string Language => Ck3Language.Russian;
    public override string Number(int n) => n.ToString("N0", Ru).Replace(' ', ' ');

    public override string DefaultPlace => "поле боя";
    public override string BattleOf(string place) => $"Битва в провинции {place}";
    public override string Village(string county) => $"Деревня, графство {county}";
    public override string Near(string ground, string county) => $"{ground}, графство {county}";
    public override string Before(string ground, string place) => $"{ground} под стенами, провинция {place}";
    public override string Fields => "Поля";
    public override string Coast => "Побережье";
    public override string Riverbank => "Берег реки";
    public override string RiverCrossing => "Переправа";
    public override string Lakeshore => "Берег озера";

    public override string Terrain(string terrain) => terrain switch
    {
        "plains" => "Равнины",
        "farmlands" => "Пашни",
        "hills" => "Холмы",
        "terraced_hills" => "Террасные холмы",
        "mountains" => "Горы",
        "desert" => "Пустыня",
        "desert_mountains" => "Пустынные горы",
        "oasis" => "Оазис",
        "jungle" => "Джунгли",
        "forest" => "Лес",
        "taiga" => "Тайга",
        "wetlands" => "Болота",
        "steppe" => "Степь",
        "floodplains" => "Пойма",
        "drylands" => "Засушливые земли",
        "coastal_sea" or "sea" => "Побережье",
        _ => base.Terrain(terrain),
    };

    public override string Season(string season) => season switch
    {
        "spring" => "Весна",
        "summer" => "Лето",
        "fall" => "Осень",
        "winter" => "Зима",
        _ => Cap(season),
    };

    public override string When(string season, int year) => year > 0 ? $"{season} {year} г." : season;
    public override string HostOf(string leader) => $"Войско: {leader}";
    public override string AttackingHost => "Наступающее войско";
    public override string DefendingHost => "Обороняющееся войско";
    public override string EnemyHost => "вражеское войско";
    public override string ArmyObject(string name) => name;
    public override string Against(string own, string enemy) => $"{own} против {enemy}";
    public override string Rout(string army) => $"Разгромите врага: {army}";
    public override string HoldSiegeLines(string place) => $"Удержите осадные позиции: {place}";
    public override string BreakSiege(string place) => $"Снимите осаду: {place}";

    public override string Reinforcements => "Подходят подкрепления!";
    public override string FreshTroopsJoin(string host) => $"Свежие силы на помощь — {host}";
    public override string BringsFreshTroops(string commander, string host) => $"{commander} ведёт подкрепление — {host}";
    public override string Joins(string who, string host) => $"{who} вступает в бой — {host}";

    public override string SingleCombat => "Поединок";
    public override string Tourney(int kind) => kind switch { 2 => "Турнир: борьба", 3 => "Турнир: общая схватка", 4 => "Турнир: джауст", 5 => "Турнир: стрельба из лука", _ => "Турнир: поединок" };
    public override string HitTargets(int par, int total) => $"Поразите {par} мишеней из {total}";
    public override string WinTheMelee => "Повергните команду соперников";
    public override string You => "Вы";
    public override string YourFoe => "противник";
    public override string DefeatFoe(string foe) => $"Одолейте противника: {foe}";
    public override string TheBand => "банда";

    public override (string Title, string Objective) Skirmish(int kind, string foe) => kind switch
    {
        2 => ("Дезертиры на дороге", $"Прогоните дезертиров: {foe}"),
        3 => ("Нападение убийцы", $"Сразите убийцу: {foe}"),
        4 => ("Солдаты на дороге", $"Прорвитесь сквозь солдат: {foe}"),
        5 => ("Фанатики на дороге", $"Прогоните фанатиков: {foe}"),
        _ => ("Разбойники на дороге", $"Прогоните разбойников: {foe}"),
    };

    public override string Assault(string place) => $"Штурм: {place}";
    public override string TakeTheWalls => "Возьмите стены";
    public override string Landing(string place) => $"Высадка: {place}";
    public override string SeaBattle(string place) => $"Морской бой: {place}";
    public override string HoldTheWalls(string place) => $"Оборона: {place}";
    public override string DefendTheWalls => "Удержите стены";
    public override string StrikeTheCamp(string place) => $"Деблокада: {place}";
    public override string BreakTheSiege => "Разгромите лагерь осаждающих";
    public override string BrokeIn(string lost) => $"При прорыве в крепость пало: {lost}.";

    public override (string Title, string Objective) Hideout(int kind, string foe) =>
        kind == 2 ? ("Лагерь дезертиров", $"Возьмите лагерь дезертиров: {foe}") : ("Логово разбойников", $"Возьмите логово разбойников: {foe}");

    public override string DuelSlew(string foe) => $"Вы сразили в поединке: {foe}.";
    public override string DuelDefeated(string foe) => $"Вы победили в поединке: {foe}.";
    public override string DuelStruckDown(string foe) => $"Вы пали в поединке: {foe}.";
    public override string DuelBested(string foe) => $"Вы проиграли поединок: {foe}.";
    public override string DuelUndecided(string foe) => $"Исход поединка ({foe}) решит CK3.";
    public override string BattleUndecided(string place) => $"Исход битвы ({place}) решит CK3.";

    public override string SkirmishBrokenOff => "Стычка в пути прервана.";
    public override string SkirmishWon(string enemyLost, string myLost) => $"Путь свободен: банда потеряла {enemyLost}, ваш отряд — {myLost}.";
    public override string SkirmishLost(string myLost, string enemyLost) => $"Поражение в пути: ваш отряд потерял {myLost}, банда — {enemyLost}.";

    public override string BattleBrokenOff(string place, string mine, string theirs) => $"Бой прерван ({place}): наши потери {mine}, потери врага {theirs}.";
    public override string BattleWon(string place, string mine, string theirs) => $"Победа ({place}): враг потерял {theirs}, мы — {mine}.";
    public override string BattleLost(string place, string mine, string theirs) => $"Поражение ({place}): мы потеряли {mine}, враг — {theirs}.";
    public override string OwnKills(int kills) => $" Сражено вами лично: {Number(kills)}.";
    public override string OverClashes(int clashes, string mine, string theirs) => $" За все схватки битвы ({clashes}): наши потери {mine}, потери врага {theirs}.";

    public override string Prestige(int n) => $"+{n} престижа";
    public override string Glory(int n) => $"+{n} славы";
    public override string ProwessChances(int n) => n == 1 ? "шанс повысить доблесть" : $"шансов повысить доблесть: {n}";
    public override string Renown(string parts) => $"Слава: {parts}.";

    public override string CardBrokenOff(string place) => $"Бой прерван: {place}";
    public override string CardVictory(string place) => $"Победа: {place}";
    public override string CardDefeat(string place) => $"Поражение: {place}";
    public override string CardLosses(int mine, int theirs) => $"Наши потери {Number(mine)} · Потери врага {Number(theirs)}";
    public override string DustSettlesOption => "Пыль оседает...";
    public override string BattleContinues => $"Битва продолжится в Crusader Kings III. Нажмите «{DustSettlesOption}» и снимите игру с паузы.";
    public override string ClickInCk3 => $"Нажмите «{DustSettlesOption}» в Crusader Kings III";
    public override string ClickToContinue => "Нажмите, чтобы перейти к битве";
}
