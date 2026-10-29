namespace Pmx2Vrm.Images;

/// <summary>8-bit RGBA pixels, top row first.</summary>
public sealed class RgbaImage
{
    public readonly int Width, Height;
    public readonly byte[] Pixels;

    public RgbaImage(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 1 << 28)
            throw new InvalidDataException($"Bad image size {width}x{height}.");
        Width = width; Height = height;
        Pixels = new byte[width * height * 4];
    }

    public void Set(int x, int y, byte r, byte g, byte b, byte a)
    {
        int o = (y * Width + x) * 4;
        Pixels[o] = r; Pixels[o + 1] = g; Pixels[o + 2] = b; Pixels[o + 3] = a;
    }

    public void FlipVertical()
    {
        int stride = Width * 4;
        var tmp = new byte[stride];
        for (int y = 0; y < Height / 2; y++)
        {
            Buffer.BlockCopy(Pixels, y * stride, tmp, 0, stride);
            Buffer.BlockCopy(Pixels, (Height - 1 - y) * stride, Pixels, y * stride, stride);
            Buffer.BlockCopy(tmp, 0, Pixels, (Height - 1 - y) * stride, stride);
        }
    }

    public void FlipHorizontal()
    {
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width / 2; x++)
                for (int c = 0; c < 4; c++)
                {
                    int a = (y * Width + x) * 4 + c, b = (y * Width + Width - 1 - x) * 4 + c;
                    (Pixels[a], Pixels[b]) = (Pixels[b], Pixels[a]);
                }
    }

    public bool HasAlpha()
    {
        for (int i = 3; i < Pixels.Length; i += 4) if (Pixels[i] != 255) return true;
        return false;
    }
}

public enum AlphaUsage { Opaque, Cutout, Blend }

public static class ImageAnalysis
{
    /// <summary>
    /// Opaque if every pixel is opaque; Cutout if the non-opaque pixels are almost all fully clear
    /// (hard-edged masks); Blend otherwise.
    /// </summary>
    public static AlphaUsage Alpha(RgbaImage img)
    {
        long clear = 0, partial = 0;
        var p = img.Pixels;
        for (int i = 3; i < p.Length; i += 4)
        {
            byte a = p[i];
            if (a >= 250) continue;
            if (a <= 8) clear++; else partial++;
        }
        long total = p.Length / 4;
        if (clear + partial == 0 || (clear + partial) * 2000 < total) return AlphaUsage.Opaque;
        return partial <= (clear + partial) / 10 ? AlphaUsage.Cutout : AlphaUsage.Blend;
    }

    /// <summary>The average color of the bottom row (the dark end of an MMD toon ramp), 0-1.</summary>
    public static (float R, float G, float B) BottomRowColor(RgbaImage img)
    {
        double r = 0, g = 0, b = 0;
        int y = img.Height - 1;
        for (int x = 0; x < img.Width; x++)
        {
            int o = (y * img.Width + x) * 4;
            r += img.Pixels[o]; g += img.Pixels[o + 1]; b += img.Pixels[o + 2];
        }
        double n = img.Width * 255.0;
        return ((float)(r / n), (float)(g / n), (float)(b / n));
    }
}
