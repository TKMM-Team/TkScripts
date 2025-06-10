using System.IO.Hashing;
using System.Runtime.InteropServices;
using BymlLibrary;
using BymlLibrary.Nodes.Containers;
using CommunityToolkit.HighPerformance;
using Revrs;
using Revrs.Buffers;
using TkScripts.LookupTables.Models;
using TotkCommon;
using TotkCommon.Extensions;

namespace TkScripts.LookupTables.Generators;

public enum RsdbCacheTag
{
    Cache,
    Index
}

public sealed class RsdbCacheGenerator : IGenerator
{
    private readonly Dictionary<ulong, RsdbCache> _cache = [];

    public IEnumerable<object> Tags { get; } = [RsdbCacheTag.Cache, RsdbCacheTag.Index];

    public string NameFormat => "Rsdb{0}.bpcc";

    public Task<object?> Generate(string[] gamePaths)
    {
        foreach (string romfs in gamePaths) {
            string rsdbFolder = Path.Combine(romfs, "RSDB");
            int version = romfs.GetRomfsVersionOrDefault();
            foreach (string rsdbFile in Directory.EnumerateFiles(rsdbFolder)) {
                CacheRsdb(romfs, rsdbFile, version, _cache);
            }
        }

        foreach (ulong hash in _cache.Keys.ToArray()) {
            RsdbCache cache = _cache[hash];
            foreach (ulong rowId in cache.Keys.ToArray()) {
                List<(Byml Node, int Version, int Count, int HashCode)> entries = cache[rowId];
                if (entries.Count < 2) {
                    cache.Remove(rowId);
                }
            }
        }

        return Task.FromResult<object?>(_cache);
    }

