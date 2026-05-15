using System.Globalization;
using System.Text.Json;
using BfresLibrary;
using Kokuban;
using TkScripts.LookupTables.Converters;
using TkScripts.LookupTables.MeshCodec;
using TotkCommon.Extensions;

namespace TkScripts.LookupTables.Generators;

public sealed class MaterialDiffGenerator : IGenerator
{
    private const string OptionPreNormal  = "o_expression_pre_normal";
    private const string OptionPostNormal = "o_expression_post_normal";
    private const string SkeletonBfresMcSuffix = ".Skeleton.bfres.mc";

    private static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
        Converters = { new CompactJsonConverter() },
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
                Console.WriteLine(Chalk.BrightYellow + $"No material.Product.* file in Shader folder, skipping: {romfs}");
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

            Console.WriteLine($"Scanning: {romfs}  (shaders version {shaderVersion})");

            snapshots.Add((shaderVersion.Value, await Task.Run(() => CollectMaterialSnapshots(romfs, zsDic))));
            lastShaderVersion = shaderVersion;
        }

        _result = BuildOutput(snapshots);
        Console.WriteLine($"  {_result.Values.Sum(d => d.Count):N0} changed values across all options.");

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

    private static Dictionary<MaterialKey, MaterialOptionValues> CollectMaterialSnapshots(string romfsRoot, string zsDicPackPath)
    {
        var paths = Directory.GetFiles(romfsRoot, "*.bfres.mc", SearchOption.AllDirectories)
            .Where(static p => !Path.GetFileName(p).EndsWith(SkeletonBfresMcSuffix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        
        var total = paths.Length;

        if (total == 0) {
            return [];
        }

        Console.WriteLine($"  {total:N0} files to scan.");

        McDecompressor.LoadDictionaries(zsDicPackPath);

        if (StringCache.Strings.Count == 0) {
            LoadStringCache(romfsRoot);
        }

        Dictionary<MaterialKey, MaterialOptionValues> acc = new();
        var done = 0;
        var errors = 0;
        var reportInterval = Math.Max(500, total / 20);

        foreach (var filePath in paths) {
            try {
                ReadBfres(romfsRoot, filePath, acc);
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

    private static void LoadStringCache(string romfsRoot)
    {
        var bfres = McDecompressor.Decompress(
            File.ReadAllBytes(Path.Combine(romfsRoot, "Shader", "ExternalBinaryString.bfres.mc")));
        
        using MemoryStream ms = new(bfres, writable: false);
        _ = new ResFile(ms, leaveOpen: true);
    }

    private static void ReadBfres(string romfsRoot, string filePath, Dictionary<MaterialKey, MaterialOptionValues> acc)
    {
        var raw  = File.ReadAllBytes(filePath);
        var data = McDecompressor.Decompress(raw);

        using MemoryStream ms = new(data, writable: false);
        var resFile = new ResFile(ms, leaveOpen: true);

        if (resFile.Models is not { Count: > 0 }) {
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
        var pre  = TryFindOption(mat, OptionPreNormal,  out var preVal);
        var post = TryFindOption(mat, OptionPostNormal, out var postVal);
        
        if (pre) {
            values.PreNormal = preVal;
        }
        
        if (post) {
            values.PostNormal = postVal;
        }
        
        return pre || post;
    }

    private static bool TryFindOption(Material mat, string optionName, out long parsed)
    {
        parsed = 0;
        var opts = mat.ShaderAssign?.ShaderOptions;
        
        if (opts is not { Count: > 0 }) {
            return false;
        }

        foreach (var kv in opts) {
            if (string.Equals(kv.Key, optionName, StringComparison.OrdinalIgnoreCase))
                return TryParseShaderOptionNumber(kv.Value?.String, out parsed);
        }

        return false;
    }

    private static bool TryParseShaderOptionNumber(string? s, out long value)
    {
        value = 0;
        s = s?.Trim();

        if (string.IsNullOrEmpty(s) || s.Equals("<Default Value>", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        if (s.Equals("True", StringComparison.OrdinalIgnoreCase)) {
            value = 1;
            return true;
        }

        if (s.Equals("False", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (!s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !ulong.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hx)
            || hx > long.MaxValue) {
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        value = (long)hx;
        return true;

    }

    private static Dictionary<string, List<SortedDictionary<int, long>>> BuildOutput(
        List<(int Version, Dictionary<MaterialKey, MaterialOptionValues> Snapshot)> snapshots)
    {
        Dictionary<string, List<SortedDictionary<int, long>>> result = new(StringComparer.Ordinal);

        foreach (var matKey in snapshots.SelectMany(s => s.Snapshot.Keys).Distinct()) {
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

            if (byVersion.Count < 2 || byVersion.All(e => e.Value == byVersion[0].Value)) {
                return;
            }

            var vMap = new SortedDictionary<int, long>(byVersion.ToDictionary(e => e.Version, e => e.Value));

            if (!result.TryGetValue(option, out var list)) {
                result[option] = list = [];
            }
            
            if (list.All(m => !m.Keys.SequenceEqual(vMap.Keys) || !m.Values.SequenceEqual(vMap.Values))) {
                list.Add(vMap);
            }
        }
    }

    private readonly record struct MaterialKey(string BfresRelativePath, string ModelName, string MaterialName);

    private struct MaterialOptionValues
    {
        public long? PreNormal;
        public long? PostNormal;
    }
}
