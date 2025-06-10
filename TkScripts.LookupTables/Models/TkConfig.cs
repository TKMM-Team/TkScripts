using System.Text.Json;

namespace TkScripts.LookupTables.Models;

public record TkConfig(string[] GameDumpFolderPaths)
{
    public static string[] GetGamePaths()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tkmm2", "TkConfig.json");
        
        using FileStream fs = File.OpenRead(path);
        return JsonSerializer.Deserialize<TkConfig>(fs)?.GameDumpFolderPaths ?? [];
    }
}