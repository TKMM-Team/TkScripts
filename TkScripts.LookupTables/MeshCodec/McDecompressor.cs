using TotkCommon;

namespace TkScripts.LookupTables.MeshCodec;

internal static class McDecompressor
{
    private static ReadOnlySpan<byte> ZstdFrameMagic => [0x28, 0xB5, 0x2F, 0xFD];
    private static ReadOnlySpan<byte> BfresSwitchMagic => "FRES"u8;

    private const uint McPkMagicLe = 0x4B50434D; // "MCPK"
    private const int HeaderSize = 0xC;

    public static bool IsMeshCodec(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BitConverter.ToUInt32(data[..4]) == McPkMagicLe;

    public static bool IsLikelySwitchBfres(ReadOnlySpan<byte> decompressed) =>
        decompressed.Length >= 4 && decompressed.StartsWith(BfresSwitchMagic);

    public static void LoadDictionaries(string zsDicPackPath) =>
        Zstd.Shared.LoadDictionaries(zsDicPackPath);

    public static byte[] DecompressToBfres(ReadOnlySpan<byte> meshCodecFile)
    {
        if (meshCodecFile.Length < HeaderSize) {
            throw new InvalidDataException("MCPK file is too small.");
        }

        if (!IsMeshCodec(meshCodecFile)) {
            throw new InvalidDataException("Expected MCPK magic.");
        }

        var flags = BitConverter.ToInt32(meshCodecFile[8..12]);
        var decompressedSizeU = (uint)((flags >> 5) << (flags & 0xF));

        if (decompressedSizeU == 0) {
            throw new InvalidDataException("MCPK header declares zero decompressed size.");
        }

        if (decompressedSizeU > (uint)int.MaxValue) {
            throw new InvalidDataException("MCPK decompressed size overflow.");
        }

        var payload = meshCodecFile[HeaderSize..];
        if (payload.IsEmpty) {
            throw new InvalidDataException("MCPK ZSTD payload is empty.");
        }

        var framed = new byte[ZstdFrameMagic.Length + payload.Length];
        ZstdFrameMagic.CopyTo(framed);
        payload.CopyTo(framed.AsSpan(ZstdFrameMagic.Length));

        var frameSize = GetZstdFrameSize(framed);

        var output = new byte[(int)decompressedSizeU];
        Zstd.Shared.Decompress(framed.AsSpan(0, frameSize), output);
        return output;
    }

    private static int GetZstdFrameSize(ReadOnlySpan<byte> framed)
    {
        if (framed.Length < 6) {
            return framed.Length;
        }

        var pos = 4;

        var fhd = framed[pos++];
        var fcsFlag = fhd >> 6;
        var singleSegment = (fhd & 0x20) != 0;
        var hasChecksum = (fhd & 0x04) != 0;

        if (!singleSegment) {
            pos++;
        }

        pos += (fhd & 3) switch { 1 => 1, 2 => 2, 3 => 4, _ => 0 };
        pos += (fcsFlag, singleSegment) switch {
            (0, true) => 1,
            (1, _)    => 2,
            (2, _)    => 4,
            (3, _)    => 8,
            _         => 0
        };

        while (pos + 3 <= framed.Length) {
            var hdr = framed[pos] | (framed[pos + 1] << 8) | (framed[pos + 2] << 16);
            var lastBlock = (hdr & 1) != 0;
            var blockType = (hdr >> 1) & 3;
            var blockSize = hdr >> 3;
            pos += 3;
            pos += blockType == 1 ? 1 : blockSize;
            if (lastBlock) {
                break;
            }
        }

        if (hasChecksum) {
            pos += 4;
        }

        return Math.Min(pos, framed.Length);
    }
}
