using System.Text.Json;
using System.Text.Json.Serialization;

namespace TkScripts.Shared.Models;

public record TkConfig(string[] GameDumpFolderPaths)
{
    public static string[] GetGamePaths()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tkmm2", "TkConfig.json");
        
        using var fs = File.OpenRead(path);
        return JsonSerializer.Deserialize<TkConfig>(fs, TkConfigJsonContext.Default.TkConfig)?.GameDumpFolderPaths ?? [];
    }
}

[JsonSerializable(typeof(TkConfig))]
internal partial class TkConfigJsonContext : JsonSerializerContext;