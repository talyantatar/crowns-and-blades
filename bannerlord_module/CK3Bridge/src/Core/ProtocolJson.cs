using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace CK3Bridge
{
    /// <summary>
    /// Newtonsoft settings for the protocol files: camelCase member names, but dictionary keys are kept verbatim.
    /// CamelCasePropertyNamesContractResolver would also camel-case dictionary keys on write, which would corrupt
    /// TroopRequest.group tags (e.g. "Levy#3" -> "levy#3") echoed back in result.json groups (PROTOCOL.md §3.5).
    /// </summary>
    public static class ProtocolJson
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new CamelCaseNamingStrategy(processDictionaryKeys: false, overrideSpecifiedNames: true),
            },
            Formatting = Formatting.Indented,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            NullValueHandling = NullValueHandling.Include,
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Double,
            Culture = CultureInfo.InvariantCulture,
        };

        private static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);
        private static readonly Regex IdPattern = new Regex("\"id\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled);

        public static string Serialize(object value) => JsonConvert.SerializeObject(value, Settings);

        /// <summary>Parses raw JSON into a tree (no date coercion) so optional extension fields can be read.</summary>
        public static JObject ParseObject(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
            {
                return JObject.Load(reader);
            }
        }

        public static T ToObject<T>(JToken token) => token.ToObject<T>(Serializer);

        /// <summary>
        /// The top-level "id" of a request (null when it has none). Text that is not a JSON object falls back to the first
        /// "id" string in it, so even a broken request can be answered under its id.
        /// </summary>
        public static string ReadId(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                JToken id = ParseObject(json)["id"];
                return id != null && id.Type == JTokenType.String ? (string)id : null;
            }
            catch (JsonException)
            {
                Match match = IdPattern.Match(json);
                return match.Success ? match.Groups[1].Value : null;
            }
        }
    }
}
