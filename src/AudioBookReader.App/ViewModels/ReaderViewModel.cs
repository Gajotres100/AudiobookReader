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
    LiveSyncRunner liveSync) : ObservableObject, IDisposable
{
    private Book? _book;

    /// <summary>
    /// Whether this book measures the passage being read as it is read, rather than having been
    /// aligned in advance. A property of the book, chosen on its own page.
    /// </summary>
    private bool MeasuresWhileReading => _book?.MeasureWhileReading == true;
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
    public string FollowHint =>
        "Tekst će pratiti naraciju kad poravnanje završi. Pokreni ga preko ☰.";

    /// <summary>
    /// The general hint, shown only when nothing more specific is being said.
    ///
    /// Requires audio, and that is the whole point of the split: a book with only text is not
    /// failing to follow anything, it is simply being read. Telling a reader on every page that
    /// they could add an audiobook is an advertisement in the middle of a novel.
    ///
    /// The rest is about sharing one line with the live status, which is set precisely when
    /// following is not yet possible — so without this the two were drawn on top of each other in
    /// the commonest state of a book that has not caught up yet.
    /// </summary>
    public bool ShowFollowHint => HasAudio && !CanFollow && !IsBusy && !HasFollowStatus;

    /// <summary>
    /// Why the highlight is standing still even though this book has a sync map.
    ///
    /// A map covers the chapters a run has reached, not the book — so listening past that point
    /// leaves the text motionless with nothing to explain it, which reads as the feature being
    /// broken rather than as work still to do.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFollowStatus))]
    [NotifyPropertyChangedFor(nameof(ShowFollowHint))]
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
    [NotifyPropertyChangedFor(nameof(ShowFollowHint))]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    public partial bool HasAudio { get; set; }

    public string PlayLabel => IsPlaying ? "⏸" : "▶";

    /// <summary>
    /// True when the map covers the moment playback would resume from.
    ///
    /// Recomputed on the tick, including while paused — a passage becomes measured because sync on
    /// the fly reached it, which has nothing to do with whether anything is playing.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    public partial bool MeasuredHere { get; set; }

    /// <summary>Set when measuring has been waited for and did not arrive, so the choice returns to the user.</summary>
    private bool _playUnmeasured;

    /// <summary>
    /// Whether starting the narration here would give the reader anything to follow.
    ///
    /// Offered as a disabled control rather than one that starts a voice reading text the page
    /// cannot mark: while measuring is still working towards this passage, playing it means
    /// listening to one part of the book and looking at another. Only sync on the fly withholds it,
    /// and only until the passage is measured — a book aligned in advance is either mapped here or
    /// never will be from this screen, and refusing to play an audiobook for an hour is a worse
    /// answer than an unmarked page.
    /// </summary>
    public bool CanPlay => HasAudio && (!MeasuresWhileReading || _playUnmeasured || MeasuredHere);

    /// <summary>Re-reads whether this moment is measured. Cheap: two binary searches.</summary>
    private void RefreshMeasured()
    {
        MeasuredHere = playback.BookId == BookId
                       && _sync?.CharOffsetAt(playback.PositionMs + AnticipationMs) is not null;
    }

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

    /// <summary>
    /// The face the text is set in.
    ///
    /// One, not a choice. It was a picker on the reader's bar, which is a place for the two or
    /// three things you reach for while reading — and a face is something you settle once and then
    /// never touch. A serif stack, because that is what a book is set in, with fallbacks for
    /// whatever the device actually has.
    /// </summary>
    public const string FontCss = "Georgia, 'Noto Serif', 'Times New Roman', serif";

    /// <summary>Chosen look, kept per reader rather than per book — it belongs to the eyes.</summary>
    public int ThemeIndex
    {
        get => Math.Clamp(Preferences.Default.Get("reader.theme", 0), 0, Themes.Length - 1);
        private set => Preferences.Default.Set("reader.theme", value);
    }

    public ReaderTheme Theme => Themes[ThemeIndex];

    public string ThemeName => Theme.Name;

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
        $"applyAppearance('{Theme.Background}', '{Theme.Foreground}', \"{FontCss}\", " +
        $"'{Theme.Highlight}', 'normal')";

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

    /// <summary>The chapter the eye is on, which scrolling changes without any page turn.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DocumentLabel))]
    public partial string CurrentChapterTitle { get; set; } = "";

    public string DocumentLabel
    {
        get
        {
            if (_text is null) return "";

            var pages = PageCount > 1 ? $"str. {PageNumber} / {PageCount}" : "";
            var chapter = CurrentChapterTitle;

            return (chapter, pages) switch
            {
                ("", "") => $"{SpineIndex + 1} / {_text.Spine.Count}",
                (_, "") => chapter,
                ("", _) => pages,
                _ => $"{chapter}   ·   {pages}",
            };
        }
    }

    /// <summary>
    /// Which page a newly shown document opens on: its first, or its last when the reader arrived
    /// by swiping backwards out of the document after it.
    /// </summary>
    public int EntryPage { get; private set; }

    public void OnPagesReported(int page, int count, int topSentence)
    {
        PageNumber = page;
        PageCount = count;

        CurrentChapterTitle = ChapterTitleAtSentence(topSentence);
    }

    /// <summary>
    /// Which of the ebook's own chapters a sentence falls in.
    ///
    /// The ebook's chapters, not the library's: for a paired book those come from the audiobook's
    /// marks and carry no text ranges until alignment has run, so they cannot name where the reader
    /// is looking in a book that has not been aligned.
    /// </summary>
    private string ChapterTitleAtSentence(int sentenceIndex)
    {
        if (_text is null || sentenceIndex < 0 || sentenceIndex >= _text.Sentences.Count) return "";

        var offset = _text.Sentences[sentenceIndex].Start;

        var chapter = _readerChapters.LastOrDefault(c => c.TextStart is { } start && start <= offset);
        return chapter?.Title ?? "";
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
    /// How far ahead of the voice the marker aims, in milliseconds.
    ///
    /// The remaining error is roughly symmetric — the marker is as likely to move early as late —
    /// but the two do not look alike. Moving early is invisible: the eye is already scanning the
    /// sentence when the narrator reaches it, which is what reading along feels like anyway. Moving
    /// late is glaring, because the voice is audibly into a sentence the marker has not reached.
    /// Aiming a little ahead therefore trades a fault nobody notices for one everybody does.
    /// </summary>
    private const int AnticipationMs = 400;

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
            _libraryChapters = chapters;

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

            // Loaded, not played. Sync on the fly measures forward from the playhead and moving
            // through the text moves the playhead — both of which need the player to actually hold
            // this book. Without it, opening the reader first and never pressing play left the
            // measuring run working from zero while the reader sat in chapter five.
            if (_book.HasAudio && playback.BookId != BookId && _book.AudioPath is { } audio)
            {
                var listening = await database.GetReadingStateAsync(BookId);
                await playback.LoadAsync(BookId, audio, listening?.AudioPositionMs ?? 0, listening?.Speed ?? 1f);
            }

            if (CanFollow)
            {
                _sync = new BookSync(_text, map!, chapters);
                IsFollowing = true;
            }

            // Following measures the passage being read while it is read, so it belongs to the
            // reader's lifetime: started here, stopped when the page goes away.
            if (_book.IsPaired && MeasuresWhileReading)
            {
                // Removed first: LoadAsync runs again on every reappearance, and a singleton would
                // otherwise collect a handler per visit, each one keeping a dead page alive.
                liveSync.Progress -= OnLiveSyncProgress;
                liveSync.Progress += OnLiveSyncProgress;
                liveSync.Failed -= OnLiveSyncFailed;
                liveSync.Failed += OnLiveSyncFailed;
                liveSync.Measured -= OnLiveSyncMeasured;
                liveSync.Measured += OnLiveSyncMeasured;

                await liveSync.StartAsync(BookId);
            }

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
        _narrationAt = offset;

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
    private Task NextDocumentAsync()
    {
        EntryPage = 0;
        return GoToDocumentAsync(SpineIndex + 1);
    }

    [RelayCommand]
    private Task PreviousDocumentAsync()
    {
        // Coming backwards lands on the last page, so the text continues where the eye left it
        // rather than jumping to the top of the previous chapter.
        EntryPage = -1;
        return GoToDocumentAsync(SpineIndex - 1);
    }

    private async Task GoToDocumentAsync(int index)
    {
        if (_text is null || index < 0 || index >= _text.Spine.Count) return;

        var leaving = SpineIndex;
        ShowDocument(index);

        if (SpineIndex == leaving) return;

        var start = _text.Spine[SpineIndex].TextStart;

        if (_text.SentenceAt(start) is { } sentence)
        {
            _lastSentence = sentence.Index;
            HighlightRequested?.Invoke(this, sentence.Index);
        }

        await TakeNarrationToAsync(start);
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

        await TakeNarrationToAsync(start);
    }

    /// <summary>How long to hold playback while measuring catches up with a chapter just opened.</summary>
    private const int ReadyWaitSeconds = 45;

    /// <summary>Cancels a hold when the reader moves again before the previous one has resumed.</summary>
    private CancellationTokenSource? _holding;

    /// <summary>Where in the text the narration was before the current move.</summary>
    private int _narrationAt;

    /// <summary>
    /// Takes the narration to where the reader has just gone, and holds it there until there is
    /// something to follow.
    ///
    /// Without this the two halves come apart: the text is at chapter two and the voice is still
    /// reading chapter one, so following either drags the page back or measures a passage nobody is
    /// looking at.
    ///
    /// The pause is the point. Sync on the fly works forward from the playhead, so the moment the
    /// playhead moves it begins measuring the new chapter — but that takes a window or two, and
    /// letting the narrator run during it means hearing text that nothing is marking. Holding for a
    /// few seconds and then starting cleanly is the kinder trade, and it is only taken when it buys
    /// something: a book aligned in advance has nothing to wait for.
    /// </summary>
    private async Task TakeNarrationToAsync(int textStart)
    {
        if (_book?.HasAudio != true || !IsFollowing) return;
        if (playback.BookId != BookId) return;

        // Anything still waiting is waiting for the wrong chapter now.
        StopHolding();

        _playUnmeasured = false;
        OnPropertyChanged(nameof(CanPlay));

        var wasPlaying = playback.IsPlaying;

        // Where the chapter being left had got to, so coming back resumes there rather than at its
        // first word. Read before the seek, which is the last moment it is still true.
        RememberPosition();

        playback.Pause();

        var chapterIndex = ChapterIndexAtChar(textStart);
        playback.SeekTo(await PositionForTextAsync(textStart, chapterIndex));

        _narrationAt = textStart;

        // The map is about to grow around a new position, so whatever sentence was showing must not
        // veto the next move.
        _lastSentence = -1;

        if (!wasPlaying) return;

        // Nothing to wait for when the whole book is aligned in advance: either this chapter is
        // already mapped or it never will be from here, and standing still would just look broken.
        if (!MeasuresWhileReading)
        {
            playback.Play();
            return;
        }

        var holding = new CancellationTokenSource();
        _holding = holding;

        await HoldUntilMeasuredAsync(holding.Token);
    }

    private async Task HoldUntilMeasuredAsync(CancellationToken ct)
    {
        FollowStatus = "Mjerim ovo poglavlje — zvuk kreće čim bude spremno.";

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(ReadyWaitSeconds);

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500, ct);

                // Live measuring hands the map over as it grows; a whole-book run writes a file.
                // Asking for both costs nothing and covers a book being worked on either way.
                MaybeRefreshSyncMap();
                RefreshMeasured();

                if (!MeasuredHere) continue;

                FollowStatus = "";
                playback.Play();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Moved again before this finished. The newer move does its own holding.
            return;
        }

        // Waited and nothing came. Rather than start a voice the page cannot follow, the choice
        // goes back to the reader — with play offered again and no pretence about what it will do.
        _playUnmeasured = true;
        OnPropertyChanged(nameof(CanPlay));

        FollowStatus = "Ovaj dio još nije izmjeren. Možeš pustiti zvuk, ali tekst ga zasad neće pratiti.";
    }

    private void StopHolding()
    {
        if (_holding is not { } holding) return;

        _holding = null;
        holding.Cancel();
        holding.Dispose();

        FollowStatus = "";
    }

    /// <summary>Which of the ebook's own chapters a character offset falls in.</summary>
    private int ChapterIndexAtChar(int charOffset)
    {
        for (var i = _readerChapters.Count - 1; i >= 0; i--)
            if (_readerChapters[i].TextStart is { } start && start <= charOffset) return i;

        return 0;
    }

    private string PositionKey(int chapterIndex) => $"reader.chapter.{BookId}.{chapterIndex}";

    /// <summary>
    /// Notes where the narration had reached in the chapter now being left.
    ///
    /// Leaving a chapter half-listened and coming back to its first word is the one thing that
    /// makes moving around feel punishing — the detour gets paid for twice.
    /// </summary>
    private void RememberPosition()
    {
        if (_text is null || playback.BookId != BookId) return;

        var at = playback.PositionMs;
        if (at <= 0) return;

        Preferences.Default.Set(PositionKey(ChapterIndexAtChar(_narrationAt)), at);
    }

    /// <summary>Where the audio should go for a place in the text, best evidence first.</summary>
    private async Task<long> PositionForTextAsync(int textStart, int chapterIndex)
    {
        // Somewhere this chapter was already listened to. Preferred over the map, which says where
        // the chapter begins where this says where the listener actually stopped.
        var remembered = Preferences.Default.Get(PositionKey(chapterIndex), 0L);
        if (remembered > 0) return remembered;

        // A map that already covers this passage knows exactly when it is read.
        if (_sync?.AudioPositionAtChar(textStart) is { } known) return known;

        var chapters = await database.GetChaptersAsync(BookId);

        // Failing that, the book's own chapter marks. Matched by position rather than by index,
        // because an ebook's front matter routinely gives it chapters the audio has not.
        var target = chapters.Count == _readerChapters.Count
            ? chapters.FirstOrDefault(c => c.Index == chapterIndex)
            : null;

        if (target?.StartMs is { } at) return at;

        // Nothing lines up, so place it by proportion — sync on the fly corrects it within a window
        // or two, and being a minute out beats being a chapter out.
        if (_text is { PlainText.Length: > 0 } text && _book!.DurationMs > 0)
            return (long)(_book.DurationMs * (textStart / (double)text.PlainText.Length));

        return 0;
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
        // The page reloads on every appearance, so without this each visit would leave another
        // timer running against the same view model — each holding the book's whole text alive and
        // calling Follow five times a second on behalf of a page that is gone.
        if (_ticker is not null)
        {
            _ticker.Start();
            return;
        }

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
        if (_sync is null || MeasuresWhileReading) MaybeRefreshSyncMap();

        // Before the early returns below: whether this passage is measured decides whether play is
        // offered at all, and that has to stay true while the page sits paused waiting for it.
        RefreshMeasured();

        // Playback outlives pages and can be on a different book entirely. Following it then would
        // walk this book's text to another book's playhead.
        var playingThisBook = playback.BookId == BookId;

        if (!IsFollowing || _sync is null || !playback.IsPlaying || !playingThisBook)
        {
            if (DateTime.UtcNow - _lastMiss > TimeSpan.FromSeconds(10))
            {
                _lastMiss = DateTime.UtcNow;
                AppLog.Detail(() =>
                    $"follow idle: following={IsFollowing}, sync={_sync is not null}, playing={playback.IsPlaying}, " +
                    $"playbackBook={playback.BookId}, thisBook={BookId}");
            }

            return;
        }

        var at = playback.PositionMs + AnticipationMs;

        if (_sync.SentenceAt(at) is not { } sentence)
        {
            // The chapter being listened to may be the one a run is working on right now.
            MaybeRefreshSyncMap();

            var chapter = _sync.ChapterAt(at);

            FollowStatus = MeasuresWhileReading
                ? "Mjerim ovaj dio — tekst kreće za koji trenutak."
                : chapter is null
                    ? "Ovaj dio knjige još nije poravnan."
                    : $"Poglavlje {chapter.Index + 1} još nije poravnano — pokreni poravnanje na stranici knjige.";

            // Once a second at most, so it does not drown the log.
            if (DateTime.UtcNow - _lastMiss > TimeSpan.FromSeconds(5))
            {
                _lastMiss = DateTime.UtcNow;

                AppLog.Detail(() =>
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
            AppLog.Detail(() =>
                $"follow: {at} ms -> sentence {sentence.Index} (doc {sentence.SpineIndex}, showing {SpineIndex})");
        }

        var previousDocument = SpineIndex;
        _lastSentence = sentence.Index;

        // Kept current as the voice moves, so that leaving a chapter records the position under the
        // chapter actually being left rather than the last one jumped to by hand.
        _narrationAt = sentence.Start;

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
            FollowStatus = MeasuresWhileReading
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

    /// <summary>
    /// Opens the full player: cover, chapter list, scrubber, sleep timer, speed, bookmarks.
    ///
    /// A paired book opens in the text now, so this is how the listening side is reached. Back
    /// rather than forward when the player is already the page underneath, so the two do not stack
    /// up on each other as the user moves between them.
    /// </summary>
    [RelayCommand]
    private Task OpenPlayerAsync() =>
        Shell.Current.Navigation.NavigationStack is [.., Views.BookPage, _]
            ? Shell.Current.GoToAsync("..")
            : Shell.Current.GoToAsync($"book?id={BookId}");

    /// <summary>The book's files, alignment and deletion — the same page the player's ☰ opens.</summary>
    [RelayCommand]
    private Task OpenDetailsAsync() => Shell.Current.GoToAsync($"details?id={BookId}");

    /// <summary>Only shown while nothing more important has the status line.</summary>
    private void OnLiveSyncProgress(object? sender, string message)
    {
        if (_sync is null || !CanFollow) FollowStatus = message;
    }

    /// <summary>
    /// Shown whatever else is on the status line.
    ///
    /// Progress is hidden once the text is moving, and that is right — but a failure hidden the same
    /// way is how following came to stop after half a page with nothing on screen saying so.
    /// </summary>
    private void OnLiveSyncFailed(object? sender, string message) => FollowStatus = message;

    /// <summary>
    /// Takes the map straight from the run that is building it.
    ///
    /// Waiting for the file was the reason following stuttered: the run writes it every few windows
    /// and the reader looked every ten seconds, so an anchor could be most of a minute old before
    /// the text could use it — and while measuring only runs about twice real time, most of a
    /// minute is most of the lead. Following would catch the voice, reach the end of what it had,
    /// and stop until the next read.
    /// </summary>
    private void OnLiveSyncMeasured(object? sender, SyncMap map)
    {
        if (_text is null || _libraryChapters is null) return;

        var appeared = _sync is null;

        _sync = new BookSync(_text, map, _libraryChapters) { ExtrapolateAheadMs = LiveExtrapolationMs };
        RefreshMeasured();

        if (appeared)
        {
            CanFollow = true;
            IsFollowing = true;
            FollowStatus = "";
        }

        // The map moved underneath, so the sentence chosen from the old one must not veto the next.
        _lastSentence = -1;
    }

    /// <summary>
    /// How far past the last anchor the highlight may be carried while measuring is still catching
    /// up. One window's worth: enough to bridge the gap between windows, short enough that a run
    /// which has actually stopped shows as stopped rather than drifting away on its own.
    /// </summary>
    private const long LiveExtrapolationMs = 20_000;

    private IReadOnlyList<Chapter>? _libraryChapters;

    public void Dispose()
    {
        StopHolding();
        _ticker?.Stop();

        liveSync.Progress -= OnLiveSyncProgress;
        liveSync.Failed -= OnLiveSyncFailed;
        liveSync.Measured -= OnLiveSyncMeasured;
        _ = liveSync.StopAsync();
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
              body { font-family: {{FONTFAMILY}}; font-size: {{FONTSIZE}}px; line-height: 1.65;
                     letter-spacing: {{LETTERSPACING}};
                     color: {{FOREGROUND}}; background: {{BACKGROUND}};
                     -webkit-user-select: none; user-select: none; }
              #size {
                position: fixed; left: 50%; top: 50%; transform: translate(-50%, -50%);
                padding: 10px 16px; border-radius: 10px; font-family: sans-serif; font-size: 15px;
                background: rgba(0, 0, 0, 0.72); color: #fff; opacity: 0; pointer-events: none;
                transition: opacity 140ms linear; z-index: 10;
              }
              #size.on { opacity: 1; }
              /* One continuous column, scrolled downwards. Text that flows on rather than breaking
                 into fixed pages is what the reader asked for and what a phone does naturally: a
                 sentence is never cut in half by a page edge, and the thumb already knows how to
                 move it. */
              #content {
                box-sizing: border-box;
                height: 100%;
                padding: 22px 20px 40vh;
                overflow-y: auto;
                overflow-x: hidden;
                -webkit-overflow-scrolling: touch;
              }
              #content::-webkit-scrollbar { width: 0; }
              img { max-width: 100%; max-height: 76vh; height: auto; }
              p { margin: 0 0 0.85em; }
              h1, h2, h3 { margin: 0 0 0.6em; }
              span[data-idx] { transition: background-color 120ms linear; }
              span.hl { background: {{HIGHLIGHT}}; border-radius: 3px; }
            </style>
            <style id="hlrule">span.hl { background: {{HIGHLIGHT}}; border-radius: 3px; }</style>
            </head><body>
            <div id="size"></div>
            <div id="content">
            """);

        page.Append(bodyHtml);

        page.Append("""
            </div>
            <script>
              var content = document.getElementById('content');
              var pageCount = 1, current = -1, reported = '';

              // A "page" is one screenful. The text scrolls freely, but the slider and the counter
              // still need a unit, and a screenful is the one the reader can see.
              function step() { return content.clientHeight; }

              function pageNow() {
                return Math.max(0, Math.round(content.scrollTop / step()));
              }

              function measure() {
                pageCount = Math.max(1, Math.ceil(content.scrollHeight / step()));
                report();
              }

              function report() {
                var page = pageNow();
                var top = topSentence();

                // The sentence travels with the page so the host can name the chapter being read.
                // Scrolling is the only thing that moves it, and the host has no other way to know.
                var state = page + '/' + pageCount + '/' + top;
                if (state === reported) return;

                reported = state;
                location.href = 'abr://pages/' + (page + 1) + '/' + pageCount + '/' + top;
              }

              // -1 means "the end", used when arriving backwards from the document after this one.
              function goToPage(n) {
                var page = n < 0 ? pageCount - 1 : Math.max(0, Math.min(n, pageCount - 1));
                content.scrollTop = page * step();
                report();
              }

              content.addEventListener('scroll', function () {
                clearTimeout(window.__scrollReport);
                window.__scrollReport = setTimeout(report, 120);
              }, { passive: true });

              function highlight(idx) {
                if (idx === current) return;
                document.querySelectorAll('span.hl').forEach(function (e) { e.classList.remove('hl'); });

                var parts = document.querySelectorAll('[data-idx="' + idx + '"]');
                parts.forEach(function (e) { e.classList.add('hl'); });
                current = idx;

                if (!parts.length) return;

                // Scrolled only when the sentence has left the comfortable band, and then brought
                // to a third of the way down rather than to the top. Following the voice line by
                // line would keep the page in constant motion, which is unreadable; letting it
                // reach the bottom edge before moving means the reader can always see what comes
                // next.
                var box = parts[0].getBoundingClientRect();
                var height = content.clientHeight;

                if (box.top > height * 0.12 && box.bottom < height * 0.78) return;

                content.scrollTo({
                  top: content.scrollTop + box.top - height * 0.32,
                  behavior: 'smooth'
                });
              }

              // Puts a sentence back where the eye was after the text has reflowed under it — which
              // it does whenever the size or the face changes.
              function keepInView(idx) {
                if (idx === '-1') return;

                var parts = document.querySelectorAll('[data-idx="' + idx + '"]');
                if (!parts.length) return;

                content.scrollTop += parts[0].getBoundingClientRect().top - content.clientHeight * 0.12;
                report();
              }

              // The first sentence visible, used to remember the reading position.
              function topSentence() {
                var spans = document.querySelectorAll('[data-idx]');
                for (var i = 0; i < spans.length; i++) {
                  var box = spans[i].getBoundingClientRect();
                  if (box.bottom > 0 && box.top < content.clientHeight) return spans[i].getAttribute('data-idx');
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

                // Down the page belongs to the text now, so only sideways is a gesture: it moves
                // between the book's documents, which is the one thing scrolling cannot do.
                if (Math.abs(dx) > 60 && Math.abs(dx) > Math.abs(dy) * 1.5) {
                  swiped = true;
                  location.href = dx < 0 ? 'abr://page/next' : 'abr://page/previous';
                  return;
                }

                // A finger that moved at all was scrolling, not tapping.
                if (Math.abs(dx) > 12 || Math.abs(dy) > 12) swiped = true;
              }, { passive: true });

              document.addEventListener('click', function (e) {
                // Neither a scroll nor a completed hold should also count as a tap.
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
                keepInView(anchor);
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
                keepInView(anchor);
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
            .Replace("{{FONTSIZE}}", FontSize.ToString())
            .Replace("{{FONTFAMILY}}", FontCss)
            .Replace("{{LETTERSPACING}}", "normal")
            .Replace("{{BACKGROUND}}", Theme.Background)
            .Replace("{{FOREGROUND}}", Theme.Foreground)
            .Replace("{{HIGHLIGHT}}", Theme.Highlight);
    }
}
