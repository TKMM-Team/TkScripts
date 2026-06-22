using CommunityToolkit.HighPerformance;
using Kokuban;
using TotkCommon;
using TotkCommon.Extensions;

namespace TkScripts.LookupTables.Generators;

public enum GameDataCacheTag
{
    Cache
}

public sealed class GameDataCacheGenerator : IGenerator
{
    private const uint Magic = 0x434C4447; // GDLC
    private const string GdlPrefix = "GameDataList.Product.";

    private readonly Dictionary<int, byte[]> _versions = [];

    public IEnumerable<object> Tags { get; } = [GameDataCacheTag.Cache];

    public string NameFormat => "GameData{0}.gdlc";

    public Task<object?> Generate(string[] gamePaths)
    {
        HashSet<int> savedVersions = [];
        string? lastGdlFileName = null;

        foreach (string gamePath in gamePaths.OrderBy(static p => p.GetRomfsVersionOrDefault())) {
            string gdlPath = GetGdlPath(gamePath);
            string gdlFileName = Path.GetFileName(gdlPath);

            if (gdlFileName == lastGdlFileName) {
                continue;
            }

            if (!TryGetGdlVersion(gdlPath, out int gdlVersion)) {
                Console.WriteLine(Chalk.BrightYellow + $"Skipping unrecognized GameData file: {gdlPath}");
                continue;
            }

            if (!savedVersions.Add(gdlVersion)) {
                Console.WriteLine(Chalk.BrightYellow +
                    $"Skipping GameData version {gdlVersion} in {gamePath} (already cached).");
                continue;
            }

            string zsDicPack = Path.Combine(gamePath, "Pack", "ZsDic.pack.zs");
            if (!File.Exists(zsDicPack)) {
                Console.WriteLine(Chalk.BrightYellow + $"Failed to locate ZsDic.pack in {gamePath}");
                savedVersions.Remove(gdlVersion);
                continue;
            }

            Zstd.Shared.LoadDictionaries(zsDicPack);
            _versions[gdlVersion] = Zstd.Shared.Decompress(File.ReadAllBytes(gdlPath));
            lastGdlFileName = gdlFileName;

            Console.WriteLine($"Cached GameData version {gdlVersion} from {gdlPath}");
        }

        return Task.FromResult<object?>(_versions);
    }

    public void WriteBinary(Stream output, object tag)
    {
        if (tag is not GameDataCacheTag.Cache) {
            throw new ArgumentOutOfRangeException(nameof(tag), tag, null);
        }

        output.Write(Magic);
        output.Write(_versions.Count);

        foreach ((int version, byte[] data) in _versions.OrderBy(static x => x.Key)) {
            output.Write(version);
            output.Write(data.Length);
            output.Write(data);
        }
    }

    private static string GetGdlPath(string gamePath)
    {
        string gameDataPath = Path.Combine(gamePath, "GameData");
        return Directory.EnumerateFiles(gameDataPath, "GameDataList.Product.*.byml.zs", SearchOption.TopDirectoryOnly)
            .First();
    }

    private static bool TryGetGdlVersion(string gdlPath, out int version)
    {
        version = -1;
        ReadOnlySpan<char> fileName = Path.GetFileName(gdlPath).AsSpan();

        if (!fileName.StartsWith(GdlPrefix, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        var rest = fileName[GdlPrefix.Length..];
        var extension = rest.IndexOf('.');
        if (extension <= 0) {
            return false;
        }

        return int.TryParse(rest[..extension], out version);
    }
}