    public void WriteBinary(Stream output, object tag)
    {
        switch (tag) {
            case RsdbCacheTag.Cache:
                WriteCacheBinary(output);
                return;
            case RsdbCacheTag.Index:
                WriteIndexBinary(output);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(tag), tag, null);
        }
    }

    private void WriteIndexBinary(Stream output)
    {
        output.Write(_cache.Count);
        
        foreach ((ulong rsdbNameHash, RsdbCache cache) in _cache) {
            output.Write(rsdbNameHash);
            output.Write(cache.IndexMapping.Count);
            for (int i = 0; i < cache.IndexMapping.Count; i++) {
                output.Write(cache.IndexMapping[i]);
                output.Write(i);
            }
        }
    }

    private void WriteCacheBinary(Stream output)
    {
        output.Write(_cache.Count);
        
        foreach ((ulong rsdbNameHash, RsdbCache cache) in _cache) {
            if (cache.Count == 0) {
                continue;
            }

            output.Write(rsdbNameHash);
            output.Write(cache.Count);

            foreach ((ulong rowIdHash, List<(Byml Node, int Version, int Count, int HashCode)> entries) in cache) {
                output.Write(rowIdHash);
                output.Write(entries.Count);
                foreach ((Byml row, int version, int _, int _) in entries) {
                    output.Write(version);

                    MemoryStream ms = new();
                    row.WriteBinary(ms, Endianness.Little);
                    output.Write(Convert.ToInt32(ms.Length));
                    ms.Seek(0, SeekOrigin.Begin);
                    ms.CopyTo(output);
                }
            }
        }
    }

    private static void CacheRsdb(string romfs, string target, int version, Dictionary<ulong, RsdbCache> rsdbCache)
    {
        ReadOnlySpan<char> canonical = target.ToCanonical(romfs);
        if (GetId(canonical) is not string rowId) {
            return;
        }

        ulong hash = XxHash3.HashToUInt64(MemoryMarshal.Cast<char, byte>(canonical));

        if (!rsdbCache.TryGetValue(hash, out RsdbCache? cache)) {
            rsdbCache[hash] = cache = [];
        }

        using FileStream fs = File.OpenRead(target);
        int size = Convert.ToInt32(fs.Length);
        using ArraySegmentOwner<byte> data = ArraySegmentOwner<byte>.Allocate(size);
        fs.ReadExactly(data.Segment);

        if (Zstd.IsCompressed(data.Segment)) {
            using ArraySegmentOwner<byte> decompressed = ArraySegmentOwner<byte>.Allocate(Zstd.GetDecompressedSize(data.Segment));
            Totk.Zstd.Decompress(data.Segment, decompressed.Segment);
            CacheEntries(canonical, decompressed.Segment, rowId, version, cache);
            return;
        }

        CacheEntries(canonical, data.Segment, rowId, version, cache);
    }

    private static void CacheEntries(ReadOnlySpan<char> canonical, ArraySegment<byte> data, string rowId, int version, RsdbCache cache)
    {
        Byml byml = Byml.FromBinary(data);

        foreach (Byml row in byml.GetArray()) {
            BymlMap map = row.GetMap();
            ulong hash = rowId switch {
                "NameHash" => map[rowId].GetUInt32(),
                _ => XxHash3.HashToUInt64(MemoryMarshal.Cast<char, byte>(map[rowId].GetString()))
            };

            if (!cache.IsIndexMappingFilled) {
                cache.IndexMapping.Add(hash);
            }

            int hashCode = Byml.ValueEqualityComparer.Default.GetHashCode(row);

            if (!cache.TryGetValue(hash, out List<(Byml Node, int Version, int Count, int HashCode)>? entries)) {
                cache[hash] = entries = [
                    (row, version, map.Count, hashCode)
                ];

                continue;
            }

            (Byml lastNode, _, _, int lastHashCode) = entries[^1];

            if (Byml.ValueEqualityComparer.Default.Equals(lastNode, row)) {
                continue;
            }

            if (lastHashCode == hashCode) {
                string id = rowId switch {
                    "NameHash" => map[rowId].GetUInt32().ToString(),
                    _ => map[rowId].GetString()
                };

                throw new InvalidDataException($"Hash collision in '{canonical}' id '{id}'");
            }

            entries.Add(
                (row, version, map.Count, hashCode)
            );
        }

        cache.IsIndexMappingFilled = true;
    }

    private static string? GetId(ReadOnlySpan<char> canonical)
    {
        // ReSharper disable StringLiteralTypo
        return canonical switch {
            "RSDB/GameSafetySetting.Product.rstbl.byml" => "NameHash",
            "RSDB/RumbleCall.Product.rstbl.byml" or
                "RSDB/UIScreen.Product.rstbl.byml" => "Name",
            "RSDB/TagDef.Product.rstbl.byml" => "FullTagId",
            "RSDB/ActorInfo.Product.rstbl.byml" or
                "RSDB/AttachmentActorInfo.Product.rstbl.byml" or
                "RSDB/Challenge.Product.rstbl.byml" or
                "RSDB/EnhancementMaterialInfo.Product.rstbl.byml" or
                "RSDB/EventPlayEnvSetting.Product.rstbl.byml" or
                "RSDB/EventSetting.Product.rstbl.byml" or
                "RSDB/GameActorInfo.Product.rstbl.byml" or
                "RSDB/GameAnalyzedEventInfo.Product.rstbl.byml" or
                "RSDB/GameEventBaseSetting.Product.rstbl.byml" or
                "RSDB/GameEventMetadata.Product.rstbl.byml" or
                "RSDB/LoadingTips.Product.rstbl.byml" or
                "RSDB/Location.Product.rstbl.byml" or
                "RSDB/LocatorData.Product.rstbl.byml" or
                "RSDB/PouchActorInfo.Product.rstbl.byml" or
                "RSDB/XLinkPropertyTable.Product.rstbl.byml" or
                "RSDB/XLinkPropertyTableList.Product.rstbl.byml" => "__RowId",
            _ => null
        };
    }
}