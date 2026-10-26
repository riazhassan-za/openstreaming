using System.Buffers.Binary;
using System.Text;

// Wraps Opus packets in Ogg pages (RFC 3533 / RFC 7845) for one client.
// Each client gets its own serial number and granule clock starting at 0,
// so it can join mid-stream while the encoder keeps running.
sealed class OggOpusStream(int serial, int preSkip, int inputSampleRate, int channels)
{
    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        uint r = (uint)i << 24;
        for (int k = 0; k < 8; k++)
            r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
        return r;
    }).ToArray();

    int sequence;
    long granule;

    public byte[] Headers()
    {
        var head = new byte[19];
        Encoding.ASCII.GetBytes("OpusHead").CopyTo(head, 0);
        head[8] = 1; // version
        head[9] = (byte)channels;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), (ushort)preSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), (uint)inputSampleRate);
        // output gain 0, channel mapping family 0 (mono/stereo)

        var vendor = Encoding.ASCII.GetBytes("OpenStreaming");
        var tags = new byte[8 + 4 + vendor.Length + 4];
        Encoding.ASCII.GetBytes("OpusTags").CopyTo(tags, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(tags.AsSpan(8), (uint)vendor.Length);
        vendor.CopyTo(tags, 12);
        // user comment count 0

        return [.. Page([head], 0x02, 0), .. Page([tags], 0, 0)];
    }

    // samplesPerPacket is at 48 kHz, which is what Opus granule positions count.
    public byte[] AudioPage(byte[][] packets, int samplesPerPacket)
    {
        granule += packets.Length * samplesPerPacket;
        return Page(packets, 0, granule);
    }

    byte[] Page(byte[][] packets, byte flags, long granulePos)
    {
        var lacing = new List<byte>();
        foreach (var p in packets)
        {
            int n = p.Length;
            for (; n >= 255; n -= 255)
                lacing.Add(255);
            lacing.Add((byte)n);
        }

        var page = new byte[27 + lacing.Count + packets.Sum(p => p.Length)];
        var s = page.AsSpan();
        Encoding.ASCII.GetBytes("OggS").CopyTo(s);
        page[5] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(s[6..], granulePos);
        BinaryPrimitives.WriteInt32LittleEndian(s[14..], serial);
        BinaryPrimitives.WriteInt32LittleEndian(s[18..], sequence++);
        page[26] = (byte)lacing.Count;
        lacing.CopyTo(page, 27);
        int offset = 27 + lacing.Count;
        foreach (var p in packets)
        {
            p.CopyTo(page, offset);
            offset += p.Length;
        }

        uint crc = 0;
        foreach (var b in page)
            crc = (crc << 8) ^ CrcTable[((crc >> 24) ^ b) & 0xff];
        BinaryPrimitives.WriteUInt32LittleEndian(s[22..], crc);
        return page;
    }
}
