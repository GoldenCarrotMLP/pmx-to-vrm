using Pmx2Vrm.Images;

namespace Pmx2Vrm.Tests;

public static class ImageTests
{
    public static void TestPngRoundTrip()
    {
        var img = new RgbaImage(5, 3);
        for (int y = 0; y < 3; y++) for (int x = 0; x < 5; x++) img.Set(x, y, (byte)(x * 50), (byte)(y * 80), 7, (byte)(x == 0 ? 0 : 255));
        var back = Png.Decode(Png.Encode(img));
        Assert.True(back.Width == 5 && back.Height == 3 && back.Pixels.SequenceEqual(img.Pixels), "png round trip");
        Assert.Equal(AlphaUsage.Cutout, ImageAnalysis.Alpha(back), "hard alpha = cutout");
    }

    public static void TestBmp24()
    {
        var img = Bmp.Decode(SyntheticModel.Bmp24());
        Assert.True(img.Pixels[0] == 255 && img.Pixels[1] == 0 && img.Pixels[2] == 0 && img.Pixels[3] == 255, "top-left red");
        int o = (1 * 4 + 0) * 4;
        Assert.True(img.Pixels[o] == 40 && img.Pixels[o + 1] == 50 && img.Pixels[o + 2] == 60, "bottom row BGR order");
    }

    public static void TestTgaRleTopDown()
    {
        var d = new List<byte> { 0, 0, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 2, 0, 32, 0x28 }; // 2x2, 32-bit, RLE, top-left origin, 8 alpha bits
        d.AddRange(new byte[] { 0x81, 0, 0, 255, 128 }); // run of 2: red, alpha 128
        d.AddRange(new byte[] { 0x01, 255, 0, 0, 255, 0, 255, 0, 255 }); // raw 2: blue, green
        var img = Tga.Decode(d.ToArray());
        Assert.True(img.Pixels[0] == 255 && img.Pixels[3] == 128, "first pixel red, alpha 128");
        Assert.True(img.Pixels[8] == 0 && img.Pixels[10] == 255, "third pixel blue");
        Assert.Equal(AlphaUsage.Blend, ImageAnalysis.Alpha(img), "partial alpha = blend");
    }

    public static void TestDdsDxt1()
    {
        var d = new byte[128 + 8];
        "DDS "u8.ToArray().CopyTo(d, 0);
        BitConverter.GetBytes(124).CopyTo(d, 4);
        BitConverter.GetBytes(4).CopyTo(d, 12); BitConverter.GetBytes(4).CopyTo(d, 16);
        BitConverter.GetBytes(32).CopyTo(d, 76); BitConverter.GetBytes(4).CopyTo(d, 80);
        "DXT1"u8.ToArray().CopyTo(d, 84);
        // color0 = pure red (0xF800), color1 = black; all indices 0.
        d[128] = 0x00; d[129] = 0xF8; d[130] = 0; d[131] = 0;
        var img = Dds.Decode(d);
        Assert.True(img.Width == 4 && img.Pixels[0] == 255 && img.Pixels[1] == 0 && img.Pixels[3] == 255, "dxt1 red");
    }
}
