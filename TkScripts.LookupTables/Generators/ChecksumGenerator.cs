using System.IO.Hashing;
using CommunityToolkit.HighPerformance.Buffers;
using Kokuban;
using Revrs;
using Revrs.Extensions;
using SarcLibrary;
using TkScripts.LookupTables.Models;
using TotkCommon;
using TotkCommon.Components;
using TotkCommon.Extensions;
using HashVersions = System.Collections.Generic.List<TkScripts.LookupTables.Models.ChecksumEntry>;

namespace TkScripts.LookupTables.Generators;

public sealed class ChecksumGenerator : IGenerator
{
    private const uint SarcMagic = 0x43524153;

    private static readonly string[] _ignore = [
        "System/Resource/ResourceSizeTable.Product.rsizetable",
        "Pack/ZsDic.pack",
    ];

    private int _baseVersion = -1;
    private int _tracking;
    private readonly Dictionary<string, HashVersions> _cache = [];

    public IEnumerable<object> Tags { get; } = ["Checksums"];

    public string NameFormat => "{0}.bpclt";

    public async Task<object?> Generate(string[] gamePaths)
    {
        foreach (string folder in gamePaths) {
            string zsDicPack = Path.Combine(folder, "Pack", "ZsDic.pack.zs");
            if (!File.Exists(zsDicPack)) {
                Console.WriteLine(Chalk.BrightYellow + $"Failed to locate ZsDic.pack in {folder}");
                continue;
            }

            int version = folder.GetRomfsVersionOrDefault();
            if (_baseVersion < 0) {
                _baseVersion = version;
            }

            Zstd.Shared.LoadDictionaries(zsDicPack);
            await CollectDiskDirectory(folder, folder, version);

            Console.WriteLine($"\n[{DateTime.Now:t}] Completed {folder}");
            _tracking = 0;
        }
        
        return _cache;
    }

    public void WriteBinary(Stream output, object tag)
    {
        foreach (string str in _ignore) {
            _cache.Remove(str);
        }

        // Pre-Compiled Lookup Table
        output.Write("PCLT"u8);
        output.Write(_baseVersion);
        output.Write(_cache.Count);

        foreach ((string key, HashVersions versions) in _cache)
        {
            output.Write(TotkChecksums.GetNameHash(key));
            output.Write(versions.Count);

            foreach ((int version, int size, ulong hash) in versions)
            {
                output.Write(version);
                output.Write(size);
                output.Write(hash);
            }
        }
    }

    private async Task CollectDiskDirectory(string directory, string romfs, int version)
    {
        await Parallel.ForEachAsync(Directory.EnumerateFiles(directory), async (file, cancellationToken) => { await Task.Run(() => CollectDiskFile(file, romfs, version), cancellationToken); });

        await Parallel.ForEachAsync(Directory.EnumerateDirectories(directory), async (folder, cancellationToken) => { await CollectDiskDirectory(folder, romfs, version); });
    }

    private void CollectDiskFile(string filePath, string romfs, int version)
    {
        string canonical = filePath.ToCanonical(romfs, out RomfsFileAttributes attributes).ToString();
        if (attributes.HasFlag(RomfsFileAttributes.HasMcExtension)) {
            // MC files are skipped until
            // decompression is possible
            return;
        }

        using FileStream fs = File.OpenRead(filePath);
        int size = Convert.ToInt32(fs.Length);
        using SpanOwner<byte> buffer = SpanOwner<byte>.Allocate(size);
        fs.ReadExactly(buffer.Span);

        if (attributes.HasFlag(RomfsFileAttributes.HasZsExtension)) {
            int decompressedSize = Zstd.GetDecompressedSize(buffer.Span);
            using SpanOwner<byte> decompressed = SpanOwner<byte>.Allocate(decompressedSize);
            Zstd.Shared.Decompress(buffer.Span, decompressed.Span);
            CollectData(canonical, decompressed.Span, version);
            return;
        }

        CollectData(canonical, buffer.Span, version);
    }

    private void CollectData(string canonical, Span<byte> data, int version)
    {
        if (data.Length > 3 && data.Read<uint>() == SarcMagic) {
            ReadOnlySpan<char> ext = Path.GetExtension(canonical.AsSpan());
            RevrsReader reader = new(data);
            ImmutableSarc sarc = new(ref reader);
            foreach ((string sarcFileName, Span<byte> sarcFileData) in sarc) {
                switch (ext) {
                    case ".pack": {
                        CollectData(sarcFileName, sarcFileData, version);
                        break;
                    }
                    default: {
                        CollectData($"{canonical}/{sarcFileName}", sarcFileData, version);
                        break;
                    }
                }
            }
        }

        CollectChecksum(canonical, data, version);
    }

    private void CollectChecksum(string canonicalFileName, Span<byte> data, int version)
    {
        ChecksumEntry entry;
        entry.Version = version;

        if (Zstd.IsCompressed(data)) {
            int decompressedSize = Zstd.GetDecompressedSize(data);
            using SpanOwner<byte> decompressed = SpanOwner<byte>.Allocate(decompressedSize);
            Zstd.Shared.Decompress(data, decompressed.Span);

            entry.Hash = XxHash3.HashToUInt64(decompressed.Span);
            entry.Size = decompressedSize;
        }
        else {
            entry.Hash = XxHash3.HashToUInt64(data);
            entry.Size = data.Length;
        }

        lock (_cache) {
            if (_cache.TryGetValue(canonicalFileName, out HashVersions? versions)) {
                (int _, int size, ulong hash) = versions[^1];
                if (size != entry.Size || hash != entry.Hash) {
                    versions.Add(entry);
                }

                Console.Write($"\r{++_tracking}");
                return;
            }

            _cache[canonicalFileName] = [entry];
        }

        Console.Write($"\r{++_tracking}");
    }
}