using System;
using System.Globalization;
using System.Xml;
using CK3xBL.Protocol;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace CK3Bridge
{
    /// <summary>
    /// Builds a hero's BodyProperties from the bridge's AppearanceSpec with the native FaceGen wrappers
    /// (MBBodyProperties.SetBody/SetPigmentation are no-ops in v1.4.8, so FaceGenerationParams fields are set directly). These calls work outside missions
    /// (CoreManaged, BodyGeneratorView), so faces are built in the idle state.
    /// </summary>
    internal static class CK3Appearance
    {
        private const int HumanRace = 0;

        /// <summary>The hero's body. <c>seed</c> is stable per CK3 character, so stand-in faces never change between battles.</summary>
        public static BodyProperties Build(AppearanceSpec spec, bool female, int age, string cultureId, int seed)
        {
            if (spec != null && !string.IsNullOrWhiteSpace(spec.BodyProperties))
            {
                if (TryParseBodyProperties(spec.BodyProperties, age, out BodyProperties raw)) return raw;
                BridgeIo.Log("appearance.bodyProperties is not a valid <BodyProperties .../> element; building the face instead");
            }
            if (spec == null || string.Equals(spec.Source, "stock", StringComparison.OrdinalIgnoreCase))
                return StandIn(spec, female, age, cultureId, seed);
            return FromSpec(spec, female, age, seed);
        }

        /// <summary>Neutral face for race/gender/age, CK3 morphs around the runtime neutral weights, then encode.</summary>
        private static BodyProperties FromSpec(AppearanceSpec spec, bool female, int age, int seed)
        {
            int gender = female ? 1 : 0;
            BodyProperties neutral = BodyProperties.Default;
            MBBodyProperties.ProduceNumericKeyWithDefaultValues(ref neutral, false, false, HumanRace, gender, age);

            FaceGenerationParams p = FaceGenerationParams.Create();
            p.CurrentRace = HumanRace;
            p.CurrentGender = gender;
            p.CurrentAge = age;
            MBBodyProperties.GetParamsFromKey(ref p, neutral, false, false);
            p.SetRaceGenderAndAdjustParams(HumanRace, gender, age);

            // Morphs are addressed by deform-key id, never by index (female index 47 is "mouth", skins.xml).
            float[] w0 = (float[])p.KeyWeights.Clone();
            int keyCount = MBBodyProperties.GetFaceGenInstancesLength(HumanRace, gender, age);
            if (spec.Morphs != null && spec.Morphs.Count > 0)
            {
                for (int i = 0; i < keyCount && i < p.KeyWeights.Length; i++)
                {
                    DeformKeyData key = MBBodyProperties.GetDeformKeyData(i, HumanRace, gender, age);
                    if (key.Id == null || !spec.Morphs.TryGetValue(key.Id, out float s)) continue;
                    s = MBMath.ClampFloat(s, -1f, 1f);
                    p.KeyWeights[i] = s < 0f ? w0[i] * (1f + s) : w0[i] + (1f - w0[i]) * s;
                }
            }

            int hairCount = 0, beardCount = 0, faceTextureCount = 0, mouthTextureCount = 0, tattooCount = 0, voiceCount = 0, eyebrowCount = 0;
            float scale = 0f;
            MBBodyProperties.GetParamsMax(HumanRace, gender, age, ref hairCount, ref beardCount, ref faceTextureCount, ref mouthTextureCount,
                ref tattooCount, ref voiceCount, ref eyebrowCount, ref scale);
            p.CurrentHair = PickStyle(spec.Hair, hairCount, p.CurrentHair, seed,
                tag => MBBodyProperties.GetHairIndicesByTag(HumanRace, gender, age, tag));
            p.CurrentBeard = female
                ? 0
                : PickStyle(spec.Beard, beardCount, p.CurrentBeard, seed, tag => MBBodyProperties.GetFacialIndicesByTag(HumanRace, gender, age, tag));

            p.CurrentSkinColorOffset = Clamp01(spec.Skin);
            p.CurrentHairColorOffset = Clamp01(spec.HairColor);
            p.CurrentEyeColorOffset = Clamp01(spec.EyeColor);
            p.HeightMultiplier = Clamp01(spec.Height);
            p.CurrentWeight = Clamp01(spec.Weight);
            p.CurrentBuild = Clamp01(spec.Build);
            p.CurrentAge = age;
            MBBodyProperties.EnforceConstraints(ref p);

            BodyProperties encoded = default;
            MBBodyProperties.ProduceNumericKeyWithParams(p, false, false, ref encoded);
            return new BodyProperties(new DynamicBodyProperties(age, p.CurrentWeight, p.CurrentBuild), encoded.StaticProperties);
        }

        /// <summary>
        /// No likeness data: a stable random face inside the culture's stock body range (sandboxcore_bodyproperties.xml,
        /// loaded in CustomGame), seeded by the CK3 character so it is the same face every battle.
        /// </summary>
        private static BodyProperties StandIn(AppearanceSpec spec, bool female, int age, string cultureId, int seed)
        {
            MBBodyProperty range = FindTemplate(female, cultureId);
            if (range == null) return FromSpec(spec ?? new AppearanceSpec(), female, age, seed);
            BodyProperties random = TaleWorlds.Core.FaceGen.GetRandomBodyProperties(HumanRace, female, range.BodyPropertyMin, range.BodyPropertyMax, 0, seed,
                range.HairTags, range.BeardTags, range.TattooTags, 0f);
            float weight = spec != null ? Clamp01(spec.Weight) : random.Weight;
            float build = spec != null ? Clamp01(spec.Build) : random.Build;
            return new BodyProperties(new DynamicBodyProperties(age, weight, build), random.StaticProperties);
        }

        /// <summary>Last resort when the face generator fails: the lower bound of a stock body range, at the hero's age.</summary>
        public static BodyProperties StockBody(bool female, int age)
        {
            MBBodyProperty range = FindTemplate(female, "vlandia");
            if (range == null) return BodyProperties.Default;
            BodyProperties min = range.BodyPropertyMin;
            return new BodyProperties(new DynamicBodyProperties(age, min.Weight, min.Build), min.StaticProperties);
        }

        private static MBBodyProperty FindTemplate(bool female, string cultureId)
        {
            var candidates = female
                ? new[] { "townswoman_" + cultureId, "townswoman_vlandia" }
                : new[] { "fighter_" + cultureId + "_noble", "fighter_" + cultureId, "fighter_vlandia" };
            foreach (string id in candidates)
            {
                MBBodyProperty range = MBObjectManager.Instance.GetObject<MBBodyProperty>(id);
                if (range != null) return range;
            }
            return null;
        }

        /// <summary>Explicit index wins (bald = 0), then the first tag with matches (seeded pick), then the neutral default.</summary>
        private static int PickStyle(StyleSpec style, int count, int fallback, int seed, Func<string, int[]> indicesByTag)
        {
            if (style != null)
            {
                if (style.Bald) return 0;
                if (style.Index.HasValue && style.Index.Value >= 0 && style.Index.Value < count) return style.Index.Value;
                if (style.Tags != null)
                {
                    foreach (string tag in style.Tags)
                    {
                        if (string.IsNullOrWhiteSpace(tag)) continue;
                        int[] indices = indicesByTag(tag);
                        if (indices != null && indices.Length > 0) return indices[(seed & int.MaxValue) % indices.Length];
                    }
                }
            }
            return MBMath.ClampInt(fallback, 0, Math.Max(0, count - 1));
        }

        /// <summary>
        /// Parses a raw "&lt;BodyProperties version=.. age=.. weight=.. build=.. key=../&gt;" element with the invariant
        /// culture (BodyProperties.FromXmlNode parses floats with the current culture).
        /// </summary>
        public static bool TryParseBodyProperties(string xml, int fallbackAge, out BodyProperties body)
        {
            body = default;
            XmlElement element;
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xml.Trim());
                element = doc.DocumentElement;
            }
            catch (XmlException)
            {
                return false;
            }
            if (element == null || element.Name != "BodyProperties") return false;
            if (!TryParseKey(element.GetAttribute("key"), out StaticBodyProperties key)) return false;
            float age = ParseFloat(element.GetAttribute("age"), fallbackAge);
            float weight = ParseFloat(element.GetAttribute("weight"), 0.5f);
            float build = ParseFloat(element.GetAttribute("build"), 0.5f);
            body = new BodyProperties(new DynamicBodyProperties(age, Clamp01(weight), Clamp01(build)), key);
            return true;
        }

        /// <summary>A static key is exactly 128 hex digits, eight 16-digit parts (StaticBodyProperties).</summary>
        private static bool TryParseKey(string hex, out StaticBodyProperties key)
        {
            key = default;
            if (hex == null || hex.Length != 128) return false;
            var parts = new ulong[8];
            for (int i = 0; i < 8; i++)
                if (!ulong.TryParse(hex.Substring(i * 16, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out parts[i]))
                    return false;
            key = new StaticBodyProperties(parts[0], parts[1], parts[2], parts[3], parts[4], parts[5], parts[6], parts[7]);
            return true;
        }

        private static float ParseFloat(string text, float fallback) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : fallback;

        private static float Clamp01(float value) => float.IsNaN(value) ? 0.5f : MBMath.ClampFloat(value, 0f, 1f);

        /// <summary>Stable seed for a CK3 character: its internal id, else a hash of the hero key.</summary>
        public static int SeedFor(HeroSpec hero)
        {
            if (hero.Ck3Id != 0) return (int)(hero.Ck3Id % int.MaxValue);
            unchecked
            {
                int hash = (int)2166136261;
                foreach (char c in hero.Key ?? "") hash = (hash ^ c) * 16777619;
                return hash & int.MaxValue;
            }
        }
    }
}
