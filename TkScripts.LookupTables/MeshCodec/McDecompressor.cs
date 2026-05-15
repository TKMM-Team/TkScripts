using TotkCommon;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace TkScripts.LookupTables.MeshCodec;

internal static class McDecompressor
{
    private const uint McPkMagicLe = 0x4B50434D; // "MCPK"
    private const int HeaderSize = 0xC;

    private static bool IsMeshCodec(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BitConverter.ToUInt32(data[..4]) == McPkMagicLe;

    public static void LoadDictionaries(string zsDicPackPath) =>
        Zstd.Shared.LoadDictionaries(zsDicPackPath);

    public static byte[] Decompress(ReadOnlySpan<byte> meshCodecFile)
    {
        if (meshCodecFile.Length < HeaderSize) {
            throw new InvalidDataException("MCPK file is too small.");
        }

        if (!IsMeshCodec(meshCodecFile)) {
            throw new InvalidDataException("Expected MCPK magic.");
        }

        var flags = BitConverter.ToInt32(meshCodecFile[8..12]);
        var decompressedSize = (uint)((flags >> 5) << (flags & 0xF));

        if (decompressedSize == 0) {
            throw new InvalidDataException("MCPK header declares zero decompressed size.");
        }

        var output = new byte[(int)decompressedSize];

        using var decompressor = new Decompressor();
        decompressor.SetParameter(ZSTD_dParameter.ZSTD_d_experimentalParam1, (int)ZSTD_format_e.ZSTD_f_zstd1_magicless);

        try {
            decompressor.Unwrap(meshCodecFile[HeaderSize..], output);
        }
        catch (ZstdException) {
            // padding bytes after the ZSTD frame cause srcSize_wrong; the output is correct
        }

        return output;
    }
}
