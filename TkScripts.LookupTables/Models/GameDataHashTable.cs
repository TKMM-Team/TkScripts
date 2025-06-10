namespace TkScripts.LookupTables.Models;

public sealed class GameDataHashTable
{
    public Dictionary<ulong, Dictionary<uint, int>> LookupTables { get; } = [];

    public Dictionary<ulong, int> Bool64Table { get; set; }
}