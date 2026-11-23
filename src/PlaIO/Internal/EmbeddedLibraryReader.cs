using System.Buffers.Binary;
using System.Text;
using PlaIO.Model;

namespace PlaIO.Internal;

internal static class EmbeddedLibraryReader
{
    private static readonly Guid AssetClass = new("6faaf82c-c131-4f8b-b1d9-9706144935dd");
    private static readonly Encoding AssetText = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);

    public static PlaEmbeddedLibraryCatalog Read(PlaDocument document, IReadOnlyList<PlaObjectClass> classes)
    {
        var kinds = classes.Where(c => c.Guid == AssetClass).Select(c => c.Number).ToHashSet();
        if (kinds.Count == 0) return new([], []);
        var standalone = document.Blocks.Where(IsAsset).ToArray();
        var parts = new List<PlaEmbeddedLibraryPart>();
        var unreadable = new SortedSet<long>();
        int index = 0;
        foreach (var pool in document.FindCompressedPools())
        {
            while (index < standalone.Length && standalone[index].Offset < pool.Offset)
                ReadStandalone(standalone[index++]);
            int first = index;
            while (index < standalone.Length && standalone[index].Offset < pool.End)
                index++;
            if (first == index && !StartsWithAsset(pool)) continue;

            byte[] decoded;
            try
            {
                decoded = pool.Decode();
            }
            catch (InvalidDataException)
            {
                unreadable.Add(pool.Offset);
                for (int i = first; i < index; i++) ReadStandalone(standalone[i]);
                continue;
            }
            // A physical index can see blocks through stored frames, even when their headers or payloads span frames.
            using var stream = new MemoryStream(decoded, writable: false);
            foreach (var block in new BlockScanner(stream, decoded.Length).Scan(startOffset: 0).Where(IsAsset))
            {
                var packed = decoded.AsSpan((int)block.PayloadOffset, (int)block.PackedSize);
                try
                {
                    ReadPart(ReadPooledPayload(block, packed), block.Guid!.Value, pool.Offset);
                }
                catch (InvalidDataException)
                {
                    unreadable.Add(pool.Offset);
                }
            }
        }
        while (index < standalone.Length) ReadStandalone(standalone[index++]);
        return new(parts.AsReadOnly(), unreadable.ToList().AsReadOnly());

        bool IsAsset(PlaBlock block) => kinds.Contains(block.Kind) && block.Tag == "BL" && block.Version == 1 &&
            block.Guid is { } id && id != Guid.Empty;

        bool StartsWithAsset(CompressedPool pool)
        {
            try
            {
                var prefix = document.ReadPoolPrefix(pool.Offset, FdbFormat.MaxBlockHeaderLength);
                return BlockHeaderParser.TryParse(prefix, 0, pool.TotalDecompressed, out var block) && IsAsset(block);
            }
            catch (InvalidDataException)
            {
                // No indexed asset points to this pool, so its contents cannot identify it as an asset pool.
                return false;
            }
        }

        void ReadStandalone(PlaBlock block)
        {
            try
            {
                ReadPart(document.ReadDirectoryPayload(block), block.Guid!.Value, block.Offset);
            }
            catch (InvalidDataException)
            {
                unreadable.Add(block.Offset);
            }
        }

        void ReadPart(byte[]? body, Guid id, long offset)
        {
            if (body is null || !TryReadPart(body, id, out var part))
                unreadable.Add(offset);
            else if (part is not null)
                parts.Add(part);
        }
    }

    private static byte[]? ReadPooledPayload(PlaBlock block, ReadOnlySpan<byte> packed)
    {
        if (block.Mode == PlaBlockMode.Raw) return packed.ToArray();
        if (block.Mode == PlaBlockMode.Lz) return QuickLzLevelOne.ReadFrames(packed, (int)block.UnpackedSize);
        if ((block.UnpackedSize + 7) / 8 > packed.Length) return null;
        var output = new byte[block.UnpackedSize];
        PayloadDecoder.DecodeZeroMask(packed, output);
        return output;
    }

    private static bool TryReadPart(byte[] body, Guid id, out PlaEmbeddedLibraryPart? part)
    {
        part = null;
        var data = body.AsSpan();
        const int frame = 39;
        if (data.Length < frame || new Guid(data[..16]) != AssetClass) return false;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(data[(frame - 4)..]);
        if (length > data.Length - frame) return false;
        var asset = data.Slice(frame, (int)length);
        if (!asset.StartsWith(new byte[] { 1, 0, 3, 0, 0 })) return false;
        asset = asset[5..];
        if (!TryReadText(ref asset, out var name) || !TryReadText(ref asset, out var type)) return false;
        if (type != "model/vnd.gs-gdl") return true;

        const int archiveHeaderSize = 32;
        if (name.Length == 0 || asset.Length < archiveHeaderSize || !asset.StartsWith("MYSGCASG"u8)) return false;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(asset[28..]);
        var gsm = asset[archiveHeaderSize..];
        if (size != gsm.Length || gsm.Length < 128 || !gsm.StartsWith("WW.\0"u8) ||
            !gsm[40..].StartsWith("MYS"u8) || gsm[43] is not ((byte)'D' or (byte)'G' or (byte)'M' or (byte)'R' or (byte)'W') ||
            !(gsm[44..48].SequenceEqual("CASG"u8) || BinaryPrimitives.ReadUInt32LittleEndian(gsm[44..]) == 0) ||
            !gsm[48..].StartsWith("DAEH"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(gsm[52..]) != 128) return false;
        uint sections = BinaryPrimitives.ReadUInt32LittleEndian(gsm[36..]);
        if (sections == 0 || sections > (gsm.Length - 128) / 16 ||
            BinaryPrimitives.ReadUInt32LittleEndian(gsm[56..]) != sections * 16) return false;
        for (int i = 128; i < 128 + sections * 16; i += 16)
        {
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(gsm[(i + 4)..]);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(gsm[(i + 8)..]);
            if (offset < 128 + sections * 16 || offset > size || count > size - offset) return false;
        }
        var family = new Guid(gsm[72..88]);
        var revision = new Guid(gsm[88..104]);
        if (family == Guid.Empty || revision == Guid.Empty) return false;
        int offsetInBody = frame + (int)length - gsm.Length;
        part = new(id, name, family, revision, body.AsMemory(offsetInBody, gsm.Length));
        return true;
    }

    private static bool TryReadText(ref Span<byte> frame, out string value)
    {
        value = string.Empty;
        if (frame.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(frame) != 1) return false;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(frame[2..]);
        if (count > (frame.Length - 6) / 2) return false;
        int bytes = (int)count * 2;
        try
        {
            value = AssetText.GetString(frame.Slice(6, bytes));
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        frame = frame[(6 + bytes)..];
        return true;
    }
}
