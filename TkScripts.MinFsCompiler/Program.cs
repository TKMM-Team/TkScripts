// See https://aka.ms/new-console-template for more information
    
// ReSharper disable StringLiteralTypo

using System.IO.Hashing;
using System.Reflection;
using CommunityToolkit.HighPerformance;
using Revrs.Extensions;
using TkScripts.MinFsCompiler.Extensions;
using TotkCommon.Extensions;

string version = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion.Split('+')[0] ?? "Undefined";

Console.WriteLine($"""
    TotK Minimized FileSystem Compiler [Version {version}]
    (c) TKMM-Team. MIT.
    """);

const int blockSize = 1000;

List<(ulong Id, string FilePath)> collection = [];

string romfs = args[0];
foreach (string filePath in Directory.GetFiles(romfs, "*.*", SearchOption.AllDirectories)) {
    var relativeFilePath = Path.GetRelativePath(romfs, filePath).AsSpan();
    relativeFilePath.ReplaceInline('\\', '/');
    
    var name = filePath.ToCanonical(relativeFilePath);
    var ext = Path.GetExtension(name);
    
    // Include all top-level diffable file extensions0.
    if (ext is ".genvb" or ".bfarc" or ".sarc" or ".pack" or ".bkres" or ".ta" or ".blarc" or ".byml" or ".bgyml"
            or ".bntx" or ".bars" || name is "System/RegionLangMask.txt") {
        collection.Add(
            (Id: XxHash64.HashToUInt64(relativeFilePath.Cast<char, byte>()), filePath)
        );
    }
}

(ulong Id, string)[] pending = [.. collection.OrderBy(x => x.Id)];

string outputFolder = args[1];
Directory.CreateDirectory(outputFolder);

string metadata = Path.Combine(outputFolder, "__meta__");
using var metadataFs = File.Create(metadata);

int blockCount = pending.Length / blockSize;

for (int i = 0; i < blockCount; i++) {
    int baseIndex = i * blockSize;
    Collect(pending.AsSpan()[baseIndex..(baseIndex + blockSize)]);
}

Collect(pending.AsSpan()[(blockCount * blockSize)..]);
return;

void Collect(Span<(ulong Id, string File)> values)
{
    ulong lastId = values[^1].Id;
    string output = Path.Combine(outputFolder, lastId.ToString());

    using var fs = File.Create(output);
    foreach (var (id, file) in values) {
        using var read = File.OpenRead(file);
        int size = Convert.ToInt32(read.Length);

        metadataFs.Write(id);
        metadataFs.Write(lastId);
        metadataFs.Write(fs.Position);
        metadataFs.Write(size);

        read.CopyTo(fs, size);
    }
}