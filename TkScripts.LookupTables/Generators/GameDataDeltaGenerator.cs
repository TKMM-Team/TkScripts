using BsDiff;
using CommunityToolkit.HighPerformance;
using Kokuban;
using TotkCommon;
using TotkCommon.Extensions;

namespace TkScripts.LookupTables.Generators;

public sealed class GameDataDeltaGenerator : IGenerator
{
    private const string GdlPrefix = "GameDataList.Product.";

    private readonly Dictionary<object, (byte[], byte[])> _deltas = [];

    public IEnumerable<object> Tags => _deltas.Keys;

    public string NameFormat => "GameDataDelta.{0}.gdldelta";

    public Task<object?> Generate(string[] gamePaths)
    {
        List<(int, byte[])> versions = [];
        int previousVersion = 0;

        foreach (string gamePath in gamePaths.OrderBy(static p => p.GetRomfsVersionOrDefault())) {
            string gameDataFile = GetGameDataFile(gamePath, out int fileVersion);

            // Skip unchanged GDL files
            if (previousVersion == fileVersion) {
                continue;
            }
            
            previousVersion = fileVersion;

            string zsDicPack = Path.Combine(gamePath, "Pack", "ZsDic.pack.zs");
            if (!File.Exists(zsDicPack)) {
                Console.WriteLine(Chalk.BrightYellow + $"Failed to locate ZsDic.pack in {gamePath}");
                continue;
            }

            Zstd.Shared.LoadDictionaries(zsDicPack);
            var data = Zstd.Shared.Decompress(File.ReadAllBytes(gameDataFile));
            versions.Add((fileVersion, data));
        }
        
        for (int i = 0; i < versions.Count - 1;) {
            var (firstVersion, firstData) = versions[i];
            var (nextVersion, nextData) = versions[++i];
            
            _deltas[$"{firstVersion}-{nextVersion}"] = (firstData, nextData);
            _deltas[$"{nextVersion}-{firstVersion}"] = (nextData, firstData);
        }

        return Task.FromResult<object?>(null);
    }

    public void WriteBinary(Stream output, object tag)
    {
        var delta = _deltas[tag];
        
        BinaryPatch.Create(delta.Item1, delta.Item2, output);
    }

    private static string GetGameDataFile(string gamePath, out int version)
    {
        var gameDataFilePath = Directory.GetFiles(
            Path.Combine(gamePath, "GameData"), "GameDataList.Product.*.byml.zs", SearchOption.TopDirectoryOnly
        ).First();
        
        version = int.Parse(Path.GetFileName(gameDataFilePath.AsSpan())[GdlPrefix.Length..][..3]);
        return gameDataFilePath;
    }
}