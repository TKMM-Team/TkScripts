using System.Text.Json;

namespace TkScripts.LookupTables.Components;

public sealed class OutputStore(string outputFolder)
{
    private static readonly JsonSerializerOptions _jsonOptions = new() {
        WriteIndented = true
    };
    
    private readonly string _outputFolder = outputFolder;

    public async Task WriteResults(IGenerator generator, string[] gamePaths)
    {
        Directory.CreateDirectory(_outputFolder);
        
        string typeName = generator.GetType().Name;
        object? debug = await generator.Generate(gamePaths);
        
        try {
            if (debug is not null) {
                string outputJson = Path.Combine(_outputFolder, $"{typeName}.debug.json");
                await using FileStream fsJson = File.Create(outputJson);
                
                await JsonSerializer.SerializeAsync(fsJson, debug, _jsonOptions);
            }
        }
        catch (Exception e) {
            Console.WriteLine($"Failed to serialize {typeName} as JSON: {e}");
        }

        foreach (object tag in generator.Tags) {
            string fileName = string.Format(generator.NameFormat, tag);

            string outputBinary = Path.Combine(_outputFolder, fileName);
            await using FileStream fs = File.Create(outputBinary);
            
            generator.WriteBinary(fs, tag);
        }
    }
}