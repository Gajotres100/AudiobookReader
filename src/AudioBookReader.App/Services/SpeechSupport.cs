using System.Runtime.InteropServices;

namespace AudioBookReader.App.Services;

/// <summary>
/// Whether this device can run speech recognition at all, which every kind of alignment needs.
///
/// Whisper ships only 64-bit native libraries. Phones have been 64-bit for years, but televisions
/// often still run a 32-bit system on 64-bit chips — a Sony BRAVIA on Android 12 does — and there the
/// library cannot load. Asked before offering alignment, so such a device says so plainly instead of
/// a switch that turns on and then silently never finds the text.
/// </summary>
public static class SpeechSupport
{
    public static bool IsAvailable { get; } =
        RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.X64;
}
