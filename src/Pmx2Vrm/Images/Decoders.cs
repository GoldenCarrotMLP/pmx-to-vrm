using System.Buffers.Binary;

namespace Pmx2Vrm.Images;

public enum ImageKind { Unknown, Png, Jpeg, Bmp, Tga, Dds, Gif }

public static class ImageFormat
{
    /// <summary>Detects the format from the bytes (MMD sphere maps .sph/.spa are usually BMPs, sometimes PNGs).</summary>
    public static ImageKind Detect(byte[] d, string path)
    {
        if (Png.IsPng(d)) return ImageKind.Png;
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return ImageKind.Jpeg;
        if (d.Length >= 2 && d[0] == 'B' && d[1] == 'M') return ImageKind.Bmp;
        if (d.Length >= 4 && d[0] == 'D' && d[1] == 'D' && d[2] == 'S' && d[3] == ' ') return ImageKind.Dds;
        if (d.Length >= 3 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F') return ImageKind.Gif;
        if (Path.GetExtension(path).Equals(".tga", StringComparison.OrdinalIgnoreCase)) return ImageKind.Tga;
        return ImageKind.Unknown;
    }

    /// <summary>Decodes PNG, BMP, TGA and DDS (uncompressed, DXT1/3/5). JPEG and GIF are not decoded (returns null).</summary>
    public static RgbaImage? Decode(byte[] d, ImageKind kind) => kind switch
    {
        ImageKind.Png => Png.Decode(d),
        ImageKind.Bmp => Bmp.Decode(d),
        ImageKind.Tga => Tga.Decode(d),
        ImageKind.Dds => Dds.Decode(d),
        _ => null,
    };
}

public static class Bmp
{
    public static RgbaImage Decode(byte[] d)
    {
        if (d.Length < 26 || d[0] != 'B' || d[1] != 'M') throw new InvalidDataException("Not a BMP.");
        int dataOffset = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(10));
        int hdrSize = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(14));
        int w, h, bpp, compression = 0, colorsUsed = 0;
        uint rm = 0, gm = 0, bm = 0, am = 0;
        if (hdrSize == 12)
        {
            w = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(18));
            h = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(20));
            bpp = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(24));
        }
        else
        {
            w = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(18));
            h = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(22));
            bpp = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(28));
            compression = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(30));
            colorsUsed = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(46));
            if (compression == 3 || compression == 6)
            {
                // Masks follow a 40-byte header, or are part of a V4/V5 header.
                int mo = 14 + 40;
                rm = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(mo));
                gm = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(mo + 4));
                bm = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(mo + 8));
                if (compression == 6 || hdrSize >= 56) am = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(mo + 12));
            }
        }
        bool topDown = h < 0;
        h = Math.Abs(h);
        if (compression != 0 && compression != 3 && compression != 6 && compression != 1 && compression != 2)
            throw new InvalidDataException($"BMP compression {compression} not supported.");

        // Palette
        byte[][]? pal = null;
        if (bpp <= 8)
        {
            int entries = colorsUsed > 0 ? colorsUsed : 1 << bpp;
            int entrySize = hdrSize == 12 ? 3 : 4;
            int po = 14 + hdrSize;
            pal = new byte[entries][];
            for (int i = 0; i < entries; i++)
            {
                int o = po + i * entrySize;
                pal[i] = o + 2 < d.Length ? new[] { d[o + 2], d[o + 1], d[o] } : new byte[3];
            }
        }

        var img = new RgbaImage(w, h);
        if (compression == 1 || compression == 2)
        {
            DecodeRle(d, dataOffset, w, h, compression == 1 ? 8 : 4, pal!, img);
            if (!topDown) img.FlipVertical();
            return img;
        }

        if (bpp == 16 && compression == 0) { rm = 0x7C00; gm = 0x03E0; bm = 0x001F; }
        int stride = ((w * bpp + 31) / 32) * 4;
        bool anyAlpha = false;
        for (int y = 0; y < h; y++)
        {
            int row = dataOffset + y * stride;
            int dy = topDown ? y : h - 1 - y;
            for (int x = 0; x < w; x++)
            {
                byte r, g, b, a = 255;
                switch (bpp)
                {
                    case 1: case 4: case 8:
                    {
                        int bit = x * bpp;
                        int o = row + (bit >> 3);
                        int idx = o < d.Length ? (d[o] >> (8 - bpp - (bit & 7))) & ((1 << bpp) - 1) : 0;
                        var c = idx < pal!.Length ? pal[idx] : new byte[3];
                        r = c[0]; g = c[1]; b = c[2];
                        break;
                    }
                    case 24:
                    {
                        int o = row + x * 3;
                        b = d[o]; g = d[o + 1]; r = d[o + 2];
                        break;
                    }
                    case 16:
                    {
                        uint v = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(row + x * 2));
                        r = Mask(v, rm); g = Mask(v, gm); b = Mask(v, bm);
                        if (am != 0) { a = Mask(v, am); anyAlpha = true; }
                        break;
                    }
                    case 32:
                    {
                        int o = row + x * 4;
                        if (compression == 0) { b = d[o]; g = d[o + 1]; r = d[o + 2]; a = d[o + 3]; anyAlpha |= a != 0; }
                        else
                        {
                            uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
                            r = Mask(v, rm); g = Mask(v, gm); b = Mask(v, bm);
                            if (am != 0) { a = Mask(v, am); anyAlpha |= a != 0; }
                        }
                        break;
                    }
                    default: throw new InvalidDataException($"BMP {bpp} bits per pixel not supported.");
                }
                img.Set(x, dy, r, g, b, a);
            }
        }
        // 32-bit BMPs with an all-zero alpha channel mean "no alpha".
        if (bpp == 32 && !anyAlpha)
            for (int i = 3; i < img.Pixels.Length; i += 4) img.Pixels[i] = 255;
        return img;
    }

    static byte Mask(uint v, uint mask)
    {
        if (mask == 0) return 0;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint max = mask >> shift;
        return (byte)(((v & mask) >> shift) * 255 / max);
    }

    static void DecodeRle(byte[] d, int pos, int w, int h, int bits, byte[][] pal, RgbaImage img)
    {
        int x = 0, y = 0; // y counts rows from the bottom; flipped by the caller
        void Put(int idx)
        {
            if (x < w && y < h)
            {
                var c = idx < pal.Length ? pal[idx] : new byte[3];
                img.Set(x, y, c[0], c[1], c[2], 255);
            }
            x++;
        }
        while (pos + 1 < d.Length)
        {
            int n = d[pos++], v = d[pos++];
            if (n > 0)
            {
                for (int i = 0; i < n; i++) Put(bits == 8 ? v : (i % 2 == 0 ? v >> 4 : v & 15));
            }
            else if (v == 0) { x = 0; y++; }
            else if (v == 1) break;
            else if (v == 2) { if (pos + 1 >= d.Length) break; x += d[pos++]; y += d[pos++]; }
            else
            {
                int bytes = bits == 8 ? v : (v + 1) / 2;
                for (int i = 0; i < v; i++)
                {
                    int b = d[pos + (bits == 8 ? i : i / 2)];
                    Put(bits == 8 ? b : (i % 2 == 0 ? b >> 4 : b & 15));
                }
                pos += bytes + (bytes & 1);
            }
        }
        // The rows were written bottom-up into top-down storage: the caller flips.
    }
}

