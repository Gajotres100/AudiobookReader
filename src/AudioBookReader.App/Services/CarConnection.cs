namespace AudioBookReader.App.Services;

/// <summary>
/// Whether the phone is currently driving a car screen.
///
/// The app is three things at once — a player, a reader, and the two synchronised — and only one of
/// them belongs in a car. Someone who opens a paired book while driving wants the narration, not a
/// page of text they must not look at, and certainly not the recognition work that following that
/// text would set running on the way.
/// </summary>
public partial class CarConnection
{
    /// <summary>True while a car screen is attached, by projection or natively.</summary>
    public partial bool IsConnected { get; }
}
