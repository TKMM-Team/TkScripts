using System.IO.Hashing;
using CommunityToolkit.HighPerformance;
using Spectre.Console;
using TkScripts.MinFsCompiler.Extensions;
using TotkCommon.Extensions;

namespace TkScripts.MinFsCompiler;

public class Compiler : IDisposable, IAsyncDisposable
{
    private const int BlockSize = 1000;
    
    private readonly List<(ulong Id, string FilePath)> _targets = [];
    private readonly string _romfs;
    private readonly string _outputFolder;
    private readonly FileStream _metadataFs;

    public Compiler(string romfs, string outputFolder)
    {
        _romfs = romfs;
        _outputFolder = outputFolder;
        
        Directory.CreateDirectory(outputFolder);
        string metadata = Path.Combine(_outputFolder, "__meta__");
        _metadataFs = File.Create(metadata);
    }

    public void Compile(StatusContext ctx)
    {
        AnsiConsole.MarkupLine("[deepskyblue1]Collecting targets[/] :ant:");
        
        foreach (string filePath in Directory.GetFiles(_romfs, "*.*", SearchOption.AllDirectories)) {
            var relativeFilePath = Path.GetRelativePath(_romfs, filePath).AsSpan();
            relativeFilePath.ReplaceInline('\\', '/');
    
            var name = filePath.ToCanonical(relativeFilePath);
            var ext = Path.GetExtension(name);
    
            // Include all top-level diffable file extensions0.
            if (ext is ".genvb" or ".bfarc" or ".sarc" or ".pack" or ".bkres" or ".ta" or ".blarc" or ".byml" or ".bgyml"
                    or ".bntx" or ".bars" || name is "System/RegionLangMask.txt") {
                _targets.Add(
                    (Id: XxHash64.HashToUInt64(relativeFilePath.Cast<char, byte>()), filePath)
                );
            }
        }
        
        AnsiConsole.MarkupLine("[springgreen1]Targets collected[/] :check_mark:");
        AnsiConsole.MarkupLine("[deepskyblue1]Sorting results[/] :file_cabinet:");
        
        (ulong Id, string)[] targets = [.. _targets.OrderBy(x => x.Id)];
        
        AnsiConsole.MarkupLine("[springgreen1]Sorting complete[/] :check_mark:");
        AnsiConsole.MarkupLine("[deepskyblue1]Calculating block count[/]");

        int blockCount = targets.Length / BlockSize;

        for (int i = 0; i < blockCount;) {
            ctx.Status = $"[slateblue1]Compiling block {i++}/{blockCount}...[/]";
            int baseIndex = i * BlockSize;
            Collect(targets.AsSpan()[baseIndex..(baseIndex + BlockSize)]);
        }

        Collect(targets.AsSpan()[(blockCount * BlockSize)..]);
        AnsiConsole.MarkupLine("[springgreen1]Compilation complete[/] :check_mark:");
    }
    
    private void Collect(Span<(ulong Id, string File)> values)
    {
        ulong lastId = values[^1].Id;
        string output = Path.Combine(_outputFolder, lastId.ToString());

        using var fs = File.Create(output);
        foreach (var (id, file) in values) {
            using var read = File.OpenRead(file);
            int size = Convert.ToInt32(read.Length);

            _metadataFs.Write(id);
            _metadataFs.Write(lastId);
            _metadataFs.Write(fs.Position);
            _metadataFs.Write(size);

            read.CopyTo(fs, size);
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _metadataFs.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _metadataFs.DisposeAsync();
    }
}