public static class Tga
{
    public static RgbaImage Decode(byte[] d)
    {
        if (d.Length < 18) throw new InvalidDataException("TGA too short.");
        int idLen = d[0], cmapType = d[1], type = d[2];
        int cmapFirst = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(3));
        int cmapLen = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(5));
        int cmapBits = d[7];
        int w = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(12));
        int h = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(14));
        int bpp = d[16], desc = d[17];
        int pos = 18 + idLen;
        byte[][]? cmap = null;
        if (cmapType == 1)
        {
            int eb = (cmapBits + 7) / 8;
            cmap = new byte[cmapLen][];
            for (int i = 0; i < cmapLen; i++) { cmap[i] = ReadColor(d, pos, cmapBits); pos += eb; }
        }
        bool rle = type >= 9;
        int baseType = rle ? type - 8 : type;
        if (baseType < 1 || baseType > 3) throw new InvalidDataException($"TGA image type {type} not supported.");
        int pb = (bpp + 7) / 8;
        var img = new RgbaImage(w, h);
        int count = w * h, i0 = 0;
        byte[] Pixel(int at)
        {
            if (baseType == 1)
            {
                int idx = pb == 1 ? d[at] : BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at));
                idx -= cmapFirst;
                return cmap != null && idx >= 0 && idx < cmap.Length ? cmap[idx] : new byte[] { 0, 0, 0, 255 };
            }
            if (baseType == 3) { byte g = d[at]; byte a = bpp == 16 ? d[at + 1] : (byte)255; return new[] { g, g, g, a }; }
            return ReadColor(d, at, bpp);
        }
        void Store(int i, byte[] c) { img.Set(i % w, i / w, c[0], c[1], c[2], c[3]); }
        if (!rle)
        {
            for (; i0 < count; i0++) { Store(i0, Pixel(pos)); pos += pb; }
        }
        else
        {
            while (i0 < count && pos < d.Length)
            {
                int hdr = d[pos++];
                int n = (hdr & 0x7F) + 1;
                if ((hdr & 0x80) != 0)
                {
                    var c = Pixel(pos); pos += pb;
                    for (int k = 0; k < n && i0 < count; k++) Store(i0++, c);
                }
                else
                {
                    for (int k = 0; k < n && i0 < count; k++) { Store(i0++, Pixel(pos)); pos += pb; }
                }
            }
        }
        // Default origin is bottom-left; bit 5 set = top-left. Bit 4 = right-to-left.
        if ((desc & 0x20) == 0) img.FlipVertical();
        if ((desc & 0x10) != 0) img.FlipHorizontal();
        int alphaBits = desc & 0x0F;
        if (bpp == 32 && alphaBits == 0)
        {
            // Many tools write 32-bit TGAs with alpha bits = 0 but a real alpha channel; only drop it if it is all zero.
            bool any = false;
            for (int i = 3; i < img.Pixels.Length; i += 4) if (img.Pixels[i] != 0) { any = true; break; }
            if (!any) for (int i = 3; i < img.Pixels.Length; i += 4) img.Pixels[i] = 255;
        }
        return img;
    }

    static byte[] ReadColor(byte[] d, int at, int bits)
    {
        switch (bits)
        {
            case 32: return new[] { d[at + 2], d[at + 1], d[at], d[at + 3] };
            case 24: return new[] { d[at + 2], d[at + 1], d[at], (byte)255 };
            case 15:
            case 16:
            {
                int v = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at));
                byte r = (byte)(((v >> 10) & 31) * 255 / 31), g = (byte)(((v >> 5) & 31) * 255 / 31), b = (byte)((v & 31) * 255 / 31);
                return new[] { r, g, b, (byte)255 };
            }
            case 8: return new[] { d[at], d[at], d[at], (byte)255 };
            default: throw new InvalidDataException($"TGA {bits}-bit colors not supported.");
        }
    }
}

