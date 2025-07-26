using System.Diagnostics;
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
using ResultCollectionEntry = System.Collections.Generic.OrderedDictionary<TkScripts.LookupTables.Generators.PackFileName, System.Collections.Generic.List<int>>;
using ResultCollection = System.Collections.Generic.Dictionary<string, System.Collections.Generic.OrderedDictionary<TkScripts.LookupTables.Generators.PackFileName, System.Collections.Generic.List<int>>>;

namespace TkScripts.LookupTables.Generators;

public class PackFileLookupGenerator : IGenerator
{
    private const uint Magic = 0x48434B50;
    private readonly Dictionary<string, PackFileName> _results = [];
    private readonly Dictionary<int, Sarc> _cache = [];

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

        ResultCollection missing = [];

        foreach ((string canon, ResultCollectionEntry parents) in allVersions) {
            PackFileName? foundParent = null;

            if (canon == "Game/StaffRoll/StaffRollSetTable/StaffRoll-NX.game__ui__StaffRollSetTable.bgyml") {
                Debugger.Break();
            }

            foreach ((PackFileName parent, List<int> versions) in parents) {
                if (versions.Count == versionCount) {
                    foundParent = parent;
                    break;
                }
            }

            if (foundParent is null && (parents.Count == 1 || !IsEachVersionListEqual(parents))) {
                Console.WriteLine(Chalk.BrightYellow +
                                  $"Versioning required: '{canon}' has no parent pack files found in every game version.");
                // This may be needed for caching, however, the
                // lookup is dependent on mod devs using the correct files
                missing[canon] = parents;
            }

            _results[canon] = foundParent ?? parents.First().Key;
        }

        return Task.FromResult<object?>(
            missing.Select(x => (x.Key, Value: x.Value.ToList()))
                .ToDictionary(x => x.Key, x => x.Value)
        );
    }

    private void CollectFolder(string romfs, string packFolder, int version, ResultCollection results)
    {
        foreach (string file in Directory.EnumerateFiles(packFolder, "*.*", SearchOption.AllDirectories)) {
            byte[] raw = File.ReadAllBytes(file);
            byte[] decompressed = Zstd.Shared.Decompress(raw);

            RevrsReader reader = new(decompressed);
            ImmutableSarc sarc = new(ref reader);

            PackFileName packFileRelative = new(
                file.ToCanonical(romfs, out RomfsFileAttributes attributes).ToString(),
                attributes);

            foreach ((string name, Span<byte> data) in sarc) {
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
        PackFileName[] values = _results.Values.ToArray();
        Array.Sort(keys, values);

        Dictionary<ushort, HashSet<uint>> hashCollisions = [];

        int skippedValues = 0;
        Dictionary<PackFileName, int> parentNameLookup = [];

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

        foreach ((PackFileName archiveRelativePath, _) in parentNameLookup) {
            int count = Encoding.UTF8.GetByteCount(archiveRelativePath.Canonical);
            using SpanOwner<byte> utf8 = SpanOwner<byte>.Allocate(count);
            Encoding.UTF8.GetBytes(archiveRelativePath.Canonical, utf8.Span);
            output.Write(utf8.Span);
            output.Write((byte)0);
            output.Write((byte)archiveRelativePath.Attributes);
        }

        output.Seek(0xC, SeekOrigin.Begin);
        output.Write(parentNameLookup.Count);
    }
}

public record struct PackFileName(string Canonical, RomfsFileAttributes Attributes);