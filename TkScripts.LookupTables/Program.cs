using ConsoleAppFramework;
using Kokuban;
using TkScripts.LookupTables.Components;
using TkScripts.LookupTables.Generators;
using TkScripts.LookupTables.Models;

await ConsoleApp.RunAsync(args, App.Run);

internal static class App
{
    /// <summary>
    /// Generates lookup tables and precompiled cache for use in merging
    /// </summary>
    /// <param name="output">The path to the output folder</param>
    /// <param name="cancellationToken"></param>
    public static async Task Run(string output = "output", CancellationToken cancellationToken = default)
    {
        if (TkConfig.GetGamePaths() is not { Length: > 2 } gamePaths) {
            Console.WriteLine(Chalk.Red + "Extracted game paths not found. Ensure all versions of TotK are configured as romfs dumps in TKMM.");
            return;
        }

        var tasks = new Task[4];

        OutputStore store = new(output);

        tasks[0] = store.WriteResults(new RsdbCacheGenerator(), gamePaths);
        tasks[1] = store.WriteResults(new GameDataIndexGenerator(), gamePaths);
        tasks[2] = store.WriteResults(new ChecksumGenerator(), gamePaths);
        tasks[3] = store.WriteResults(new PackFileLookupGenerator(), gamePaths, compress: true);
        
        await Task.WhenAll(tasks);
    }
}