using PlaIO.Internal;
using Xunit;

namespace PlaIO.Tests;

public class SizeClassTests
{
    [Theory]
    [InlineData(0ul, 0)]
    [InlineData(3ul, 0)]
    [InlineData(4ul, 0)]
    [InlineData(15ul, 0)]
    [InlineData(16ul, 1)]
    [InlineData(63ul, 1)]
    [InlineData(64ul, 2)]
    [InlineData(255ul, 2)]
    [InlineData(256ul, 3)]
    [InlineData(1023ul, 3)]
    [InlineData(1024ul, 4)]
    [InlineData(0x3FFFFul, 7)]
    [InlineData(0x40000ul, 8)]
    [InlineData(0x3FFFFFFul, 11)]
    [InlineData(0x4000000ul, 12)]
    [InlineData(0xFFFFFFFFul, 14)]
    [InlineData(0x100000000ul, 15)]
    [InlineData(ulong.MaxValue, 15)]
    public void ClassOfPackedSize(ulong packed, int expected) => Assert.Equal(expected, SizeClass.Of(packed));

    [Theory]
    [InlineData(0, 2)]
    [InlineData(6, 2)]
    [InlineData(7, 4)]
    [InlineData(14, 4)]
    [InlineData(15, 8)]
    public void FieldWidthByClass(int sizeClass, int width) => Assert.Equal(width, SizeClass.FieldWidth(sizeClass));
}
