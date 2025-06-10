namespace TkScripts.LookupTables;

public interface IGenerator
{
    IEnumerable<object> Tags { get; }
    
    string NameFormat { get; }
    
    Task<object?> Generate(string[] gamePaths);
    
    void WriteBinary(Stream output, object tag);
}