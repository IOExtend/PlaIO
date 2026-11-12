using System.Numerics;

namespace PlaIO.Internal;

/// <summary>The size class kept in the low 4 bits of the block flags. The formula matches the classes observed in real
/// files; for sizes not observed it applies the same rule.</summary>
internal static class SizeClass
{
    public static int T(ulong v) => v == 0 ? 0 : BitOperations.Log2(v) / 2;

    public static int Of(ulong packed)
    {
        if (packed > 0xFFFFFFFF) return 15;
        ulong q = packed >> 2;
        if (q <= 0xFF) return T(q);
        if (q <= 0xFFFF) return T((packed >> 10) & 0xFF) + 4;
        if (q <= 0xFFFFFF) return T((packed >> 18) & 0xFF) + 8;
        return T((packed >> 26) & 0xFF) + 12;
    }

    /// <summary>Width in bytes of the packed-size field for a class.</summary>
    public static int FieldWidth(int sizeClass) => sizeClass <= 6 ? 2 : sizeClass <= 14 ? 4 : 8;
}
