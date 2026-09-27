namespace AudioBookReader.App.Services;

/// <summary>
/// Reads a QR code out of a photograph.
///
/// A photo rather than a live camera view: taking one is something the app already does for
/// finding a page, it needs no extra camera library, and a QR code on another screen a hand's
/// breadth away is read from a single photo every time. Each platform decodes with what it has —
/// ZXing on Android, Apple's own Vision on iOS.
/// </summary>
public static partial class QrReader
{
    /// <summary>The text in the first QR code found in the image, or null when there is none.</summary>
    public static partial string? Read(byte[] image);
}
