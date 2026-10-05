namespace RemoteSupport.Shared.ScreenStreaming.Video;

internal static class YuvConvert
{
    public static byte[] BgraToNv12(byte[] bgra, int width, int height)
    {
        var ySize = width * height;
        var nv12 = new byte[ySize + ySize / 2];
        var uvOff = ySize;

        for (var y = 0; y < height; y++)
        {
            var rowEven = (y & 1) == 0;
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var b = bgra[i];
                var g = bgra[i + 1];
                var r = bgra[i + 2];

                var Y = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                nv12[y * width + x] = (byte)Math.Clamp(Y, 0, 255);

                if (rowEven && (x & 1) == 0)
                {
                    var U = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                    var V = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
                    var uvIndex = uvOff + (y / 2) * width + x;
                    nv12[uvIndex] = (byte)Math.Clamp(U, 0, 255);
                    nv12[uvIndex + 1] = (byte)Math.Clamp(V, 0, 255);
                }
            }
        }

        return nv12;
    }

    public static byte[] Nv12ToBgra(byte[] nv12, int width, int height)
    {
        var bgra = new byte[width * height * 4];
        var uvOff = width * height;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var Y = nv12[y * width + x] - 16;
                var uvIndex = uvOff + (y / 2) * width + (x & ~1);
                var U = nv12[uvIndex] - 128;
                var V = nv12[uvIndex + 1] - 128;

                var r = (298 * Y + 409 * V + 128) >> 8;
                var g = (298 * Y - 100 * U - 208 * V + 128) >> 8;
                var b = (298 * Y + 516 * U + 128) >> 8;

                var o = (y * width + x) * 4;
                bgra[o] = (byte)Math.Clamp(b, 0, 255);
                bgra[o + 1] = (byte)Math.Clamp(g, 0, 255);
                bgra[o + 2] = (byte)Math.Clamp(r, 0, 255);
                bgra[o + 3] = 255;
            }
        }

        return bgra;
    }
}
