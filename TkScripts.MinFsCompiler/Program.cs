using System.Reflection;
using Spectre.Console;
using TkScripts.MinFsCompiler;
using TkScripts.Shared.Models;
using TotkCommon.Extensions;

string version = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion.Split('+')[0] ?? "Undefined";

Console.WriteLine($"""
    TotK Minimized FileSystem Compiler [Version {version}]
    (c) TKMM-Team. MIT.

    """);

if (TkConfig.GetGamePaths() is not { Length: > 2 } gamePaths) {
    AnsiConsole.MarkupLine("[red bold]Extracted game paths not found. Ensure all versions of TotK are configured as romfs dumps in TKMM.[/]");
    return;
}

if (args is not [string outputFolder]) {
    outputFolder = "bin";
}

AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots2)
    .SpinnerStyle(Style.Parse("slateblue1 bold"))
    .Start("Compiling...", ctx => {
        foreach (var romfsFolderPath in gamePaths) {
            int versionNumber = romfsFolderPath.GetRomfsVersion();
            string output = Path.Combine(outputFolder, versionNumber.ToString());

            ctx.Status = $"Compiling {versionNumber} to '{output}'";

            using Compiler compiler = new(romfsFolderPath, output);
            compiler.Compile(ctx);

            AnsiConsole.MarkupLine($"[springgreen1]Compilation of {versionNumber} completed.[/]");
        }
    });