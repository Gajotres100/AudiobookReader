using Android.Graphics;
using ZXing;
using ZXing.Common;

namespace AudioBookReader.App.Services;

public static partial class QrReader
{
    public static partial string? Read(byte[] image)
    {
        // A camera photo is twelve megapixels; a QR code filling part of it reads just as well at a
        // fraction of that, and decoding the full size takes seconds and a large slice of memory.
        var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeByteArray(image, 0, image.Length, bounds);

        var sample = 1;
        while (Math.Max(bounds.OutWidth, bounds.OutHeight) / sample > 1600) sample *= 2;

        using var bitmap = BitmapFactory.DecodeByteArray(image, 0, image.Length, new BitmapFactory.Options { InSampleSize = sample });
        if (bitmap is null) return null;

        var width = bitmap.Width;
        var height = bitmap.Height;

        var pixels = new int[width * height];
        bitmap.GetPixels(pixels, 0, width, 0, 0, width, height);

        // ARGB ints are B, G, R, A bytes in memory on every Android device, which is little-endian.
        var bytes = new byte[pixels.Length * 4];
        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);

        var reader = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE], TryHarder = true },
        };

        return reader.Decode(new RGBLuminanceSource(bytes, width, height, RGBLuminanceSource.BitmapFormat.BGRA32))?.Text;
    }
}
