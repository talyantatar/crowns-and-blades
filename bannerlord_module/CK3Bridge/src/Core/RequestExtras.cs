using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace CK3Bridge
{
    /// <summary>
    /// Optional request.json fields that are not (yet) part of shared/Protocol.cs. They are read from the raw JSON
    /// tree so the shared DTOs stay untouched and old or new bridges keep working (PROTOCOL.md, "Additions: Bannerlord
    /// module stage 1"):
    ///  - location.atmosphere.{atmospherePreset, rain, snow, fogDensity}, location.battlefield.sceneLevels;
    ///  - casualties.{troopDeathChance, heroDeathChance} and attacker/defender.heroes[].deathChance;
    ///  - whether scale.battleSize was sent at all (the shared DTO defaults it to 500, which would hide its absence);
    ///  - whether presentation.preload was sent as true: the shared DTO defaults it to true, so a request without it (or
    ///    without presentation, as protocol 1 requests are) would otherwise be held until a "go" that never comes.
    /// </summary>
    public sealed class RequestExtras
    {
        /// <summary>CK3 converts 0.3 of main-phase casualties to fatal (CK3 define BASE_RATIO_CASUALTIES_CONVERSION).</summary>
        public const float DefaultTroopDeathChance = 0.3f;

        /// <summary>Upper bound of the knight rule clamp(0.30 - prowess/100, 0.05, 0.30).</summary>
        public const float DefaultHeroDeathChance = 0.3f;

        public string AtmospherePreset;
        public float? Rain;
        public float? Snow;
        public float? FogDensity;
        public string SceneLevels;
        public int? BattleSize;

        /// <summary>Hold the loaded battle until "go": only when the request says presentation.preload = true.</summary>
        public bool Preload;
        public float TroopDeathChance = DefaultTroopDeathChance;
        public float HeroDeathChance = DefaultHeroDeathChance;
        public readonly Dictionary<string, float> HeroDeathChanceByKey = new Dictionary<string, float>(StringComparer.Ordinal);

        public static RequestExtras From(JObject root)
        {
            var extras = new RequestExtras();
            if (root == null) return extras;

            extras.AtmospherePreset = ReadString(root.SelectToken("location.atmosphere.atmospherePreset"));
            extras.Rain = ReadFloat(root.SelectToken("location.atmosphere.rain"));
            extras.Snow = ReadFloat(root.SelectToken("location.atmosphere.snow"));
            extras.FogDensity = ReadFloat(root.SelectToken("location.atmosphere.fogDensity"));
            extras.SceneLevels = ReadString(root.SelectToken("location.battlefield.sceneLevels"));
            float? battleSize = ReadFloat(root.SelectToken("scale.battleSize"));
            if (battleSize.HasValue && battleSize.Value >= 1f) extras.BattleSize = (int)Math.Round(battleSize.Value);
            extras.Preload = ReadBool(root.SelectToken("presentation.preload")) ?? false;

            float? troop = ReadFloat(root.SelectToken("casualties.troopDeathChance"));
            float? hero = ReadFloat(root.SelectToken("casualties.heroDeathChance"));
            if (troop.HasValue) extras.TroopDeathChance = RequestRules.Probability(troop.Value, DefaultTroopDeathChance);
            if (hero.HasValue) extras.HeroDeathChance = RequestRules.Probability(hero.Value, DefaultHeroDeathChance);

            foreach (string side in new[] { "attacker", "defender" })
            {
                if (!(root.SelectToken(side + ".heroes") is JArray heroes)) continue;
                foreach (JToken heroToken in heroes)
                {
                    string key = ReadString(heroToken["key"]);
                    float? chance = ReadFloat(heroToken["deathChance"]);
                    if (key != null && chance.HasValue)
                        extras.HeroDeathChanceByKey[key] = RequestRules.Probability(chance.Value, extras.HeroDeathChance);
                }
            }
            return extras;
        }

        public float DeathChanceForHero(string key) =>
            key != null && HeroDeathChanceByKey.TryGetValue(key, out float chance) ? chance : HeroDeathChance;

        private static string ReadString(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            string value = token.Type == JTokenType.String ? (string)token : token.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static bool? ReadBool(JToken token)
        {
            if (token == null) return null;
            switch (token.Type)
            {
                case JTokenType.Boolean:
                    return token.Value<bool>();
                case JTokenType.String:
                    return bool.TryParse((string)token, out bool parsed) ? parsed : (bool?)null;
                default:
                    return null;
            }
        }

        private static float? ReadFloat(JToken token)
        {
            if (token == null) return null;
            switch (token.Type)
            {
                case JTokenType.Float:
                case JTokenType.Integer:
                    return token.Value<float>();
                case JTokenType.String:
                    return float.TryParse((string)token, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                        ? parsed
                        : (float?)null;
                default:
                    return null;
            }
        }
    }
}
