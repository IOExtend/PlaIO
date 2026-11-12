using Xunit;

namespace PlaIO.Tests;

public class FileKindTests
{
    [Fact]
    public void FdbSignatureIsFdb() => Assert.Equal(PlaFileKind.Fdb, PlaFile.DetectKind("ROF FDB \x10\0"u8));

    [Fact]
    public void BimxSignatureFollowedByAnythingIsBimx()
    {
        Assert.Equal(PlaFileKind.Bimx, PlaFile.DetectKind("BIMx"u8));
        Assert.Equal(PlaFileKind.Bimx, PlaFile.DetectKind(Synthetic.Bimx(Synthetic.SimplePng(), [])));
    }

    public static TheoryData<byte[]> UnknownHeads() => new()
    {
        Array.Empty<byte>(),
        "BIM"u8.ToArray(),
        "ROF FDB"u8.ToArray(),
        "ROF FDX "u8.ToArray(),
        "bimx"u8.ToArray(),
        "rof fdb "u8.ToArray(),
        Synthetic.RandomBytes(new Random(7), 64),
    };

    [Theory]
    [MemberData(nameof(UnknownHeads))]
    public void AnythingElseIsUnknown(byte[] head) => Assert.Equal(PlaFileKind.Unknown, PlaFile.DetectKind(head));

    [Fact]
    public void PathOverloadAgreesWithSpanOverload()
    {
        byte[][] contents = [Synthetic.Fdb(), Synthetic.Bimx(Synthetic.SimplePng(), []), "BIM"u8.ToArray(), []];
        foreach (var content in contents)
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(path, content);
                Assert.Equal(PlaFile.DetectKind(content), PlaFile.DetectKind(path));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void PathOverloadPropagatesIoErrors() =>
        Assert.Throws<FileNotFoundException>(() => PlaFile.DetectKind(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pla")));
}
