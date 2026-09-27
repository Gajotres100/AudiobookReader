using Foundation;
using UIKit;
using Vision;

namespace AudioBookReader.App.Services;

public static partial class QrReader
{
    public static partial string? Read(byte[] image)
    {
        using var data = NSData.FromArray(image);
        using var picture = UIImage.LoadFromData(data);
        if (picture?.CGImage is not { } cgImage) return null;

        var request = new VNDetectBarcodesRequest(null);
        using var handler = new VNImageRequestHandler(cgImage, new VNImageOptions());

        if (!handler.Perform([request], out _)) return null;

        return request.GetResults<VNBarcodeObservation>()?
            .Select(found => found.PayloadStringValue)
            .FirstOrDefault(text => !string.IsNullOrEmpty(text));
    }
}
