using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RemoteSupport.Shared.ScreenStreaming.Encoding;

public class JpegFrameEncoder : IFrameEncoder
{
    private readonly int _defaultQuality;

    public JpegFrameEncoder(int defaultQuality = 75)
    {
        _defaultQuality = defaultQuality;
    }

    public byte[] EncodeFrame(byte[] rawPixels, int width, int height, int quality = -1)
    {
        quality = quality < 0 ? _defaultQuality : quality;

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var bitmapData = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            var bytesPerPixel = 4;
            var sourceStride = width * bytesPerPixel;

            for (int y = 0; y < height; y++)
            {
                var sourceOffset = y * sourceStride;
                var destOffset = y * bitmapData.Stride;

                Marshal.Copy(rawPixels, sourceOffset,
                    bitmapData.Scan0 + destOffset, sourceStride);
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        using var ms = new MemoryStream();
        var encoder = GetJpegEncoder();
        var encoderParams = CreateEncoderParameters(quality);
        bitmap.Save(ms, encoder, encoderParams);

        return ms.ToArray();
    }

    public byte[] DecodeFrame(byte[] encodedData, out int width, out int height)
    {
        using var ms = new MemoryStream(encodedData);
        using var bitmap = new Bitmap(ms);

        width = bitmap.Width;
        height = bitmap.Height;

        var bitmapData = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            var pixels = new byte[width * height * 4];
            var bytesPerPixel = 4;
            var sourceStride = width * bytesPerPixel;

            for (int y = 0; y < height; y++)
            {
                var sourceOffset = y * bitmapData.Stride;
                var destOffset = y * sourceStride;

                Marshal.Copy(bitmapData.Scan0 + sourceOffset,
                    pixels, destOffset, sourceStride);
            }

            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
    }

    private static ImageCodecInfo GetJpegEncoder()
    {
        return ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    }

    private static EncoderParameters CreateEncoderParameters(int quality)
    {
        var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        return encoderParams;
    }
}