public static class Dds
{
    public static RgbaImage Decode(byte[] d)
    {
        if (d.Length < 128) throw new InvalidDataException("DDS too short.");
        int h = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(12));
        int w = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(16));
        uint pfFlags = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(80));
        string four = System.Text.Encoding.ASCII.GetString(d, 84, 4);
        int bits = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(88));
        uint rm = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(92));
        uint gm = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(96));
        uint bm = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(100));
        uint am = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(104));
        int pos = 128;
        var img = new RgbaImage(w, h);
        if ((pfFlags & 0x4) != 0)
        {
            int blockBytes = four switch { "DXT1" => 8, "DXT3" or "DXT5" => 16, _ => throw new InvalidDataException($"DDS format {four} not supported.") };
            int bw = (w + 3) / 4, bh = (h + 3) / 4;
            if (pos + bw * bh * blockBytes > d.Length) throw new InvalidDataException("DDS data truncated.");
            var block = new byte[64];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    int o = pos + (by * bw + bx) * blockBytes;
                    if (four == "DXT1") ColorBlock(d, o, block, true);
                    else
                    {
                        ColorBlock(d, o + 8, block, false);
                        if (four == "DXT3") Dxt3Alpha(d, o, block); else Dxt5Alpha(d, o, block);
                    }
                    for (int py = 0; py < 4; py++)
                        for (int px = 0; px < 4; px++)
                        {
                            int x = bx * 4 + px, y = by * 4 + py;
                            if (x >= w || y >= h) continue;
                            int q = (py * 4 + px) * 4;
                            img.Set(x, y, block[q], block[q + 1], block[q + 2], block[q + 3]);
                        }
                }
            return img;
        }
        if ((pfFlags & 0x40) == 0 || (bits != 32 && bits != 24 && bits != 16))
            throw new InvalidDataException("DDS pixel format not supported.");
        bool hasA = (pfFlags & 0x1) != 0 && am != 0;
        int pbytes = bits / 8;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = pos + (y * w + x) * pbytes;
                uint v = bits == 32 ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o))
                    : bits == 24 ? (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16)
                    : BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o));
                img.Set(x, y, M(v, rm), M(v, gm), M(v, bm), hasA ? M(v, am) : (byte)255);
            }
        return img;
    }

    static byte M(uint v, uint mask)
    {
        if (mask == 0) return 0;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint max = mask >> shift;
        return (byte)(((v & mask) >> shift) * 255 / max);
    }

    static void ColorBlock(byte[] d, int o, byte[] outRgba, bool dxt1)
    {
        int c0 = d[o] | d[o + 1] << 8, c1 = d[o + 2] | d[o + 3] << 8;
        var pal = new byte[4][];
        pal[0] = C565(c0); pal[1] = C565(c1);
        if (!dxt1 || c0 > c1)
        {
            pal[2] = Mix(pal[0], pal[1], 2, 1, 3); pal[3] = Mix(pal[0], pal[1], 1, 2, 3);
        }
        else
        {
            pal[2] = Mix(pal[0], pal[1], 1, 1, 2); pal[3] = new byte[] { 0, 0, 0, 0 };
        }
        uint bitsv = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o + 4));
        for (int i = 0; i < 16; i++)
        {
            var c = pal[(bitsv >> (i * 2)) & 3];
            outRgba[i * 4] = c[0]; outRgba[i * 4 + 1] = c[1]; outRgba[i * 4 + 2] = c[2]; outRgba[i * 4 + 3] = c[3];
        }
    }

    static byte[] C565(int c) => new[] { (byte)(((c >> 11) & 31) * 255 / 31), (byte)(((c >> 5) & 63) * 255 / 63), (byte)((c & 31) * 255 / 31), (byte)255 };
    static byte[] Mix(byte[] a, byte[] b, int wa, int wb, int div) =>
        new[] { (byte)((a[0] * wa + b[0] * wb) / div), (byte)((a[1] * wa + b[1] * wb) / div), (byte)((a[2] * wa + b[2] * wb) / div), (byte)255 };

    static void Dxt3Alpha(byte[] d, int o, byte[] outRgba)
    {
        for (int i = 0; i < 16; i++)
        {
            int v = (d[o + i / 2] >> ((i & 1) * 4)) & 15;
            outRgba[i * 4 + 3] = (byte)(v * 17);
        }
    }

    static void Dxt5Alpha(byte[] d, int o, byte[] outRgba)
    {
        int a0 = d[o], a1 = d[o + 1];
        var a = new int[8];
        a[0] = a0; a[1] = a1;
        if (a0 > a1) for (int i = 1; i < 7; i++) a[i + 1] = ((7 - i) * a0 + i * a1) / 7;
        else { for (int i = 1; i < 5; i++) a[i + 1] = ((5 - i) * a0 + i * a1) / 5; a[6] = 0; a[7] = 255; }
        ulong bitsv = 0;
        for (int i = 0; i < 6; i++) bitsv |= (ulong)d[o + 2 + i] << (8 * i);
        for (int i = 0; i < 16; i++) outRgba[i * 4 + 3] = (byte)a[(int)((bitsv >> (3 * i)) & 7)];
    }
}
