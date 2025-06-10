using System.IO.Hashing;
using BymlLibrary;
using BymlLibrary.Nodes.Containers;
using CommunityToolkit.HighPerformance;
using Kokuban;
using TkScripts.LookupTables.Models;
using TotkCommon;
using TotkCommon.Extensions;

namespace TkScripts.LookupTables.Generators;

public sealed class GameDataIndexGenerator : IGenerator
{
    private readonly Dictionary<int, GameDataHashTable> _versions = [];
    
    public IEnumerable<object> Tags { get; } = ["Index"];

    public string NameFormat => "GameData{0}.bpclt";

    public Task<object?> Generate(string[] gamePaths)
    {
        string? lastVersion = null;
        
        foreach ((string gamePath, int version) in gamePaths.Select(static gamePath => (gamePath, version: gamePath.GetRomfsVersionOrDefault())).OrderBy(x => x.version)) {
            string path = GetGdlPath(gamePath);
            if (Path.GetFileName(path) == lastVersion) {
                continue;
            }
            
            string zsDicPack = Path.Combine(gamePath, "Pack", "ZsDic.pack.zs");
            if (!File.Exists(zsDicPack)) {
                Console.WriteLine(Chalk.BrightYellow + $"Failed to locate ZsDic.pack in {gamePath}");
                continue;
            }
            
            Zstd.Shared.LoadDictionaries(zsDicPack);

            GameDataHashTable table = _versions[version] = new GameDataHashTable();
            GenerateHashTable(table, path);
            lastVersion = Path.GetFileName(path);
        }

        return Task.FromResult<object?>(_versions);
    }

    public void WriteBinary(Stream output, object tag)
    {
        // Pre-Compiled Lookup Table
        output.Write("PCLT"u8);
        output.Write(_versions.Count);
        
        foreach ((int version, GameDataHashTable hashTable) in _versions) {
            output.Write(version);
            
            // Standard Tables
            output.Write("_STD"u8);
            output.Write(hashTable.LookupTables.Count);

            foreach ((ulong tableHash, Dictionary<uint, int> indexTable) in hashTable.LookupTables) {
                // Table
                output.Write("_TBL"u8);
                output.Write(tableHash);
                output.Write(indexTable.Count);

                foreach ((uint hash, int index) in indexTable) {
                    output.Write(hash);
                    output.Write(index);
                }
            }
            
            // 64-Bit Table
            output.Write("64BT"u8);
            
            // Table
            output.Write("_TBL"u8);
            output.Write(hashTable.Bool64Table.Count);
            
            foreach ((ulong hash, int index) in hashTable.Bool64Table) {
                output.Write(hash);
                output.Write(index);
            }
        }
    }

    private static string GetGdlPath(string gamePath)
    {
        string gameDataPath = Path.Combine(gamePath, "GameData");
        return Directory.EnumerateFiles(gameDataPath, "*.byml.zs", SearchOption.TopDirectoryOnly).First();
    }

    private static void GenerateHashTable(GameDataHashTable hashTable, string gdlFilePath)
    {
        byte[] buffer = Zstd.Shared.Decompress(File.ReadAllBytes(gdlFilePath));
        Byml gdl = Byml.FromBinary(buffer).GetMap()["Data"].GetMap();

        foreach ((string tableName, Byml table) in gdl.GetMap()) {
            BymlArray entries = table.GetArray();
            
            if (tableName is "Bool64bitKey") {
                hashTable.Bool64Table = CreateIndexTable<ulong>(tableName, entries);
                continue;
            }
            
            ulong tableNameHash = XxHash3.HashToUInt64(
                tableName.AsSpan().Cast<char, byte>());
            hashTable.LookupTables[tableNameHash] = CreateIndexTable<uint>(tableName, entries);
        }
    }

    private static Dictionary<T, int> CreateIndexTable<T>(string tableName, BymlArray entries) where T : notnull
    {
        Dictionary<T, int> indexTable = [];
        
        for (int i = 0; i < entries.Count; i++) {
            BymlMap entry = entries[i].GetMap();

            if (!entry.TryGetValue("Hash", out Byml? hashNode)) {
                Console.WriteLine(Chalk.BrightYellow + $"Invalid GDL entry at {i} in {tableName}");
                continue;
            }

            indexTable[hashNode.Get<T>()] = i;
        }

        return indexTable;
    }
}