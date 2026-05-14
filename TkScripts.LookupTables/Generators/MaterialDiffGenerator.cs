using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BfresLibrary;
using Kokuban;
using TkScripts.LookupTables.MeshCodec;
using TotkCommon.Extensions;

namespace TkScripts.LookupTables.Generators;

public sealed class MaterialDiffGenerator : IGenerator
{
    private const string OptionPreNormal  = "o_expression_pre_normal";
    private const string OptionPostNormal = "o_expression_post_normal";
    private const string SkeletonBfresMcSuffix = "Skeleton.bfres.mc";

    private static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new CompactVersionMapListConverter() },
    };

    public IEnumerable<object> Tags { get; } = ["MaterialDiff"];
    public string NameFormat => "{0}.json";

    private Dictionary<string, List<SortedDictionary<int, long>>>? _result;

    public async Task<object?> Generate(string[] gamePaths)
    {
        string[] ordered = [.. gamePaths
            .Select(p => (path: p, v: p.GetRomfsVersionOrDefault()))
            .OrderBy(x => x.v)
            .Select(x => x.path)];

        List<(int Version, Dictionary<MaterialKey, MaterialOptionValues> Snapshot)> snapshots = [];
        int? lastShaderVersion = null;

        foreach (var romfs in ordered) {
            var shaderVersion = TryGetShaderProductVersion(romfs);
            if (shaderVersion is null) {
                Console.WriteLine(Chalk.BrightYellow + $"No material.Product.* in Shader folder, skipping: {romfs}");
                continue;
            }

            var zsDic = Path.Combine(romfs, "Pack", "ZsDic.pack.zs");
            if (!File.Exists(zsDic)) {
                Console.WriteLine(Chalk.BrightYellow + $"ZsDic.pack.zs not found, skipping: {romfs}");
                continue;
            }

            if (shaderVersion == lastShaderVersion) {
                Console.WriteLine(Chalk.BrightYellow + $"Shader version {shaderVersion} unchanged, skipping: {romfs}");
                continue;
            }

            Console.WriteLine($"Scanning: {romfs}  (shaders v{shaderVersion})");

            snapshots.Add((shaderVersion.Value, await Task.Run(() => CollectMaterialSnapshots(romfs, zsDic))));
            lastShaderVersion = shaderVersion;
        }

        _result = BuildOutput(snapshots);
        Console.WriteLine($"  {_result.Values.Sum(d => d.Count):N0} changed value groups across all options.");

        return null;
    }

    public void WriteBinary(Stream output, object tag) =>
        JsonSerializer.Serialize(output, _result, JsonOptions);

    private static int? TryGetShaderProductVersion(string romfsRoot)
    {
        var shaderDir = Path.Combine(romfsRoot, "Shader");
        if (!Directory.Exists(shaderDir)) {
            return null;
        }

        foreach (var path in Directory.EnumerateFiles(shaderDir)) {
            var name = Path.GetFileName(path);
            const string prefix = "material.Product.";
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var rest = name.AsSpan(prefix.Length);
            if (rest.Length >= 3 && int.TryParse(rest[..3], out var v)) {
                return v;
            }
        }

        return null;
    }

    private static Dictionary<MaterialKey, MaterialOptionValues> CollectMaterialSnapshots(
        string romfsRoot,
        string zsDicPackPath)
    {
        var paths = Directory.GetFiles(romfsRoot, "*.bfres.mc", SearchOption.AllDirectories)
            .Where(static p => !Path.GetFileName(p).EndsWith(SkeletonBfresMcSuffix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var total = paths.Length;

        if (total == 0) {
            return new Dictionary<MaterialKey, MaterialOptionValues>();
        }

        Console.WriteLine($"  {total:N0} files to scan.");

        McDecompressor.LoadDictionaries(zsDicPackPath);

        if (StringCache.Strings.Count == 0) {
            PrimeStringCacheForScan(paths);
        }

        Dictionary<MaterialKey, MaterialOptionValues> acc = new();
        var done = 0;
        var errors = 0;
        var reportInterval = Math.Max(500, Math.Min(total, Math.Max(1, total / 20)));

        foreach (var filePath in paths) {
            try {
                ReadOneBfres(romfsRoot, filePath, acc);
            }
            catch (Exception ex) {
                errors++;
                Console.WriteLine(Chalk.BrightYellow + $"  Warning: {Path.GetFileName(filePath)}: {ex.Message}");
            }

            done++;
            if (done % reportInterval == 0) {
                Console.WriteLine($"  {done:N0}/{total:N0}  ({100.0 * done / total:0.#}%)  matched: {acc.Count:N0}");
            }
        }

        Console.WriteLine($"  Done — {total:N0} files, {acc.Count:N0} materials with tracked expression options." +
            (errors > 0 ? Chalk.BrightYellow + $"  ({errors:N0} file error(s))" : ""));

        return acc;
    }

    private static void PrimeStringCacheForScan(string[] bfresMcPaths)
    {
        const byte holdsExternalStringsFlag = 4;
        const int flagByteOffset = 0xEE;

        foreach (var path in bfresMcPaths) {
            byte[] raw;
            try {
                raw = File.ReadAllBytes(path);
            }
            catch {
                continue;
            }

            if (!McDecompressor.IsMeshCodec(raw)) {
                continue;
            }

            byte[] bfres;
            try {
                bfres = McDecompressor.DecompressToBfres(raw);
            }
            catch (InvalidDataException) {
                continue;
            }

            if (bfres.Length <= flagByteOffset || !McDecompressor.IsLikelySwitchBfres(bfres)) {
                continue;
            }

            if ((bfres[flagByteOffset] & holdsExternalStringsFlag) == 0) {
                continue;
            }

            using MemoryStream ms = new(bfres, writable: false);
            _ = new ResFile(ms, leaveOpen: true);

            return;
        }
    }

    private static void ReadOneBfres(
        string romfsRoot,
        string filePath,
        Dictionary<MaterialKey, MaterialOptionValues> acc)
    {
        var raw  = File.ReadAllBytes(filePath);
        var data = McDecompressor.DecompressToBfres(raw);

        using MemoryStream ms = new(data, writable: false);
        var resFile = new ResFile(ms, leaveOpen: true);

        if (resFile.Models is null || resFile.Models.Count == 0) {
            return;
        }

        var rel = Path.GetRelativePath(romfsRoot, filePath).Replace('\\', '/');

        foreach (var model in resFile.Models.Values) {
            foreach (var mat in model.Materials.Values) {
                if (!TryExtractTrackedOptions(mat, out var vals)) {
                    continue;
                }

                acc[new MaterialKey(rel, model.Name, mat.Name)] = vals;
            }
        }
    }

    private static bool TryExtractTrackedOptions(Material mat, out MaterialOptionValues values)
    {
        values = default;
        var any = false;

        if (TryFindOption(mat, OptionPreNormal, out var pre)) {
            values.PreNormal = pre;
            any = true;
        }

        if (TryFindOption(mat, OptionPostNormal, out var post)) {
            values.PostNormal = post;
            any = true;
        }

        return any;
    }

    private static bool TryFindOption(Material mat, string optionName, out long parsed)
    {
        parsed = 0;

        var opts = mat.ShaderAssign?.ShaderOptions;
        if (opts is not { Count: > 0 }) {
            return false;
        }

        foreach (var kv in opts) {
            if (!string.Equals(kv.Key, optionName, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var s = kv.Value is { } rs ? rs.String : null;
            return TryParseShaderOptionNumber(s, out parsed);
        }

        return false;
    }

    private static bool TryParseShaderOptionNumber(string? s, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) {
            return false;
        }

        s = s.Trim();
        if (s.Equals("<Default Value>", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        if (s.Equals("True", StringComparison.OrdinalIgnoreCase)) {
            value = 1;
            return true;
        }

        if (s.Equals("False", StringComparison.OrdinalIgnoreCase)) {
            value = 0;
            return true;
        }

        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ulong.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hx) &&
            hx <= long.MaxValue) {
            value = (long)hx;
            return true;
        }

        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) {
            return true;
        }

        if (ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ud) && ud <= long.MaxValue) {
            value = (long)ud;
            return true;
        }

        return false;
    }

    private static Dictionary<string, List<SortedDictionary<int, long>>> BuildOutput(
        List<(int Version, Dictionary<MaterialKey, MaterialOptionValues> Snapshot)> snapshots)
    {
        Dictionary<string, List<SortedDictionary<int, long>>> result = new(StringComparer.Ordinal);
        Dictionary<string, HashSet<string>> seen = new(StringComparer.Ordinal);

        var allKeys = snapshots.SelectMany(s => s.Snapshot.Keys).Distinct();

        foreach (var matKey in allKeys) {
            Collect(matKey, OptionPreNormal,  static v => v.PreNormal);
            Collect(matKey, OptionPostNormal, static v => v.PostNormal);
        }

        return result;

        void Collect(MaterialKey matKey, string option, Func<MaterialOptionValues, long?> get)
        {
            var byVersion = snapshots
                .Select(s => (s.Version, Val: s.Snapshot.TryGetValue(matKey, out var mv) ? get(mv) : null))
                .Where(x => x.Val.HasValue)
                .Select(x => (x.Version, x.Val!.Value))
                .ToList();

            if (byVersion.Count == 0) {
                return;
            }

            if (byVersion.Select(x => x.Value).Distinct().Count() == 1) {
                return;
            }

            var vMap = new SortedDictionary<int, long>();
            foreach (var entry in byVersion) {
                vMap[entry.Version] = entry.Value;
            }

            var dedupeKey = string.Join(";", vMap.Select(e => $"{e.Key}:{e.Value}"));
            if (!seen.TryGetValue(option, out var seenSet)) {
                seenSet = new HashSet<string>(StringComparer.Ordinal);
                seen[option] = seenSet;
            }

            if (!seenSet.Add(dedupeKey)) {
                return;
            }

            if (!result.TryGetValue(option, out var optList)) {
                optList = [];
                result[option] = optList;
            }

            optList.Add(vMap);
        }
    }

    private readonly struct MaterialKey(string bfresRelativePath, string modelName, string materialName)
        : IEquatable<MaterialKey>
    {
        private string BfresRelativePath { get; } = bfresRelativePath;
        private string ModelName { get; } = modelName;
        private string MaterialName { get; } = materialName;

        public bool Equals(MaterialKey other) =>
            BfresRelativePath == other.BfresRelativePath &&
            ModelName == other.ModelName &&
            MaterialName == other.MaterialName;

        public override bool Equals(object? obj) => obj is MaterialKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(BfresRelativePath, ModelName, MaterialName);
    }

    private struct MaterialOptionValues
    {
        public long? PreNormal;
        public long? PostNormal;
    }

    private sealed class CompactVersionMapListConverter : JsonConverter<List<SortedDictionary<int, long>>>
    {
        public override List<SortedDictionary<int, long>>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var list = new List<SortedDictionary<int, long>>();
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) {
                if (reader.TokenType != JsonTokenType.StartObject) continue;
                var dict = new SortedDictionary<int, long>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject) {
                    var key = int.Parse(reader.GetString()!, CultureInfo.InvariantCulture);
                    reader.Read();
                    dict[key] = reader.GetInt64();
                }
                list.Add(dict);
            }
            return list;
        }

        public override void Write(Utf8JsonWriter writer, List<SortedDictionary<int, long>> value, JsonSerializerOptions options)
        {
            var depth = writer.CurrentDepth;
            var outerIndent = new string(' ', depth * 2);
            var innerIndent = new string(' ', (depth + 1) * 2);

            var sb = new System.Text.StringBuilder("[");
            for (var i = 0; i < value.Count; i++) {
                sb.Append('\n').Append(innerIndent);
                sb.Append("{ ");
                sb.Append(string.Join(", ", value[i].Select(e => $"\"{e.Key}\": {e.Value}")));
                sb.Append(i < value.Count - 1 ? " }," : " }");
            }
            if (value.Count > 0) sb.Append('\n').Append(outerIndent);
            sb.Append(']');

            writer.WriteRawValue(sb.ToString(), skipInputValidation: true);
        }
    }
}
