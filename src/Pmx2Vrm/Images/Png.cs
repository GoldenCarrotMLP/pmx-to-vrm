using System.Buffers.Binary;
using System.IO.Compression;

namespace Pmx2Vrm.Images;

/// <summary>Minimal PNG encoder (RGBA8) and decoder (all standard color types and depths, interlaced too).</summary>
public static class Png
{
    static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static bool IsPng(ReadOnlySpan<byte> d) => d.Length >= 8 && d[..8].SequenceEqual(Signature);

    public static byte[] Encode(RgbaImage img)
    {
        var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            int stride = img.Width * 4;
            var line = new byte[stride + 1];
            var prev = new byte[stride];
            for (int y = 0; y < img.Height; y++)
            {
                // Filter "Up" (2): cheap and compresses texture rows well.
                line[0] = 2;
                int o = y * stride;
                for (int i = 0; i < stride; i++)
                {
                    byte cur = img.Pixels[o + i];
                    line[i + 1] = (byte)(cur - prev[i]);
                    prev[i] = cur;
                }
                z.Write(line, 0, line.Length);
            }
        }
        var outMs = new MemoryStream();
        outMs.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, img.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), img.Height);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        Chunk(outMs, "IHDR", ihdr);
        Chunk(outMs, "IDAT", raw.ToArray());
        Chunk(outMs, "IEND", Array.Empty<byte>());
        return outMs.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var hdr = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(hdr, data.Length);
        for (int i = 0; i < 4; i++) hdr[4 + i] = (byte)type[i];
        s.Write(hdr);
        s.Write(data);
        uint crc = Crc32.Update(0xFFFFFFFF, hdr.AsSpan(4, 4));
        crc = Crc32.Update(crc, data) ^ 0xFFFFFFFF;
        var c = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc);
        s.Write(c);
    }

    public static RgbaImage Decode(byte[] d)
    {
        if (!IsPng(d)) throw new InvalidDataException("Not a PNG.");
        int pos = 8;
        int w = 0, h = 0, depth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null, trns = null;
        var idat = new MemoryStream();
        while (pos + 8 <= d.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(d, pos + 4, 4);
            int dataStart = pos + 8;
            if (len < 0 || dataStart + len > d.Length) throw new InvalidDataException("Truncated PNG chunk.");
            var data = d.AsSpan(dataStart, len);
            switch (type)
            {
                case "IHDR":
                    w = BinaryPrimitives.ReadInt32BigEndian(data);
                    h = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    depth = data[8]; colorType = data[9]; interlace = data[12];
                    break;
                case "PLTE": palette = data.ToArray(); break;
                case "tRNS": trns = data.ToArray(); break;
                case "IDAT": idat.Write(data); break;
            }
            pos = dataStart + len + 4;
            if (type == "IEND") break;
        }
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"PNG color type {colorType}.") };
        idat.Position = 0;
        byte[] raw;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        using (var o = new MemoryStream())
        {
            z.CopyTo(o);
            raw = o.ToArray();
        }
        var img = new RgbaImage(w, h);
        int bitsPerPixel = channels * depth;
        int bpp = Math.Max(1, bitsPerPixel / 8);
        int rp = 0;
        if (interlace == 0)
        {
            Unfilter(raw, ref rp, w, h, bitsPerPixel, bpp, (x, y, line, lo) => Put(img, x, y, line, lo, depth, colorType, palette, trns), 0, 0, 1, 1);
        }
        else
        {
            int[] sx = { 0, 4, 0, 2, 0, 1, 0 }, sy = { 0, 0, 4, 0, 2, 0, 1 }, dx = { 8, 8, 4, 4, 2, 2, 1 }, dy = { 8, 8, 8, 4, 4, 2, 2 };
            for (int pass = 0; pass < 7; pass++)
            {
                int pw = (w - sx[pass] + dx[pass] - 1) / dx[pass];
                int ph = (h - sy[pass] + dy[pass] - 1) / dy[pass];
                if (pw <= 0 || ph <= 0) continue;
                Unfilter(raw, ref rp, pw, ph, bitsPerPixel, bpp, (x, y, line, lo) => Put(img, x, y, line, lo, depth, colorType, palette, trns), sx[pass], sy[pass], dx[pass], dy[pass]);
            }
        }
        return img;
    }

    delegate void PixelSink(int x, int y, byte[] line, int pixelIndex);

    static void Unfilter(byte[] raw, ref int rp, int w, int h, int bitsPerPixel, int bpp, PixelSink sink, int ox, int oy, int sx, int sy)
    {
        int stride = (w * bitsPerPixel + 7) / 8;
        var prev = new byte[stride];
        var cur = new byte[stride];
        for (int y = 0; y < h; y++)
        {
            if (rp + 1 + stride > raw.Length) throw new InvalidDataException("Truncated PNG data.");
            byte f = raw[rp++];
            Buffer.BlockCopy(raw, rp, cur, 0, stride);
            rp += stride;
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                int add = f switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"PNG filter {f}."),
                };
                cur[i] = (byte)(cur[i] + add);
            }
            for (int x = 0; x < w; x++) sink(ox + x * sx, oy + y * sy, cur, x);
            (prev, cur) = (cur, prev);
        }
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    static int Sample(byte[] line, int index, int depth)
    {
        switch (depth)
        {
            case 8: return line[index];
            case 16: return line[index * 2]; // high byte
            default:
                int bit = index * depth;
                int v = (line[bit >> 3] >> (8 - depth - (bit & 7))) & ((1 << depth) - 1);
                return v;
        }
    }

    static byte Scale(int v, int depth) => depth switch
    {
        8 or 16 => (byte)v,
        _ => (byte)(v * 255 / ((1 << depth) - 1)),
    };

    static void Put(RgbaImage img, int x, int y, byte[] line, int p, int depth, int colorType, byte[]? palette, byte[]? trns)
    {
        switch (colorType)
        {
            case 0:
            {
                int raw = depth == 16 ? (line[p * 2] << 8) | line[p * 2 + 1] : Sample(line, p, depth);
                byte g = Scale(depth == 16 ? raw >> 8 : raw, depth);
                byte a = 255;
                if (trns != null && trns.Length >= 2 && ((trns[0] << 8) | trns[1]) == raw) a = 0;
                img.Set(x, y, g, g, g, a);
                break;
            }
            case 2:
            {
                int n = depth == 16 ? 6 : 3;
                byte r = line[p * n], g = line[p * n + (depth == 16 ? 2 : 1)], b = line[p * n + (depth == 16 ? 4 : 2)];
                byte a = 255;
                if (trns != null && trns.Length >= 6)
                {
                    int tr = depth == 16 ? (trns[0] << 8) | trns[1] : trns[1];
                    int tg = depth == 16 ? (trns[2] << 8) | trns[3] : trns[3];
                    int tb = depth == 16 ? (trns[4] << 8) | trns[5] : trns[5];
                    int cr = depth == 16 ? (line[p * 6] << 8) | line[p * 6 + 1] : r;
                    int cg = depth == 16 ? (line[p * 6 + 2] << 8) | line[p * 6 + 3] : g;
                    int cb = depth == 16 ? (line[p * 6 + 4] << 8) | line[p * 6 + 5] : b;
                    if (cr == tr && cg == tg && cb == tb) a = 0;
                }
                img.Set(x, y, r, g, b, a);
                break;
            }
            case 3:
            {
                int i = Sample(line, p, depth);
                if (palette == null || i * 3 + 2 >= palette.Length) { img.Set(x, y, 0, 0, 0, 255); break; }
                byte a = trns != null && i < trns.Length ? trns[i] : (byte)255;
                img.Set(x, y, palette[i * 3], palette[i * 3 + 1], palette[i * 3 + 2], a);
                break;
            }
            case 4:
            {
                int n = depth == 16 ? 4 : 2;
                byte g = line[p * n], a = line[p * n + (depth == 16 ? 2 : 1)];
                img.Set(x, y, g, g, g, a);
                break;
            }
            case 6:
            {
                int n = depth == 16 ? 8 : 4, s = depth == 16 ? 2 : 1;
                img.Set(x, y, line[p * n], line[p * n + s], line[p * n + 2 * s], line[p * n + 3 * s]);
                break;
            }
        }
    }
}

static class Crc32
{
    static readonly uint[] Table = MakeTable();

    static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
