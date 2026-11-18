using System.Buffers.Binary;

namespace PlaIO.Internal;

// QuickLZ 1.5.0 level 1, without streaming. See docs/public-api.md for the public format reference.
internal static class QuickLzLevelOne
{
    public static void Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        Span<int> matches = stackalloc int[4096];
        matches.Fill(-1);
        uint control = 1;
        int written = 0;
        int nextHash = 0;
        bool tail = false;

        while (written < output.Length)
        {
            if (control == 1)
            {
                control = TakeWord(ref input, sizeof(uint));
                if ((control & 0x80000000) == 0)
                    throw new InvalidDataException("A QuickLZ control word has no sentinel bit.");
            }

            if (!tail && (control & 1) != 0)
            {
                uint token = TakeWord(ref input, sizeof(ushort));
                int lengthCode = (int)(token & 15);
                int count = lengthCode == 0 ? (int)TakeWord(ref input, sizeof(byte)) : lengthCode + 2;
                int start = matches[(int)(token >> 4)];
                if (start < 0 || count < 3 || count > output.Length - written)
                    throw new InvalidDataException("A QuickLZ reference has an invalid position or length.");

                int matchStart = written;
                for (int i = 0; i < count; i++)
                    output[written++] = output[start + i];
                Register(matches, output, nextHash, matchStart + 1);
                nextHash = written;
            }
            else
            {
                tail |= output.Length - written <= 10;
                output[written++] = (byte)TakeWord(ref input, sizeof(byte));
                if (!tail && nextHash + 3 <= written)
                {
                    Register(matches, output, nextHash, written - 2);
                    nextHash = written - 2;
                }
            }
            control >>= 1;
        }
    }

    private static void Register(Span<int> matches, ReadOnlySpan<byte> output, int start, int end)
    {
        for (int pos = start; pos < end; pos++)
        {
            int word = output[pos] | (output[pos + 1] << 8) | (output[pos + 2] << 16);
            matches[(word ^ (word >> 12)) & 0xFFF] = pos;
        }
    }

    private static uint TakeWord(ref ReadOnlySpan<byte> input, int width)
    {
        if (width > input.Length)
            throw new InvalidDataException("A QuickLZ frame ends inside a token.");
        uint word = width switch
        {
            sizeof(uint) => BinaryPrimitives.ReadUInt32LittleEndian(input),
            sizeof(ushort) => BinaryPrimitives.ReadUInt16LittleEndian(input),
            _ => input[0],
        };
        input = input[width..];
        return word;
    }
}
