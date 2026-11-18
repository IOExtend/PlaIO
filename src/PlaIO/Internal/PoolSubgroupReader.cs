namespace PlaIO.Internal;

internal static class PoolSubgroupReader
{
    public static IReadOnlyList<PoolSubgroup> Read(byte[] pool)
    {
        var subgroups = new List<PoolSubgroup>();
        int pos = 0;
        while (pool.Length - pos >= FdbFormat.MinBlockHeaderLength)
        {
            int next = pool.AsSpan(pos).IndexOf("BL"u8);
            if (next < 0) break;
            pos += next;
            if (!BlockHeaderParser.TryParse(pool.AsSpan(pos), pos, pool.Length, out var header, poolSubgroup: true))
            {
                pos++;
                continue;
            }

            ReadOnlySpan<byte> payload = pool.AsSpan((int)header.PayloadOffset, (int)header.PackedSize);
            byte[] data;
            bool partial;
            switch (header.Mode)
            {
                case PlaBlockMode.Raw:
                    data = payload.ToArray();
                    partial = false;
                    break;
                case PlaBlockMode.ZeroMask:
                    data = new byte[header.UnpackedSize];
                    PayloadDecoder.DecodeZeroMask(payload, data, out _, out partial);
                    break;
                default:
                    data = [];
                    partial = true;
                    break;
            }
            subgroups.Add(new PoolSubgroup(header, data, partial));
            pos = (int)header.End;
        }
        return subgroups.AsReadOnly();
    }
}
