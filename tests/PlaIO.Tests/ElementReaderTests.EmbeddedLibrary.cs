using System.Buffers.Binary;
using System.Text;
using PlaIO.Model;
using Xunit;

namespace PlaIO.Tests;

public partial class ElementReaderTests
{
    [Theory]
    [InlineData(PlaBlockMode.Raw, false)]
    [InlineData(PlaBlockMode.ZeroMask, false)]
    [InlineData(PlaBlockMode.Lz, false)]
    [InlineData(PlaBlockMode.Raw, true)]
    [InlineData(PlaBlockMode.ZeroMask, true)]
    [InlineData(PlaBlockMode.Lz, true)]
    public void ReadsValidatedEmbeddedLibraryAssets(PlaBlockMode mode, bool pooled)
    {
        Guid assetClass = new("6faaf82c-c131-4f8b-b1d9-9706144935dd");
        Guid assetId = Guid.NewGuid(), family = Guid.NewGuid(), revision = Guid.NewGuid();
        Guid unknownId = Guid.NewGuid(), invalidId = Guid.NewGuid(), shortId = Guid.NewGuid(), invalidTextId = Guid.NewGuid();
        const ushort assetKind = 19;
        var gsm = new byte[148];
        "WW.\0"u8.CopyTo(gsm);
        "MYSDCASGDAEH"u8.CopyTo(gsm.AsSpan(40));
        if (mode == PlaBlockMode.ZeroMask)
        {
            gsm[43] = (byte)'W';
            gsm.AsSpan(44, 4).Clear();
        }
        BinaryPrimitives.WriteUInt32LittleEndian(gsm.AsSpan(36), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(gsm.AsSpan(52), 128);
        BinaryPrimitives.WriteUInt32LittleEndian(gsm.AsSpan(56), 16);
        Assert.True(family.TryWriteBytes(gsm.AsSpan(72)));
        Assert.True(revision.TryWriteBytes(gsm.AsSpan(88)));
        "TEST"u8.CopyTo(gsm.AsSpan(128));
        BinaryPrimitives.WriteUInt32LittleEndian(gsm.AsSpan(132), 144);
        BinaryPrimitives.WriteUInt32LittleEndian(gsm.AsSpan(136), 4);
        "DATA"u8.CopyTo(gsm.AsSpan(144));
        var badSection = (byte[])gsm.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badSection.AsSpan(136), 5);
        const string name = "Дверь.gsm";
        byte[][] assets =
        [
            Asset(mode, assetId, Text(name), "model/vnd.gs-gdl", gsm),
            Asset(PlaBlockMode.Raw, unknownId, Text("finish.jpg"), "image/jpeg", [1]),
            Asset(PlaBlockMode.Raw, invalidId, Text("bad.gsm"), "model/vnd.gs-gdl", badSection),
            Asset(PlaBlockMode.Raw, shortId, Text("short.gsm"), "model/vnd.gs-gdl", gsm[..100]),
            Asset(PlaBlockMode.Raw, invalidTextId, [1, 0, 1, 0, 0, 0, 0, 0xD8], "model/vnd.gs-gdl", gsm),
        ];
        byte[] classes = ClassTable((assetKind, assetClass));
        using var doc = pooled
            ? Open(classes, Synthetic.QuickLzPool(assets.SelectMany(asset => new[] { asset[..1], asset[1..^3], asset[^3..] })
                .Select(part => ((uint)part.Length, Synthetic.QuickLzFrame(part, part.Length, 0x46))).ToArray()))
            : Open([classes, .. assets]);

        var result = doc.ReadEmbeddedLibraryCatalog();
        var part = Assert.Single(result.Parts);
        Assert.Equal(assetId, part.AssetGuid);
        Assert.Equal(name, part.Name);
        Assert.Equal(family, part.LibraryPartGuid);
        Assert.Equal(revision, part.RevisionGuid);
        Assert.Equal(gsm, part.Data.ToArray());
        if (pooled)
        {
            Assert.DoesNotContain(doc.Blocks, b => b.Kind == assetKind);
            Assert.Equal(Assert.Single(doc.FindCompressedPools()).Offset, Assert.Single(result.UnreadableAssetOffsets));
        }
        else
        {
            Assert.Equal(doc.Blocks.Where(b => b.Guid == invalidId || b.Guid == shortId || b.Guid == invalidTextId).Select(b => b.Offset),
                result.UnreadableAssetOffsets);
        }

        byte[] Asset(PlaBlockMode storage, Guid id, byte[] title, string mediaType, byte[] content)
        {
            byte[] archive = new byte[32];
            "MYSGCASG"u8.CopyTo(archive);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(28), (uint)content.Length);
            byte[] fields = Synthetic.Concat([1, 0, 3, 0, 0], title, Text(mediaType), archive, content);
            byte[] record = new byte[39];
            Assert.True(assetClass.TryWriteBytes(record));
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(35), (uint)fields.Length);
            byte[] body = Synthetic.Concat(record, fields);
            var packed = storage switch
            {
                PlaBlockMode.Lz => Synthetic.QuickLzFrame(body, body.Length, 0x46),
                PlaBlockMode.ZeroMask => Synthetic.ZeroMask(body),
                _ => body,
            };
            return Synthetic.Block(storage, packed, ver: 1, unpacked: (uint)body.Length, guid: id, kind: assetKind);
        }

        static byte[] Text(string value)
        {
            var field = new byte[6 + value.Length * 2];
            BinaryPrimitives.WriteUInt16LittleEndian(field, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(field.AsSpan(2), (uint)value.Length);
            Encoding.Unicode.GetBytes(value, field.AsSpan(6));
            return field;
        }
    }
}
