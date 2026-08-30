using System.Text;
using AudioBookReader.App.Services;
using AudioBookReader.Core.Books;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioBookReader.App.ViewModels;

[QueryProperty(nameof(BookId), "id")]
public partial class ReaderViewModel(
    LibraryDatabase database,
    SyncMapStore syncMaps,
    BookTextExtractors extractors,
    PlaybackController playback,
    AlignmentQueue alignment,
    AlignmentSettingsStore settings) : ObservableObject, IDisposable
{
    private Book? _book;
    private BookText? _text;
    private BookSync? _sync;
    private IDispatcherTimer? _ticker;

    [ObservableProperty]
    public partial int BookId { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFollowHint))]
    public partial bool IsBusy { get; set; }

    /// <summary>HTML for the document currently on screen.</summary>
    [ObservableProperty]
    public partial string Html { get; set; } = "";

    /// <summary>True when a sync map covers this book, so the text can follow the narration.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FollowHint))]
    [NotifyPropertyChangedFor(nameof(ShowFollowHint))]
    public partial bool CanFollow { get; set; }

    /// <summary>
    /// Says why the text is not following, rather than leaving a disabled control to explain
    /// itself. Silently doing nothing is the worst of the options here — it looks broken.
    /// </summary>
    public string FollowHint => _book?.IsPaired == true
        ? "Tekst će pratiti naraciju kad poravnanje završi. Pokreni ga na stranici knjige."
        : "Dodaj i audioknjigu i e-knjigu za isti naslov pa će tekst moći pratiti naraciju.";

    public bool ShowFollowHint => !CanFollow && !IsBusy;

    /// <summary>
    /// Why the highlight is standing still even though this book has a sync map.
    ///
    /// A map covers the chapters a run has reached, not the book — so listening past that point
    /// leaves the text motionless with nothing to explain it, which reads as the feature being
    /// broken rather than as work still to do.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFollowStatus))]
    public partial string FollowStatus { get; set; } = "";

    public bool HasFollowStatus => FollowStatus.Length > 0;

    /// <summary>Whether to keep the highlight moving with playback. Off means plain reading.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FollowLabel))]
    public partial bool IsFollowing { get; set; }

    public string FollowLabel => IsFollowing ? "Prati ✓" : "Prati";

    /// <summary>
    /// Playback control lives in the reader too.
    ///
    /// The whole point of a paired book is listening and reading at once, so having to leave the
    /// text to press play was backwards — and the follow toggle was being mistaken for it, since
    /// it was the only audio-shaped control here.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayLabel))]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial bool HasAudio { get; set; }

    public string PlayLabel => IsPlaying ? "⏸" : "▶";

    [RelayCommand]
    private async Task TogglePlayAsync()
    {
        if (_book?.AudioPath is not { } audioPath) return;

        // The reader can be opened without the book page having loaded anything.
        if (playback.BookId != BookId)
        {
            var state = await database.GetReadingStateAsync(BookId);
            await playback.LoadAsync(BookId, audioPath, state?.AudioPositionMs ?? 0, state?.Speed ?? 1f);
        }

        playback.TogglePlayPause();
        IsPlaying = playback.IsPlaying;
    }

    // ---- Appearance ----

    /// <summary>A page colouring, named the way a reader thinks of it rather than by hex.</summary>
    public sealed record ReaderTheme(string Name, string Background, string Foreground, string Highlight);

    /// <summary>
    /// Paper first, because a book is not a white screen.
    ///
    /// Each is a matched pair rather than a background with black text on it: the contrast that
    /// suits paper is not the contrast that suits a dark room, and a highlight bright enough to see
    /// on cream is glaring on black.
    /// </summary>
    public static readonly ReaderTheme[] Themes =
    [
        new("Papir", "#F5EFE1", "#2B2620", "rgba(196, 148, 0, 0.28)"),
        new("Sepija", "#EBDCC0", "#4A3A26", "rgba(168, 112, 0, 0.30)"),
        new("Svijetlo", "#FBFBF9", "#1B1B1F", "rgba(255, 196, 0, 0.34)"),
        new("Prigušeno", "#2A2724", "#D6CFC2", "rgba(255, 196, 0, 0.20)"),
        new("Noć", "#000000", "#A9A39A", "rgba(255, 196, 0, 0.16)"),
    ];

    public sealed record ReaderFont(string Name, string Css);

    public static readonly ReaderFont[] Fonts =
    [
        new("Serif", "Georgia, 'Noto Serif', 'Times New Roman', serif"),
        new("Bez serifa", "'Noto Sans', Roboto, system-ui, sans-serif"),
        new("Široki", "'Noto Serif', Georgia, serif"),
    ];

    /// <summary>Chosen look, kept per reader rather than per book — it belongs to the eyes.</summary>
    public int ThemeIndex
    {
        get => Math.Clamp(Preferences.Default.Get("reader.theme", 0), 0, Themes.Length - 1);
        private set => Preferences.Default.Set("reader.theme", value);
    }

    public int FontIndex
    {
        get => Math.Clamp(Preferences.Default.Get("reader.font", 0), 0, Fonts.Length - 1);
        private set => Preferences.Default.Set("reader.font", value);
    }

    /// <summary>Extra letter spacing for the widest of the faces, which is what makes it wide.</summary>
    private string LetterSpacing => FontIndex == 2 ? "0.02em" : "normal";

    public ReaderTheme Theme => Themes[ThemeIndex];

    public ReaderFont Font => Fonts[FontIndex];

    public string ThemeName => Theme.Name;

    public string FontName => Font.Name;

    /// <summary>Raised when the page should restyle itself without being rebuilt and losing its place.</summary>
    public event EventHandler? AppearanceChanged;

    [RelayCommand]
    private Task ChooseThemeAsync() => PickAsync(
        "Izgled stranice",
        [.. Themes.Select(t => t.Name)],
        index =>
        {
            ThemeIndex = index;
            OnPropertyChanged(nameof(ThemeName));
        });

    [RelayCommand]
    private Task ChooseFontAsync() => PickAsync(
        "Font",
        [.. Fonts.Select(f => f.Name)],
        index =>
        {
            FontIndex = index;
            OnPropertyChanged(nameof(FontName));
        });

    private async Task PickAsync(string title, string[] options, Action<int> chosen)
    {
        var choice = await Shell.Current.DisplayActionSheetAsync(title, "Odustani", null, options);

        var index = Array.IndexOf(options, choice);
        if (index < 0) return;

        chosen(index);
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The one call that restyles a loaded page in place.</summary>
    public string AppearanceScript =>
        $"applyAppearance('{Theme.Background}', '{Theme.Foreground}', \"{Font.Css}\", " +
        $"'{Theme.Highlight}', '{LetterSpacing}')";

    // ---- Chrome ----

    /// <summary>
    /// Whether the controls are on screen.
    ///
    /// Hidden while the narration runs, because at that point the page is being read rather than
    /// operated, and a row of buttons under a paragraph is just something else to look at. A tap
    /// brings them back — and pauses, since wanting the controls and wanting the voice to keep
    /// going are rarely the same wish.
    /// </summary>
    [ObservableProperty]
    public partial bool ChromeVisible { get; set; } = true;

    /// <summary>Whether the tap that revealed the controls was the thing that paused playback.</summary>
    private bool _pausedByTap;

    public void OnTapped()
    {
        if (ChromeVisible)
        {
            ChromeVisible = false;

            // Only resume what this paused; a book the user stopped themselves stays stopped.
            if (_pausedByTap && !playback.IsPlaying) playback.Play();
            _pausedByTap = false;

            return;
        }

        ChromeVisible = true;

        if (playback.IsPlaying)
        {
            playback.Pause();
            _pausedByTap = true;
        }

        IsPlaying = playback.IsPlaying;
    }

    /// <summary>Raised when the page should move the highlight. Carries the sentence index.</summary>
    public event EventHandler<int>? HighlightRequested;

    private int _lastSentence = -1;

    /// <summary>
    /// Chapters as the ebook itself defines them, with offsets into its text.
    ///
    /// Not the library's chapters: for a paired book those come from the audiobook's marks and
    /// carry no text ranges until alignment has run — so they cannot be used to navigate a book
    /// that has not been aligned yet, which is exactly when the reader needs to get around.
    /// </summary>
    private List<Chapter> _readerChapters = [];

    /// <summary>Which document of the book is on screen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DocumentLabel))]
    [NotifyPropertyChangedFor(nameof(CanGoPrevious))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    public partial int SpineIndex { get; set; } = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChapters))]
    public partial int ChapterCount { get; set; }

    public bool HasChapters => ChapterCount > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DocumentLabel))]
    public partial int PageNumber { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DocumentLabel))]
    public partial int PageCount { get; set; }

    public string DocumentLabel
    {
        get
        {
            if (_text is null) return "";

            var document = $"{SpineIndex + 1} / {_text.Spine.Count}";
            return PageCount > 1 ? $"{document}   ·   str. {PageNumber} / {PageCount}" : document;
        }
    }

    /// <summary>
    /// Which page a newly shown document opens on: its first, or its last when the reader arrived
    /// by swiping backwards out of the document after it.
    /// </summary>
    public int EntryPage { get; private set; }

    public void OnPagesReported(int page, int count)
    {
        PageNumber = page;
        PageCount = count;
    }

    /// <summary>Slider for moving through the pages of the chapter, hidden until asked for.</summary>
    [ObservableProperty]
    public partial bool ShowPageSlider { get; set; }

    [RelayCommand]
    private void TogglePageSlider() => ShowPageSlider = !ShowPageSlider;

    /// <summary>Raised when the page should be turned to a given index by the host.</summary>
    public event EventHandler<int>? PageJumpRequested;

    public void JumpToPage(int oneBasedPage) => PageJumpRequested?.Invoke(this, oneBasedPage - 1);

    /// <summary>
    /// Reading size, in pixels, remembered across books and sessions.
    ///
    /// Kept out of the database deliberately: it belongs to the reader's eyes, not to any one
    /// book, and it is read on every page build.
    /// </summary>
    public int FontSize
    {
        get => Preferences.Default.Get("reader.fontSize", 19);
        private set => Preferences.Default.Set("reader.fontSize", Math.Clamp(value, 13, 34));
    }

    public void OnFontSizeChanged(int size) => FontSize = size;

    /// <summary>
    /// Manual correction, in milliseconds, added to the playback position before looking the text
    /// up.
    ///
    /// Anchors sit minutes apart and everything between them is interpolated, so the highlight can
    /// sit a second or two off the voice — about one sentence, and consistently in one direction
    /// for a given book and narrator. Rather than make the reader live with it or spend an hour of
    /// CPU narrowing it, one nudge corrects the whole book. Kept per book, since the offset comes
    /// from that recording.
    /// </summary>
    /// <summary>
    /// How far ahead of the voice the marker aims, in milliseconds.
    ///
    /// The remaining error is roughly symmetric — the marker is as likely to move early as late —
    /// but the two do not look alike. Moving early is invisible: the eye is already scanning the
    /// sentence when the narrator reaches it, which is what reading along feels like anyway. Moving
    /// late is glaring, because the voice is audibly into a sentence the marker has not reached.
    /// Aiming a little ahead therefore trades a fault nobody notices for one everybody does.
    /// </summary>
    private const int AnticipationMs = 400;

    public int SyncOffsetMs
    {
        get => Preferences.Default.Get($"reader.offset.{BookId}", 0);
        private set => Preferences.Default.Set($"reader.offset.{BookId}", Math.Clamp(value, -20_000, 20_000));
    }

    public string SyncOffsetText =>
        SyncOffsetMs == 0 ? "0 s" : $"{(SyncOffsetMs > 0 ? "+" : "")}{SyncOffsetMs / 1000.0:0.0} s";

    [RelayCommand]
    private void NudgeSyncEarlier() => AdjustOffset(-500);

    [RelayCommand]
    private void NudgeSyncLater() => AdjustOffset(500);

    private void AdjustOffset(int deltaMs)
    {
        SyncOffsetMs += deltaMs;
        OnPropertyChanged(nameof(SyncOffsetText));

        // Take effect on the next tick rather than waiting for the sentence to change on its own.
        _lastSentence = -1;
    }

    public bool CanGoPrevious => SpineIndex > 0;

    public bool CanGoNext => _text is not null && SpineIndex < _text.Spine.Count - 1;

    public async Task LoadAsync()
    {
        IsBusy = true;

        try
        {
            _book = await database.GetBookAsync(BookId);
            if (_book?.EbookPath is null) return;

            Title = _book.Title;
            HasAudio = _book.HasAudio;

            var extracted = await extractors.ExtractAsync(_book.EbookPath);
            _text = extracted.Text;

            _readerChapters = [.. extracted.Chapters];
            ChapterCount = _readerChapters.Count;

            var chapters = await database.GetChaptersAsync(BookId);
            var map = await syncMaps.LoadAsync(BookId);

            // Following requires both media and a map built from this exact pair of files.
            CanFollow = _book.IsPaired
                && map is not null
                && map.MatchesPair(_book.AudioHash, _book.EbookHash);

            AppLog.Info(
                $"reader: paired={_book.IsPaired}, map={(map is null ? "none" : $"{map.Chapters.Count} chapters")}, " +
                $"hashes book=({_book.AudioHash},{_book.EbookHash}) map=({map?.AudioHash},{map?.EbookHash}), " +
                $"canFollow={CanFollow}, text={_text.PlainText.Length} chars, " +
                $"libraryChapters={chapters.Count}");

            if (CanFollow)
            {
                _sync = new BookSync(_text, map!, chapters);
                IsFollowing = true;
            }

            // Following measures the passage being read while it is read, so it belongs to the
            // reader's lifetime: started here, stopped when the page goes away.
            if (_book.IsPaired && settings.LiveRefinement) alignment.StartFollowing(BookId);

            await ShowStartingDocumentAsync();
            StartTicking();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Opens wherever the reader should resume: the narrator's position when following, otherwise
    /// wherever they last stopped reading.
    /// </summary>
    private async Task ShowStartingDocumentAsync()
    {
        var state = await database.GetReadingStateAsync(BookId);

        var offset = CanFollow && playback.PositionMs > 0
            ? _sync!.CharOffsetAt(playback.PositionMs) ?? state?.TextOffset ?? 0
            : state?.TextOffset ?? 0;

        ShowDocumentAt(offset);

        if (_text!.SentenceAt(offset) is { } sentence)
        {
            _lastSentence = sentence.Index;
            HighlightRequested?.Invoke(this, sentence.Index);
        }
    }

    private void ShowDocumentAt(int charOffset)
    {
        var document = _text!.SpineAt(charOffset) ?? _text.Spine.FirstOrDefault();
        if (document is not null) ShowDocument(document.Index);
    }

    private void ShowDocument(int index)
    {
        if (_text is null || _text.Spine.Count == 0) return;

        index = Math.Clamp(index, 0, _text.Spine.Count - 1);
        if (index == SpineIndex) return;

        SpineIndex = index;
        Html = BuildPage(_text.Spine[index].Html);
    }

    /// <summary>
    /// Moves through the book by document.
    ///
    /// A reader with no way to turn a page is not a reader. Following the narration only works
    /// once alignment has run, and even then the user will want to look ahead or back — so getting
    /// around cannot depend on the sync map existing.
    /// </summary>
    [RelayCommand]
    private void NextDocument()
    {
        EntryPage = 0;
        ShowDocument(SpineIndex + 1);
    }

    [RelayCommand]
    private void PreviousDocument()
    {
        // Coming backwards lands on the last page, so the text continues where the eye left it
        // rather than jumping to the top of the previous chapter.
        EntryPage = -1;
        ShowDocument(SpineIndex - 1);
    }

    /// <summary>Jumps straight to a chapter, since paging through forty documents is no way to travel.</summary>
    [RelayCommand]
    private async Task JumpToChapterAsync()
    {
        EntryPage = 0;

        if (_readerChapters.Count == 0) return;

        // Numbered so that chapters sharing a title stay distinguishable in the list.
        var labels = _readerChapters.Select((c, i) => $"{i + 1}. {c.Title}").ToArray();

        var choice = await Shell.Current.DisplayActionSheetAsync("Poglavlje", "Odustani", null, labels);
        var index = Array.IndexOf(labels, choice);
        if (index < 0) return;

        var chapter = _readerChapters[index];
        if (chapter.TextStart is not { } start) return;

        ShowDocumentAt(start);

        if (_text?.SentenceAt(start) is { } sentence)
        {
            _lastSentence = sentence.Index;
            HighlightRequested?.Invoke(this, sentence.Index);
        }
    }

    private DateTime _lastMapCheck = DateTime.MinValue;
    private bool _checkingMap;

    /// <summary>
    /// Picks up alignment that landed after the reader was already open.
    ///
    /// The map was read once, at page load, and alignment writes it a chapter at a time — so a run
    /// finishing while the reader is on screen, or simply reaching the chapter being listened to,
    /// changed nothing until the page was left and re-entered. Nobody would guess that; it just
    /// looks like the text refuses to move.
    /// </summary>
    private async Task RefreshSyncMapAsync()
    {
        if (_checkingMap || _text is null || _book is null || !_book.IsPaired) return;

        _checkingMap = true;

        try
        {
            var map = await syncMaps.LoadAsync(BookId);
            if (map is null || !map.MatchesPair(_book.AudioHash, _book.EbookHash)) return;

            var chapters = await database.GetChaptersAsync(BookId);
            var appeared = _sync is null;

            _sync = new BookSync(_text, map, chapters);

            if (appeared)
            {
                AppLog.Info($"reader: picked up a sync map covering {map.Chapters.Count} chapters");

                CanFollow = true;
                IsFollowing = true;
                FollowStatus = "";
            }

            // The map changed underneath, so whatever sentence was last shown was chosen from an
            // older one and must not suppress the next move.
            _lastSentence = -1;
        }
        catch (Exception ex)
        {
            // A run writing the file while it is being read is ordinary; the next check gets it.
            AppLog.Error("reading the sync map", ex);
        }
        finally
        {
            _checkingMap = false;
        }
    }

    /// <summary>
    /// Looks for a newer map, but only every so often — this runs from a tick twice a second and
    /// the file is only rewritten once a chapter.
    /// </summary>
    private void MaybeRefreshSyncMap()
    {
        if (DateTime.UtcNow - _lastMapCheck < TimeSpan.FromSeconds(10)) return;

        _lastMapCheck = DateTime.UtcNow;
        _ = RefreshSyncMapAsync();
    }

    private void StartTicking()
    {
        _ticker = Application.Current?.Dispatcher.CreateTimer();
        if (_ticker is null) return;

        // Twice a second left the marker up to half a second late on its own, before the map's own
        // error was counted. The work per tick is two binary searches, and the web view is only
        // touched when the sentence actually changes, so a faster tick costs almost nothing.
        _ticker.Interval = TimeSpan.FromMilliseconds(200);
        _ticker.Tick += (_, _) => Follow();
        _ticker.Start();
    }

    /// <summary>
    /// Moves the highlight to wherever the narrator is.
    ///
    /// Twice a second, which is roughly the rate sentences go by. Faster would send the WebView
    /// work it would only discard; slower and the highlight visibly lags the voice.
    /// </summary>
    private void Follow()
    {
        // Starting the narration is the moment the page stops being operated and starts being read,
        // so the controls get out of the way on their own.
        var playing = playback.IsPlaying;
        if (playing && !IsPlaying) ChromeVisible = false;

        IsPlaying = playing;

        // No map yet is exactly the state a finishing alignment gets us out of, so keep looking —
        // and while following, the map grows under us continuously, so keep looking regardless.
        if (_sync is null || settings.LiveRefinement) MaybeRefreshSyncMap();

        if (!IsFollowing || _sync is null || !playback.IsPlaying)
        {
            if (DateTime.UtcNow - _lastMiss > TimeSpan.FromSeconds(10))
            {
                _lastMiss = DateTime.UtcNow;
                AppLog.Info(
                    $"follow idle: following={IsFollowing}, sync={_sync is not null}, playing={playback.IsPlaying}, " +
                    $"playbackBook={playback.BookId}, thisBook={BookId}");
            }

            return;
        }

        var at = playback.PositionMs + AnticipationMs + SyncOffsetMs;

        if (_sync.SentenceAt(at) is not { } sentence)
        {
            // The chapter being listened to may be the one a run is working on right now.
            MaybeRefreshSyncMap();

            var chapter = _sync.ChapterAt(at);

            FollowStatus = settings.LiveRefinement
                ? "Mjerim ovaj dio — tekst kreće za koji trenutak."
                : chapter is null
                    ? "Ovaj dio knjige još nije poravnan."
                    : $"Poglavlje {chapter.Index + 1} još nije poravnano — pokreni poravnanje na stranici knjige.";

            // Once a second at most, so it does not drown the log.
            if (DateTime.UtcNow - _lastMiss > TimeSpan.FromSeconds(5))
            {
                _lastMiss = DateTime.UtcNow;

                AppLog.Info(
                    $"follow: no sentence at {at} ms — chapter {chapter?.Index}, " +
                    $"aligned={(chapter is null ? "?" : _sync.IsAligned(chapter.Index).ToString())}");
            }

            return;
        }

        FollowStatus = "";

        if (sentence.Index == _lastSentence) return;

        if (DateTime.UtcNow - _lastTrace > TimeSpan.FromSeconds(10))
        {
            _lastTrace = DateTime.UtcNow;
            AppLog.Info(
                $"follow: {at} ms -> char {_sync.CharOffsetAt(at)} -> sentence {sentence.Index} " +
                $"(doc {sentence.SpineIndex}, showing {SpineIndex})");
        }

        var previousDocument = SpineIndex;
        _lastSentence = sentence.Index;

        // Crossing into another document means loading a different page before highlighting in it.
        ShowDocumentAt(sentence.Start);

        // A document change reloads the web view, and the script that does the highlighting will
        // not exist until it finishes. The page re-applies this once loaded; asking now would be
        // swallowed and never retried, because the sentence index has already been consumed.
        if (SpineIndex == previousDocument) HighlightRequested?.Invoke(this, sentence.Index);
    }

    private DateTime _lastMiss = DateTime.MinValue;
    private DateTime _lastTrace = DateTime.MinValue;

    /// <summary>The sentence the page should highlight once a freshly loaded document is ready.</summary>
    public int PendingHighlight => _lastSentence;

    /// <summary>
    /// Called when the reader presses and holds a sentence: play from here.
    ///
    /// It falls out of the same map that drives the highlight, and it is also the fix for a
    /// highlight that has drifted — pressing the right sentence puts the audio back where it
    /// belongs instead of leaving the reader to hunt with the scrubber.
    /// </summary>
    public void OnSentenceTapped(int sentenceIndex)
    {
        _lastSentence = sentenceIndex;

        if (_sync?.AudioPositionAtSentence(sentenceIndex) is { } at)
        {
            playback.SeekTo(at);
            FollowStatus = "";
        }
        else
        {
            // Nothing has measured this passage yet, so there is no audio position to jump to.
            // Saying so beats a press that silently does nothing.
            FollowStatus = settings.LiveRefinement
                ? "Ovaj dio još nije izmjeren — pusti zvuk pa će ga izmjeriti."
                : "Ovaj dio još nije poravnan.";
        }

        HighlightRequested?.Invoke(this, sentenceIndex);
    }

    /// <summary>Records where the reader stopped, so opening the book again lands in the right place.</summary>
    public Task SavePositionAsync(int? topSentenceIndex)
    {
        if (_text is null) return Task.CompletedTask;

        var index = topSentenceIndex ?? _lastSentence;
        if (index < 0 || index >= _text.Sentences.Count) return Task.CompletedTask;

        return database.SaveReadingStateAsync(BookId, textOffset: _text.Sentences[index].Start);
    }

    [RelayCommand]
    private void ToggleFollow() => IsFollowing = !IsFollowing;

    [RelayCommand]
    private Task CloseAsync() => Shell.Current.GoToAsync("..");

    public void Dispose()
    {
        _ticker?.Stop();

        // Only a run this page started. A whole-book alignment the user asked for on the book page
        // is theirs to stop, and closing the reader is not that.
        if (alignment.Status.IsFollowing) alignment.Stop();
    }

    /// <summary>
    /// Wraps a document's body in the shell that makes it readable and interactive.
    ///
    /// The spans are already in the HTML — placed by the extractor in the same pass that produced
    /// the offsets alignment uses — so this only adds the styling that shows the highlight and the
    /// click handler that reports which sentence was tapped.
    /// </summary>
    private string BuildPage(string bodyHtml)
    {
        var page = new StringBuilder();

        // The text is laid out in screen-wide CSS columns rather than one long scroll, so it reads
        // as pages that turn sideways — what a book does, and what makes a horizontal swipe mean
        // something. Vertical scrolling is switched off entirely, which frees the downward swipe
        // to mean "show me the chapters".
        page.Append("""
            <!doctype html>
            <html><head>
            <meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1">
            <style>
              html, body { height: 100%; margin: 0; overflow: hidden; }
              body { font-family: FONTFAMILY; font-size: FONTSIZEpx; line-height: 1.65;
                     letter-spacing: LETTERSPACING;
                     color: FOREGROUND; background: BACKGROUND;
                     -webkit-user-select: none; user-select: none; }
              #size {
                position: fixed; left: 50%; top: 50%; transform: translate(-50%, -50%);
                padding: 10px 16px; border-radius: 10px; font-family: sans-serif; font-size: 15px;
                background: rgba(0, 0, 0, 0.72); color: #fff; opacity: 0; pointer-events: none;
                transition: opacity 140ms linear; z-index: 10;
              }
              #size.on { opacity: 1; }
              /* Scrolled programmatically rather than moved with a transform: an element with
                 overflow:hidden clips its own later columns, so translating it would slide a box
                 whose content past the first page had already been cut away. overflow:hidden still
                 permits scripted scrolling, which is exactly what is wanted — pages, no scrollbar,
                 no dragging. */
              #content {
                box-sizing: border-box;
                height: 100%;
                padding: 22px 20px;
                overflow: hidden;
                scroll-behavior: smooth;
                column-width: calc(100vw - 40px);
                column-gap: 40px;
                column-fill: auto;
              }
              img { max-width: 100%; max-height: 76vh; height: auto; }
              p { margin: 0 0 0.85em; }
              h1, h2, h3 { margin: 0 0 0.6em; }
              span[data-idx] { transition: background-color 120ms linear; }
              span.hl { background: HIGHLIGHT; border-radius: 3px; }
            </style>
            <style id="hlrule">span.hl { background: HIGHLIGHT; border-radius: 3px; }</style>
            </head><body>
            <div id="size"></div>
            <div id="content">
            """);

        page.Append(bodyHtml);

        page.Append("""
            </div>
            <script>
              var content = document.getElementById('content');
              var page = 0, pageCount = 1, current = -1;

              function step() { return window.innerWidth; }

              function measure() {
                // scrollWidth spans every column, so it gives the page count directly.
                pageCount = Math.max(1, Math.round(content.scrollWidth / step()));
                if (page > pageCount - 1) page = pageCount - 1;
                apply();
              }

              function apply() {
                content.scrollLeft = page * step();
                location.href = 'abr://pages/' + (page + 1) + '/' + pageCount;
              }

              // -1 means "the last page", used when arriving backwards from the next document.
              function goToPage(n) {
                page = n < 0 ? pageCount - 1 : Math.max(0, Math.min(n, pageCount - 1));
                apply();
              }

              function turn(forward) {
                if (forward && page < pageCount - 1) { page++; apply(); return; }
                if (!forward && page > 0) { page--; apply(); return; }

                // Past the edge of this document, so the host moves to the next or previous one.
                location.href = forward ? 'abr://page/next' : 'abr://page/previous';
              }

              function highlight(idx) {
                if (idx === current) return;
                document.querySelectorAll('span.hl').forEach(function (e) { e.classList.remove('hl'); });

                var parts = document.querySelectorAll('[data-idx="' + idx + '"]');
                parts.forEach(function (e) { e.classList.add('hl'); });
                current = idx;

                // Turn to whichever page the sentence fell on rather than scrolling to it.
                if (parts.length) {
                  var left = parts[0].getBoundingClientRect().left + content.scrollLeft;
                  goToPage(Math.floor(left / step()));
                }
              }

              // The first sentence on the current page, used to remember the reading position.
              function topSentence() {
                var spans = document.querySelectorAll('[data-idx]');
                for (var i = 0; i < spans.length; i++) {
                  var box = spans[i].getBoundingClientRect();
                  if (box.right > 0 && box.left < step()) return spans[i].getAttribute('data-idx');
                }
                return '-1';
              }

              // Gestures are handled here rather than by a recognizer around the WebView, because
              // the web view consumes its own touches and a recognizer outside it never sees them.
              //
              // A plain tap shows and hides the controls; jumping the narration to a sentence is a
              // press and hold. That way round because tapping is what a hand resting on a phone
              // does by accident, and having that throw the audio somewhere else is far worse than
              // having it show a toolbar.
              var startX = 0, startY = 0, swiped = false, holdTimer = 0, held = false;

              function cancelHold() {
                clearTimeout(holdTimer);
                holdTimer = 0;
              }

              document.addEventListener('touchstart', function (e) {
                startX = e.changedTouches[0].clientX;
                startY = e.changedTouches[0].clientY;
                swiped = false;
                held = false;

                if (e.touches.length !== 1) { cancelHold(); return; }

                var span = e.target.closest ? e.target.closest('[data-idx]') : null;
                if (!span) return;

                var idx = span.getAttribute('data-idx');

                holdTimer = setTimeout(function () {
                  held = true;
                  holdTimer = 0;

                  // Confirms the press landed, since the audio may take a moment to find the place.
                  highlight(parseInt(idx, 10));
                  location.href = 'abr://seek/' + idx;
                }, 450);
              }, { passive: true });

              document.addEventListener('touchmove', function (e) {
                var dx = Math.abs(e.touches[0].clientX - startX);
                var dy = Math.abs(e.touches[0].clientY - startY);

                // A finger on its way somewhere is not a press and hold.
                if (dx > 12 || dy > 12) cancelHold();
              }, { passive: true });

              document.addEventListener('touchend', function (e) {
                cancelHold();

                var dx = e.changedTouches[0].clientX - startX;
                var dy = e.changedTouches[0].clientY - startY;

                if (Math.abs(dx) > 55 && Math.abs(dx) > Math.abs(dy)) {
                  swiped = true;
                  turn(dx < 0);
                  return;
                }

                // A pull downwards opens the chapter list — there is no vertical scrolling to
                // confuse it with.
                if (dy > 80 && Math.abs(dy) > Math.abs(dx)) {
                  swiped = true;
                  location.href = 'abr://chapters';
                }
              }, { passive: true });

              document.addEventListener('click', function (e) {
                // Neither a swipe nor a completed hold should also count as a tap.
                if (swiped) { swiped = false; return; }
                if (held) { held = false; return; }

                location.href = 'abr://tap';
              });

              // Restyles a page already on screen, keeping the reader on the passage they were
              // looking at — changing the face reflows the columns, so the page number moves.
              function applyAppearance(bg, fg, family, hl, spacing) {
                var anchor = topSentence();

                document.body.style.background = bg;
                document.body.style.color = fg;
                document.body.style.fontFamily = family;
                document.body.style.letterSpacing = spacing;

                var rule = document.getElementById('hlrule');
                rule.textContent = 'span.hl { background: ' + hl + '; border-radius: 3px; }';

                measure();

                if (anchor !== '-1') {
                  var parts = document.querySelectorAll('[data-idx="' + anchor + '"]');
                  if (parts.length) {
                    var left = parts[0].getBoundingClientRect().left + content.scrollLeft;
                    goToPage(Math.floor(left / step()));
                  }
                }
              }

              // Pinch to resize the text.
              //
              // Handled here for the same reason the swipes are: the web view keeps its own
              // touches. Changing font size reflows the columns, so the page count changes and the
              // reader is kept on the passage they were looking at rather than on a page number
              // that now means something else.
              var pinchStart = 0, sizeAtPinchStart = 0, sizeBadge = document.getElementById('size'), badgeTimer = 0;

              function fontSize() {
                return parseFloat(window.getComputedStyle(document.body).fontSize);
              }

              function spread(touches) {
                var dx = touches[0].clientX - touches[1].clientX;
                var dy = touches[0].clientY - touches[1].clientY;
                return Math.hypot(dx, dy);
              }

              function showSize(size) {
                sizeBadge.textContent = size + ' px';
                sizeBadge.classList.add('on');

                clearTimeout(badgeTimer);
                badgeTimer = setTimeout(function () { sizeBadge.classList.remove('on'); }, 700);
              }

              document.addEventListener('touchstart', function (e) {
                if (e.touches.length !== 2) return;

                pinchStart = spread(e.touches);
                sizeAtPinchStart = fontSize();
              }, { passive: true });

              document.addEventListener('touchmove', function (e) {
                if (e.touches.length !== 2 || pinchStart === 0) return;

                var size = Math.round(Math.max(13, Math.min(34, sizeAtPinchStart * spread(e.touches) / pinchStart)));
                if (size === Math.round(fontSize())) return;

                // Hold the first visible sentence still while the text reflows around it.
                var anchor = topSentence();

                document.body.style.fontSize = size + 'px';
                showSize(size);
                measure();

                if (anchor !== '-1') {
                  var parts = document.querySelectorAll('[data-idx="' + anchor + '"]');
                  if (parts.length) {
                    var left = parts[0].getBoundingClientRect().left + content.scrollLeft;
                    goToPage(Math.floor(left / step()));
                  }
                }
              }, { passive: true });

              document.addEventListener('touchend', function (e) {
                if (e.touches.length > 0 || pinchStart === 0) return;

                pinchStart = 0;
                location.href = 'abr://font/' + Math.round(fontSize());
              }, { passive: true });

              window.addEventListener('resize', measure);
              measure();
            </script>
            </body></html>
            """);

        return page.ToString()
            .Replace("FONTSIZE", FontSize.ToString())
            .Replace("FONTFAMILY", Font.Css)
            .Replace("LETTERSPACING", LetterSpacing)
            .Replace("BACKGROUND", Theme.Background)
            .Replace("FOREGROUND", Theme.Foreground)
            .Replace("HIGHLIGHT", Theme.Highlight);
    }
}
