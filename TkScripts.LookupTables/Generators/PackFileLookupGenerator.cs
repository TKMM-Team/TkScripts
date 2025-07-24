using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.HighPerformance;
using CommunityToolkit.HighPerformance.Buffers;
using Kokuban;
using Revrs;
using SarcLibrary;
using TotkCommon;
using TotkCommon.Extensions;
using ResultCollectionEntry = System.Collections.Generic.OrderedDictionary<string, System.Collections.Generic.List<int>>;
using ResultCollection = System.Collections.Generic.Dictionary<string, System.Collections.Generic.OrderedDictionary<string, System.Collections.Generic.List<int>>>;

namespace TkScripts.LookupTables.Generators;

public class PackFileLookupGenerator : IGenerator
{
    private const uint Magic = 0x48434B50;
    private readonly Dictionary<string, string> _results = [];

    public IEnumerable<object> Tags { get; } = ["Lookup"];

    public string NameFormat => "PackFile{0}.pkcache";

    public Task<object?> Generate(string[] gamePaths)
    {
        int versionCount = gamePaths.Length;
        ResultCollection allVersions = [];

        foreach (string folder in gamePaths) {
            string zsDicPack = Path.Combine(folder, "Pack", "ZsDic.pack.zs");
            if (!File.Exists(zsDicPack)) {
                Console.WriteLine(Chalk.BrightYellow + $"Failed to locate ZsDic.pack in {folder}");
                continue;
            }

            int version = folder.GetRomfsVersion();
            Zstd.Shared.LoadDictionaries(zsDicPack);
            CollectFolder(folder, Path.Combine(folder, "Pack"), version, allVersions);
            Console.WriteLine($"[{DateTime.Now:t}] Completed {folder}");
        }

        Dictionary<string, ResultCollectionEntry> missing = [];

        foreach ((string canon, ResultCollectionEntry parents) in allVersions) {
            string? foundParent = null;

            foreach ((string parent, List<int> versions) in parents) {
                if (versions.Count == versionCount) {
                    foundParent = parent;
                    break;
                }
            }

            if (foundParent is null && IsEachVersionListEqual(parents) is false) {
                Console.WriteLine(Chalk.BrightYellow +
                                  $"Versioning required: '{canon}' has no parent pack files found in every game version.");
                missing[canon] = parents;
                continue;
            }

            _results[canon] = foundParent ?? parents.First().Key;
        }

        return Task.FromResult<object?>(missing);
    }

    private static void CollectFolder(string romfs, string packFolder, int version, ResultCollection results)
    {
        foreach (string file in Directory.EnumerateFiles(packFolder, "*.*", SearchOption.AllDirectories)) {
            byte[] raw = File.ReadAllBytes(file);
            byte[] decompressed = Zstd.Shared.Decompress(raw);

            RevrsReader reader = new(decompressed);
            ImmutableSarc sarc = new(ref reader);

            string packFileRelative = Path.GetRelativePath(romfs, file);

            foreach ((string name, _) in sarc) {
                if (!results.TryGetValue(name, out ResultCollectionEntry? parents)) {
                    results[name] = new ResultCollectionEntry {
                        { packFileRelative, [version] }
                    };
                    continue;
                }

                if (!parents.TryGetValue(packFileRelative, out List<int>? versions)) {
                    parents[packFileRelative] = [version];
                    continue;
                }

                versions.Add(version);
            }
        }
    }

    private static bool IsEachVersionListEqual(ResultCollectionEntry parents)
    {
        List<int> first = parents.Values.First();
        return parents.Values.Skip(1).All(value => first.SequenceEqual(value));
    }

    public void WriteBinary(Stream output, object tag)
    {
        output.Write(Magic);
        output.Write(_results.Count);
        output.Write(0x10 + _results.Count * 8);
        output.Write(0U);

        string[] keys = _results.Keys.ToArray();
        string[] values = _results.Values.ToArray();
        Array.Sort(keys, values);

        Dictionary<ushort, HashSet<uint>> hashCollisions = [];

        int skippedValues = 0;
        Dictionary<string, int> parentNameLookup = [];

        for (int i = 0; i < keys.Length; i++) {
            ReadOnlySpan<char> key = keys[i];

            ushort sectionKey = (ushort)((byte)key[0] << 8 | (byte)key[^1]);
            output.Write(sectionKey);

            uint hash = XxHash32.HashToUInt32(key.Cast<char, byte>());
            if (!hashCollisions.TryGetValue(sectionKey, out HashSet<uint>? hashes)) {
                hashCollisions[sectionKey] = hashes = [];
            }

            if (!hashes.Add(hash)) {
                Console.WriteLine(Chalk.BrightYellow + $"Hash collision at '{key}'");
            }

            output.Write(hash);

            ref int index = ref CollectionsMarshal.GetValueRefOrAddDefault(parentNameLookup, values[i], out bool exists);
            if (!exists) index = i - skippedValues;
            else skippedValues++;

            output.Write((ushort)index);
        }

        foreach ((string archiveRelativePath, _) in parentNameLookup) {
            ReadOnlySpan<char> canonical = archiveRelativePath.ToCanonical(out RomfsFileAttributes attributes);
            int count = Encoding.UTF8.GetByteCount(canonical);
            using SpanOwner<byte> utf8 = SpanOwner<byte>.Allocate(count);
            Encoding.UTF8.GetBytes(canonical, utf8.Span);
            output.Write(utf8.Span);
            output.Write((byte)0);
            output.Write((byte)attributes);
        }

        output.Seek(0xC, SeekOrigin.Begin);
        output.Write(parentNameLookup.Count);
    }
}