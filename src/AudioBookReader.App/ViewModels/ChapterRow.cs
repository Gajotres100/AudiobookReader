using AudioBookReader.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioBookReader.App.ViewModels;

/// <summary>
/// A chapter as the list shows it.
///
/// Observable rather than a plain record because the row has to light up as the narration reaches
/// it — a chapter list that cannot show where you are is only half a chapter list.
/// </summary>
public partial class ChapterRow(Chapter chapter, string title, string startText) : ObservableObject
{
    public Chapter Chapter { get; } = chapter;

    public string Title { get; } = title;

    /// <summary>Where the chapter begins in the book, as a timestamp.</summary>
    public string StartText { get; } = startText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Marker))]
    [NotifyPropertyChangedFor(nameof(TitleWeight))]
    public partial bool IsCurrent { get; set; }

    public string Marker => IsCurrent ? "▶︎" : "";

    public FontAttributes TitleWeight => IsCurrent ? FontAttributes.Bold : FontAttributes.None;
}
