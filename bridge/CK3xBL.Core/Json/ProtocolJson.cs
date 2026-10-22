// JSON conventions shared by every file the bridge writes or reads (PROTOCOL header and §4): UTF-8 without BOM, camelCase keys,
// public fields included, dictionary keys verbatim (troop groups, skill names and morph ids are case-sensitive data), atomic writes.
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CK3xBL.Core.Json;

public static class ProtocolJson
{
    /// <summary>For protocol files exchanged with the Bannerlord module.</summary>
    public static readonly JsonSerializerOptions Options = Create(indented: false);

    /// <summary>For human-read files (config, archive).</summary>
    public static readonly JsonSerializerOptions Indented = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,       // the module's Newtonsoft output and hand-edited config
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        WriteIndented = indented,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        // Names are written as-is ("Luó", "Hepung's"); the files are never embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { SkipComputedProperties } },
    };

    /// <summary>Drops C# properties without a setter (computed helpers such as BattleSnapshot.Sides / PlayerSide / RequestId).
    /// Data is carried in fields (PROTOCOL §4), which are unaffected.</summary>
    private static void SkipComputedProperties(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object) return;
        for (int i = info.Properties.Count - 1; i >= 0; i--)
            if (info.Properties[i] is { Set: null, AttributeProvider: System.Reflection.PropertyInfo }) info.Properties.RemoveAt(i);
    }

    public static string Serialize<T>(T value, bool indented = false) =>
        JsonSerializer.Serialize(value, indented ? Indented : Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Reads and parses a JSON file another process may be replacing; null when missing, locked or not (yet) valid.</summary>
    public static T? TryRead<T>(string path) where T : class
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<T>(text, Options);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    public static void WriteAtomic<T>(string path, T value, bool indented = false) =>
        AtomicFile.WriteAllText(path, Serialize(value, indented), withBom: false);
}

public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly UTF8Encoding Utf8Bom = new(true);

    /// <summary>Writes <c>path.tmp</c>, then renames it over <c>path</c>, so readers never see a partial file.
    /// Retries briefly when the reader holds the target open without delete sharing.</summary>
    public static void WriteAllText(string path, string text, bool withBom)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, withBom ? Utf8Bom : Utf8NoBom);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < 20)
            {
                Thread.Sleep(25);
            }
        }
    }
}
