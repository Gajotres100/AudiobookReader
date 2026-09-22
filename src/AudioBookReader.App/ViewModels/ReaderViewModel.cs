using AudioBookReader.App.Resources.Strings;
using System.Text;
using AudioBookReader.App.Services;
using AudioBookReader.Core.Books;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioBookReader.App.ViewModels;

[QueryProperty(nameof(BookId), "id")]
[QueryProperty(nameof(EntryOffset), "offset")]
public partial class ReaderViewModel(
    LibraryDatabase database,
    SyncMapStore syncMaps,
    BookTextExtractors extractors,
    PlaybackController playback,
    LiveSyncRunner liveSync) : ObservableObject, IDisposable
{
    private Book? _book;

    /// <summary>
    /// A specific place to open at instead of the remembered one, carried from a bookmark chosen
    /// on another page. -1 means none was asked for; consumed once by the load that follows, so
    /// simply reappearing on this same page later resumes normally again.
    /// </summary>
    [ObservableProperty]
    public partial int EntryOffset { get; set; } = -1;

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
    [NotifyPropertyChangedFor(nameof(CanSpeak))]
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
        Strings.Reader_WillFollow;

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

    /// <summary>
    /// Confirms a bookmark actually saved something, the same way the player's held flag does.
    /// Its own line rather than reusing <see cref="FollowStatus"/>: that one is rewritten by the
    /// follow loop every time the narration moves on, and a confirmation that vanishes under the
    /// next tick before anyone reads it is not a confirmation.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBookmarkNote))]
    public partial string BookmarkNote { get; set; } = "";

    public bool HasBookmarkNote => BookmarkNote.Length > 0;

    /// <summary>
    /// Bookmarks the passage a selection starts in — the same record the player writes from the
    /// audio side, into the same list, so a book read from both ends shows one merged trail of
    /// places rather than two the reader never sees together.
    ///
    /// Takes a sentence index rather than a raw character offset, the same coordinate every other
    /// message from the page already uses (<see cref="OnSentenceTapped"/>, <see cref="PendingHighlight"/>)
    /// — the page knows which sentence a tap landed in from its own <c>data-idx</c> markup, not
    /// where that sentence starts in the book's text, which is this class's own bookkeeping.
    ///
    /// Carries the audio position too when the book is paired and this passage has already been
    /// aligned, so a bookmark set with the eyes can still be jumped to from the player. Unaligned
    /// or unpaired, it carries only the text offset, the same as one placed on the audio side
    /// before that book had text at all.
    /// </summary>
    public async Task SaveSelectionBookmarkAsync(int sentenceIndex)
    {
        if (_text is null || sentenceIndex < 0 || sentenceIndex >= _text.Sentences.Count) return;

        var textOffset = _text.Sentences[sentenceIndex].Start;

        var existing = await database.GetBookmarksAsync(BookId);
        if (existing.Count >= BookmarksViewModel.Limit)
        {
            BookmarkNote = string.Format(Strings.Bookmarks_LimitReachedBook, BookmarksViewModel.Limit);
            await Task.Delay(2500);
            BookmarkNote = "";
            return;
        }

        var previewLength = Math.Min(90, _text.PlainText.Length - textOffset);

        await database.AddBookmarkAsync(new Bookmark
        {
            BookId = BookId,
            TextOffset = textOffset,
            PositionMs = _sync?.AudioPositionAtChar(textOffset),
            ChapterIndex = _libraryChapters?.LastOrDefault(c => c.HasTextRange && c.TextStart <= textOffset)?.Index ?? 0,
            TextPreview = _text.PlainText.Substring(textOffset, previewLength).ReplaceLineEndings(" ").Trim(),
            CreatedUtc = DateTime.UtcNow,
        });

        BookmarkNote = Strings.Bookmarks_Saved;
        await Task.Delay(2500);
        BookmarkNote = "";
    }

    /// <summary>Whether to keep the highlight moving with playback. Off means plain reading.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FollowLabel))]
    public partial bool IsFollowing { get; set; }

    public string FollowLabel => IsFollowing ? Strings.Reader_FollowOn : Strings.Reader_Follow;

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
    [NotifyPropertyChangedFor(nameof(CanSpeak))]
    public partial bool HasAudio { get; set; }

    /// <summary>
    /// The transport glyph.
    ///
    /// Both carry the text presentation selector. Without it Android renders U+23F8 as a
    /// colour emoji and U+25B6 as plain text, so play and pause — the same control one moment
    /// apart — came out as two different objects in two different styles.
    /// </summary>
    public string PlayLabel => IsPlaying ? "⏸︎" : "▶︎";

    /// <summary>
    /// True while a chapter just jumped to is being measured and playback is deliberately waiting.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    public partial bool IsWaitingToSpeak { get; set; }

    /// <summary>
    /// Whether play is offered.
    ///
    /// Withheld in exactly one situation: the few seconds after a chapter jump, while measuring
    /// works towards the new position and the status line says so. Starting there would mean a
    /// voice reading text the page cannot mark.
    ///
    /// It is deliberately not withheld merely because the passage is unmeasured. That looked like
    /// the same rule but is a very different one — on a book that has never been played there is no
    /// playhead, nothing measured anywhere, and gating play on measurement leaves a dead button and
    /// no way to ever start the book. The control that starts a thing cannot depend on the thing
    /// having started.
    /// </summary>
    public bool CanPlay => HasAudio && !IsWaitingToSpeak;


    [RelayCommand]
    private async Task TogglePlayAsync()
    {
        if (!await EnsurePlaybackLoadedAsync()) return;

        playback.TogglePlayPause();
        IsPlaying = playback.IsPlaying;
    }

    /// <summary>
    /// Makes sure the player is holding this book before anything asks it to seek or play.
    ///
    /// The reader can be opened, and text can be tapped or held, without the player ever having
    /// been told which book it is — nothing forces that to happen first. Without this, double
    /// tapping a sentence in a book that had never been played did nothing: the seek went to
    /// whichever book the player happened to be holding, or nowhere at all if it was holding none.
    /// </summary>
    private async Task<bool> EnsurePlaybackLoadedAsync()
    {
        if (playback.BookId == BookId) return true;
        if (_book?.AudioPath is not { } audioPath) return false;

        var state = await database.GetReadingStateAsync(BookId);
        return await playback.LoadAsync(
            BookId, audioPath, state?.AudioPositionMs ?? 0, state?.Speed ?? 1f,
            title: _book.Title, author: _book.Author, coverPath: _book.CoverPath,
            chapterStarts: ChapterStarts());
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
    ///
    /// Dim and Night were first tuned lower on exactly that reasoning, and turned out to have gone
    /// too far the other way: an amber wash that faint over a near-black page barely lifts off it
    /// at all, reported from a real evening of reading in both. Raised well past where a light
    /// theme would need it, since the same opacity reads much weaker the darker the page is.
    /// </summary>
    public static readonly ReaderTheme[] Themes =
    [
        // Papir is the app's own page, to the character: opening the reader should feel like
        // turning into the book rather than like arriving somewhere else.
        new(Strings.Reader_ThemePaper, "#FBF8F2", "#241F1A", "rgba(178, 106, 0, 0.22)"),
        new(Strings.Reader_ThemeSepia, "#EFE0C6", "#4A3A26", "rgba(168, 112, 0, 0.26)"),
        new(Strings.Reader_ThemeLight, "#FDFDFB", "#1B1B1F", "rgba(196, 148, 0, 0.30)"),
        new(Strings.Reader_ThemeDim, "#1C212B", "#E9E4DA", "rgba(232, 169, 69, 0.34)"),
        new(Strings.Reader_ThemeNight, "#0B0D11", "#A9A39A", "rgba(232, 169, 69, 0.40)"),
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
        Strings.Reader_Appearance,
        [.. Themes.Select(t => t.Name)],
        index =>
        {
            ThemeIndex = index;
            OnPropertyChanged(nameof(ThemeName));
        });

    private async Task PickAsync(string title, string[] options, Action<int> chosen)
    {
        var choice = await Shell.Current.DisplayActionSheetAsync(title, Strings.Common_Cancel, null, options);

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

    /// <summary>
    /// Where each chapter begins in the audio, for the controls outside the app.
    ///
    /// From the library's chapters, not the reader's: the reader's come from the ebook and carry
    /// text ranges, while these are the ones with times in them.
    /// </summary>
    private long[] ChapterStarts() =>
        _libraryChapters is null
            ? []
            : [.. _libraryChapters.Where(c => c.HasAudioRange).Select(c => c.StartMs ?? 0).Order()];

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

            var pages = PageCount > 1 ? string.Format(Strings.Reader_PageOf, PageNumber, PageCount) : "";
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
    ///
    /// Null outside of that swipe, so a fresh document load falls through to restoring the
    /// remembered sentence instead — which used to be skipped whenever this happened to be sitting
    /// on its default value of 0 from an unrelated earlier visit.
    /// </summary>
    public int? EntryPage { get; private set; }

    public void OnPagesReported(int page, int count, int topSentence)
    {
        PageNumber = page;
        PageCount = count;

        _topSentence = topSentence;
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
            // Read before anything is shown, because showing a document needs it. Once per
            // process: the file cannot change under a running app.
            await ShellAsync();

            _book = await database.GetBookAsync(BookId);
            if (_book?.EbookPath is null) return;

            await database.MarkOpenedAsync(BookId);

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
            //
            // Never while something is playing: opening one book's text to check a name would
            // otherwise silence the book being listened to.
            if (_book.HasAudio && playback.BookId != BookId && !playback.IsPlaying
                && _book.AudioPath is { } audio)
            {
                var listening = await database.GetReadingStateAsync(BookId);
                await playback.LoadAsync(
                    BookId, audio, listening?.AudioPositionMs ?? 0, listening?.Speed ?? 1f,
                    title: _book.Title, author: _book.Author, coverPath: _book.CoverPath,
                    chapterStarts: ChapterStarts());
            }

            if (CanFollow)
            {
                _sync = new BookSync(_text, map!, chapters);
                IsFollowing = true;
            }
            else
            {
                // Cleared rather than left standing. The page object outlives a visit, so a map
                // deleted while the reader was away would otherwise go on answering questions from
                // whatever this field was still holding.
                _sync = null;
                IsFollowing = false;
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
    /// Opens wherever the reader should resume: a bookmark chosen elsewhere when one was asked
    /// for, the narrator's position when following, otherwise wherever they last stopped reading.
    /// </summary>
    private async Task ShowStartingDocumentAsync()
    {
        // Cleared unconditionally rather than left to whichever swipe last set it: this method
        // runs on every reappearance of the page, on the same view model instance, and a page
        // request left over from an earlier visit — including one abandoned by cancelling the
        // chapter picker — must not steal a fresh restore of the remembered sentence below.
        EntryPage = null;

        int offset;

        // Whether somebody named this spot — a bookmark, or a photographed page the search placed
        // — as opposed to the book simply being reopened where it was left.
        var asked = EntryOffset >= 0;

        if (asked)
        {
            offset = EntryOffset;
            EntryOffset = -1;
        }
        else
        {
            var state = await database.GetReadingStateAsync(BookId);

            offset = CanFollow && playback.PositionMs > 0
                ? _sync!.CharOffsetAt(playback.PositionMs) ?? state?.TextOffset ?? 0
                : state?.TextOffset ?? 0;
        }

        ShowDocumentAt(offset);
        _narrationDocument = SpineIndex;

        // The voice starts where the eye is, not where listening last stopped. In a read-along
        // those are usually the same place, and when they are not it is the page in front of the
        // reader that is right — opening chapter one and pressing play should read chapter one, not
        // resume halfway through it because that is where a previous session ended.
        //
        // Not blocking Play while this settles: nothing has played yet, so nothing is playing FOR
        // the correction loop to protect — it used to hold Play disabled for up to forty-five
        // seconds on a book nobody had listened to a second of yet, which could only ever time out,
        // since live measuring has nothing to have found near a passage that has never been heard.
        if (!playback.IsPlaying)
            await TakeNarrationToAsync(
                offset, blockPlayWhileCorrecting: false, userMoved: false, exactPlace: asked);

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
        // Cleared rather than set to the first page: a chapter does not have to begin where its
        // document does — a TOC entry pointing at an anchor halfway down a large spine file is
        // ordinary — and asking for page zero would land at the top of that file instead of at the
        // chapter. The sentence highlighted below already says exactly where to go.
        EntryPage = null;

        if (_readerChapters.Count == 0) return;

        // Numbered so that chapters sharing a title stay distinguishable in the list.
        var labels = _readerChapters.Select((c, i) => $"{i + 1}. {c.Title}").ToArray();

        var choice = await Shell.Current.DisplayActionSheetAsync(
            Strings.Chapter_Pick, Strings.Common_Cancel, null, labels);
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

    /// <summary>
    /// How far past a requested offset to look for one the map covers. About a paragraph — far
    /// enough past a heading and an opening line, near enough that it is still the same place.
    /// </summary>
    private const int LookAheadChars = 1_200;

    /// <summary>How long to hold playback while measuring catches up with a chapter just opened.</summary>
    private const int ReadyWaitSeconds = 45;

    /// <summary>Cancels a hold when the reader moves again before the previous one has resumed.</summary>
    private CancellationTokenSource? _holding;

    /// <summary>
    /// Whether the reader has taken the narration somewhere on purpose during this visit.
    ///
    /// The distinction the saved audio position depends on. Every move through the text seeks the
    /// player, including the one that merely opens the book — and that one seeks to whatever
    /// <see cref="PositionForTextAsync"/> could guess, which for a book with no map is the start of
    /// it. A guess is fine to listen from and fatal to save, so only a page turn, a chapter jump or
    /// a tapped sentence counts as progress worth writing down.
    /// </summary>
    private bool _narrationTakenHere;

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
    /// <param name="blockPlayWhileCorrecting">
    /// Whether Play should stay disabled while this waits to correct the position.
    ///
    /// True for a jump made while there is a real chance something is already measured nearby —
    /// mid-listening, moving between chapters. False for placing the very first position of a
    /// session, before anything has ever played: there is nothing yet for live measuring to have
    /// found in a book nobody has listened to, so the wait can only ever time out, and disabling
    /// Play for the better part of a minute over a book someone has only just opened to read is
    /// exactly the dead button this feature exists to avoid.
    /// </param>
    /// <param name="userMoved">
    /// Whether this is the reader going somewhere rather than the book merely being opened. Sets
    /// <see cref="_narrationTakenHere"/>, which is what allows the position reached to be saved.
    /// </param>
    /// <param name="exactPlace">
    /// Whether <paramref name="textStart"/> is a place somebody actually named, rather than just
    /// the document being entered. Stops the remembered position for that document standing in for
    /// it — see <see cref="PositionForTextAsync"/>.
    /// </param>
    private async Task TakeNarrationToAsync(
        int textStart, bool blockPlayWhileCorrecting = true, bool userMoved = true,
        bool exactPlace = false)
    {
        // Deliberately not conditional on IsFollowing. That is only true once a map exists, and a
        // book measured live has none until the first window lands — so on exactly the book this
        // was written for, moving through the text moved nothing and the voice carried on in the
        // chapter left behind. Telling the narration where the reader went is also how measuring
        // learns which passage to work on, so it has to happen before there is anything to follow.
        if (_book?.HasAudio != true) return;
        if (!await EnsurePlaybackLoadedAsync()) return;

        if (userMoved) _narrationTakenHere = true;

        // Anything still waiting is waiting for the wrong chapter now.
        StopHolding();

        var wasPlaying = playback.IsPlaying;

        // Where the document being left had got to. Read before the seek, which is the last moment
        // it is still true.
        RememberPosition();

        var arriving = SpineIndex;

        playback.Pause();
        playback.SeekTo(await PositionForTextAsync(textStart, arriving, exactPlace));

        _narrationDocument = arriving;

        // The map is about to grow around a new position, so whatever sentence was showing must not
        // veto the next move.
        _lastSentence = -1;

        // A book aligned in advance has nothing to measure, but it still has a seek to land. Play
        // called in the same breath as the seek starts on whatever the player is still holding, so
        // a jump began with a second or two of the passage being left before the new one arrived.
        // A short settle costs nothing and removes it.
        if (!MeasuresWhileReading)
        {
            var settling = new CancellationTokenSource();
            _holding = settling;

            await SettleSeekAsync(resume: wasPlaying, settling.Token);
            return;
        }

        // Corrected whether or not the voice was running, which is the case that was missed. Most
        // jumps are made while paused — you find the chapter, then press play — and the correction
        // only ran for a jump made mid-sentence. So the guess stayed uncorrected, play started from
        // it, and half a minute later a window finally matched and dragged the text back to
        // wherever the guess had actually landed. Doing the work while paused means the position is
        // already right by the time anyone presses anything.
        var holding = new CancellationTokenSource();
        _holding = holding;

        await HoldUntilMeasuredAsync(textStart, resume: wasPlaying, blockPlayWhileCorrecting, holding.Token);
    }

    /// <summary>How far off the target the voice may start, in milliseconds. About one sentence.</summary>
    private const long CloseEnoughMs = 4_000;

    /// <summary>How many times a guess may be corrected before the result is accepted as it is.</summary>
    private const int MaxCorrections = 4;

    /// <summary>
    /// Waits for the passage to be measured, and uses each measurement to correct where the voice
    /// was put.
    ///
    /// The seek that precedes this is a guess, and on a book whose ebook and audiobook do not
    /// divide into the same chapters it can be minutes out — which is how asking for chapter seven
    /// started the voice in the middle of chapter six. Nothing afterwards ever revisited that guess:
    /// the wait only asked whether the place it had landed was measured, and it was, so it played
    /// there.
    ///
    /// So the wait now aims at the piece of text the reader actually asked for. Each window that
    /// lands tells us where the voice really is in the text, and the distance from there to the
    /// target converts back into time — a correction anchored on a measurement rather than on a
    /// proportion across ten hours, which is what makes it converge instead of merely differing.
    /// </summary>
    /// <summary>How long to give a seek before speaking anyway.</summary>
    private const int SettleSeconds = 5;

    /// <summary>
    /// Waits for the player to actually be sitting on the position it was sent to, then speaks.
    ///
    /// A seek is a request, not a fact: the player reports the old position for a moment and then
    /// buffers, and starting playback in that window plays the passage being left. Two hundred
    /// milliseconds of the wrong chapter is still the wrong chapter, and it is the first thing
    /// heard after asking for a new one.
    /// </summary>
    private async Task SettleSeekAsync(bool resume, CancellationToken ct)
    {
        IsWaitingToSpeak = true;
        FollowStatus = Strings.Reader_PreparingChapter;

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(SettleSeconds);

            while (DateTime.UtcNow < deadline && !playback.IsReadyToPlay)
                await Task.Delay(150, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            IsWaitingToSpeak = false;
        }

        FollowStatus = "";

        // Whatever is showing was chosen against the old position; let the next tick place it.
        _lastSentence = -1;

        if (resume) playback.Play();
    }

    private async Task HoldUntilMeasuredAsync(int targetChar, bool resume, bool blockPlay, CancellationToken ct)
    {
        if (blockPlay) IsWaitingToSpeak = true;
        FollowStatus = Strings.Reader_PreparingChapter;

        var corrections = 0;

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(ReadyWaitSeconds);

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500, ct);

                // Live measuring hands the map over as it grows; a whole-book run writes a file.
                // Asking for both costs nothing and covers a book being worked on either way.
                MaybeRefreshSyncMap();

                // The map now knows when this very text is read, so stop guessing and use it.
                if (_sync?.AudioPositionAtChar(targetChar) is { } exact)
                {
                    if (Math.Abs(exact - playback.PositionMs) > CloseEnoughMs) playback.SeekTo(exact);

                    AppLog.Info($"reader: settled on char {targetChar} at {exact} ms after {corrections} corrections");
                    Settle();
                    return;
                }

                // Not the target, but the voice's own position may now be known in the text. The
                // gap between that and the target is a real distance, and the book's own pace turns
                // it back into a time to move by.
                //
                // Only where it is genuinely measured. Reading it from the map wherever the map
                // would answer meant taking an interpolation across an unmeasured gap as fact: one
                // correction landed the voice outside anything that had been listened to, the next
                // reading came back as the opening of the book, and it moved by forty-seven
                // minutes to chase it. Measurements only, and it waits for one.
                var at = playback.PositionMs;

                if (_sync?.MeasuredCharOffsetAt(at, MeasuredWindowMs) is not { } voiceAt) continue;

                var drift = (long)((targetChar - voiceAt) / CharsPerMs);

                if (Math.Abs(drift) <= CloseEnoughMs || corrections >= MaxCorrections)
                {
                    Settle();
                    return;
                }

                corrections++;
                playback.SeekTo(Math.Max(0, at + drift));

                AppLog.Info(
                    $"reader: correction {corrections} — voice at char {voiceAt}, wanted {targetChar}, " +
                    $"moved {drift} ms");

                // A seek needs a moment before the player reports the position it landed on, and
                // measuring needs longer than that to reach it. Reading again half a second later
                // produced the nonsense above.
                await Task.Delay(2_000, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Moved again before this finished. The newer move does its own holding.
            return;
        }
        finally
        {
            if (blockPlay) IsWaitingToSpeak = false;
        }

        // Waited and nothing came. Rather than start a voice the page cannot follow behind the
        // user's back, the choice goes back to them, with what will happen stated rather than
        // implied.
        AppLog.Info($"reader: gave up placing char {targetChar} after {corrections} corrections");

        FollowStatus = Strings.Reader_UnmeasuredCanPlay;
        return;

        void Settle()
        {
            if (blockPlay) IsWaitingToSpeak = false;
            FollowStatus = "";

            // Only resumes what was already running. A jump made while paused leaves it paused —
            // with the position now correct, which was the whole point of waiting.
            if (resume) playback.Play();
        }
    }

    /// <summary>
    /// How close an anchor must be for a position to count as measured rather than interpolated.
    /// One live window either side.
    /// </summary>
    private const long MeasuredWindowMs = 20_000;

    /// <summary>The book's average pace, used only to turn a distance in the text back into a time.</summary>
    private double CharsPerMs =>
        _text is { PlainText.Length: > 0 } text && _book is { DurationMs: > 0 }
            ? text.PlainText.Length / (double)_book.DurationMs
            : 0.015;

    private void StopHolding()
    {
        IsWaitingToSpeak = false;

        if (_holding is not { } holding) return;

        _holding = null;
        holding.Cancel();
        holding.Dispose();

        FollowStatus = "";
    }

    /// <summary>The document the narration is in, which is what a remembered position is filed under.</summary>
    private int _narrationDocument = -1;

    private string PositionKey(int documentIndex) => $"reader.at3.{BookId}.{documentIndex}";

    /// <summary>
    /// Notes where the narration had reached in the document now being left, so coming back to it
    /// resumes rather than restarts.
    ///
    /// Only what the map vouches for, and only for a book aligned in advance — see
    /// <see cref="PositionForTextAsync"/> for why the two modes differ.
    /// </summary>
    private void RememberPosition()
    {
        if (MeasuresWhileReading || _text is null || playback.BookId != BookId) return;
        if (_narrationDocument < 0) return;

        var at = playback.PositionMs;
        if (at <= 0) return;

        // A position is worth keeping because the voice was reading this document, and the only
        // thing that knows whether it was is the map.
        if (_sync?.CharOffsetAt(at) is not { } readingAt) return;
        if (_text.SpineAt(readingAt)?.Index != _narrationDocument) return;

        Preferences.Default.Set(PositionKey(_narrationDocument), at);
    }

    /// <summary>Which of the ebook's own chapters a character offset falls in.</summary>
    private int ChapterIndexAtChar(int charOffset)
    {
        for (var i = _readerChapters.Count - 1; i >= 0; i--)
            if (_readerChapters[i].TextStart is { } start && start <= charOffset) return i;

        return 0;
    }

    /// <summary>
    /// Where the audio should go for a place in the text, best evidence first.
    ///
    /// Whether a document remembers where it was left depends on the mode, and the two really are
    /// different. A book aligned in advance has a complete map: the position it recorded is exact,
    /// and coming back to a chapter you were half way through and being returned there is a
    /// kindness. A book measured in the reader has a map full of holes, and the positions it
    /// recorded were often taken while playback was somewhere the map only guessed at — so being
    /// dropped a third of the way into a chapter you asked for reads as the app ignoring you.
    /// </summary>
    private async Task<long> PositionForTextAsync(int textStart, int documentIndex, bool exactPlace)
    {
        var chapterIndex = ChapterIndexAtChar(textStart);

        // Skipped when the place was named rather than merely arrived at. The remembered position
        // answers "I am back in this document" and deliberately ignores textStart — which is the
        // right answer for a swipe into a chapter and the wrong one for a bookmark, or for a page
        // found from a photograph: both of those say exactly where to be, and returning the spot
        // the document was last left at instead sent the voice, and then the text following it,
        // straight back to where the reader had already been.
        if (!exactPlace && !MeasuresWhileReading && documentIndex >= 0
            && Preferences.Default.Get(PositionKey(documentIndex), 0L) is > 0 and var remembered)
            return remembered;

        // A map that already covers this passage knows exactly when it is read — or the first
        // sentence just after it does, which for a chapter opening is usually the case.
        if (_sync?.AudioPositionAtOrAfterChar(textStart, LookAheadChars) is { } known) return known;

        var chapters = await database.GetChaptersAsync(BookId);

        // The audiobook's own marks, when the two sides divide the book the same way.
        if (chapters.Count == _readerChapters.Count
            && chapters.FirstOrDefault(c => c.Index == chapterIndex)?.StartMs is { } exact)
            return exact;

        if (_text is not { PlainText.Length: > 0 } text || _book!.DurationMs <= 0) return 0;

        // Otherwise place it by proportion — and then put it on the nearest chapter mark, if one is
        // near enough to be the same chapter.
        //
        // Proportion alone is what sent a jump to chapter six into the end of chapter five: an
        // ebook's front matter gives it documents the audio has none of, so the counts differ, the
        // exact match above is skipped, and a raw proportion lands wherever the arithmetic falls —
        // minutes from any boundary. Snapping recovers the thing actually asked for, which is the
        // start of a chapter, without needing the two sides to agree on how many there are.
        var proportional = (long)(_book.DurationMs * (textStart / (double)text.PlainText.Length));

        var nearest = chapters
            .Where(c => c.StartMs is not null)
            .OrderBy(c => Math.Abs(c.StartMs!.Value - proportional))
            .FirstOrDefault();

        if (nearest?.StartMs is { } mark && Math.Abs(mark - proportional) <= SnapToChapterMs)
            return mark;

        return proportional;
    }

    /// <summary>
    /// How far a proportional guess may be from a chapter mark and still be treated as that chapter.
    ///
    /// Five minutes: long enough to absorb the drift a proportion accumulates over ten hours,
    /// short enough that it cannot reach past a neighbouring chapter of any ordinary length.
    /// </summary>
    private const long SnapToChapterMs = 5 * 60 * 1000;

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

        // A player that has failed says nothing on its own: the button responds, the glyph does not
        // change, and no sound comes out. Say which of the two it is.
        if (playback.LastError is { } failure)
            FollowStatus = string.Format(Strings.Reader_AudioFailed, failure);

        // Playback outlives pages and can be on a different book entirely. Following it then would
        // walk this book's text to another book's playhead.
        var loadedThisBook = playback.BookId == BookId;

        // Saved here rather than left to a sentence tap: tapping a sentence saves the text
        // position, not this, and someone listening with their eyes closed — the whole point of a
        // sleep timer — never taps anything. Without this, nothing wrote the audio position for a
        // paired book read through this page at all, so a process killed while the phone slept lost
        // the entire session and came back wherever it last happened to be saved, which could be
        // nowhere later than the very start.
        //
        // Not conditional on playback.IsPlaying, because turning a page or tapping a sentence seeks
        // the player (see TakeNarrationToAsync) whether or not anything is audibly playing, and
        // someone reading in silence moves that seek just as much as someone listening does — but
        // conditional on one of those two things having actually happened. Merely opening the text
        // also seeks, to a position PositionForTextAsync had to guess: for a paired book with no
        // map that guess is the start of the book, and writing it here would overwrite hours of
        // real listening with a zero a second after the reader was opened to glance at a name.
        if (loadedThisBook && (playback.IsPlaying || _narrationTakenHere)
            && DateTime.UtcNow - _lastPositionSaved > TimeSpan.FromSeconds(5))
        {
            _lastPositionSaved = DateTime.UtcNow;
            _ = database.SaveReadingStateAsync(BookId, audioPositionMs: playback.PositionMs, speed: playback.Speed);
        }

        if (!IsFollowing || _sync is null || !playback.IsPlaying || !loadedThisBook)
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
                ? Strings.Reader_MeasuringHere
                : chapter is null
                    ? Strings.Reader_PartNotAligned
                    : string.Format(Strings.Reader_ChapterNotAligned, chapter.Index + 1);

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
        _narrationDocument = sentence.SpineIndex;

        // Crossing into another document means loading a different page before highlighting in it.
        ShowDocumentAt(sentence.Start);

        // A document change reloads the web view, and the script that does the highlighting will
        // not exist until it finishes. The page re-applies this once loaded; asking now would be
        // swallowed and never retried, because the sentence index has already been consumed.
        if (SpineIndex == previousDocument) HighlightRequested?.Invoke(this, sentence.Index);
    }

    private DateTime _lastMiss = DateTime.MinValue;
    private DateTime _lastTrace = DateTime.MinValue;
    private DateTime _lastPositionSaved = DateTime.MinValue;

    /// <summary>The sentence the page should highlight once a freshly loaded document is ready.</summary>
    public int PendingHighlight => _lastSentence;

    /// <summary>
    /// Called when the reader presses and holds a sentence: play from here.
    ///
    /// This is the gesture for "start at this exact place", so it has to work at any place — and
    /// it used to work only where the map already knew the answer, which is the case that needed no
    /// help. Anywhere else it refused and left the voice where it was, so pressing the first line
    /// of a chapter did nothing and playback carried on from page seven.
    ///
    /// Where the map knows, it is exact and instant. Where it does not, this takes the same route a
    /// chapter jump takes: place the voice as well as can be guessed, then correct the guess from
    /// what measuring finds.
    /// </summary>
    public void OnSentenceTapped(int sentenceIndex)
    {
        _lastSentence = sentenceIndex;
        HighlightRequested?.Invoke(this, sentenceIndex);

        _ = SafelyAsync("placing the narration by hand", () => SeekNarrationToSentenceAsync(sentenceIndex));
    }

    /// <summary>
    /// The fast path used to call <see cref="PlaybackController.SeekTo"/> directly, on the
    /// assumption that a book with a map is a book the player already holds — true once someone
    /// has pressed play, and never true for a double tap that is the very first thing done with
    /// the book. The player then either seeked whatever book it happened to be holding, or did
    /// nothing at all if it was holding none, which is what looked like the gesture being ignored.
    /// </summary>
    private async Task SeekNarrationToSentenceAsync(int sentenceIndex)
    {
        if (!await EnsurePlaybackLoadedAsync()) return;

        if (_sync?.AudioPositionAtSentence(sentenceIndex) is { } at)
        {
            playback.SeekTo(at);
            FollowStatus = "";
            return;
        }

        if (_text is null || sentenceIndex < 0 || sentenceIndex >= _text.Sentences.Count) return;

        // A tapped sentence is as named as a place gets: the same substitution that sent a
        // photographed page back to where the document was last left would do it here too, for
        // any sentence the map does not already cover.
        var start = _text.Sentences[sentenceIndex].Start;
        await TakeNarrationToAsync(start, exactPlace: true);
    }

    /// <summary>Runs work started by a gesture, so a failure lands in the log instead of the process.</summary>
    private static async Task SafelyAsync(string what, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            AppLog.Error(what, ex);
        }
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

    /// <summary>
    /// Leaves the reader.
    ///
    /// The player is directly underneath whenever it was opened from there (see
    /// <see cref="OpenPlayerAsync"/>), the same alternating-view pair the player's own close button
    /// accounts for. A single pop would only hop back across to the player instead of leaving the
    /// book, so that case pops past it too.
    /// </summary>
    [RelayCommand]
    private Task CloseAsync()
    {
        var stack = Shell.Current.Navigation.NavigationStack;

        return stack.Count >= 2 && stack[^1] is Views.ReaderPage && stack[^2] is Views.BookPage
            ? Shell.Current.GoToAsync("../..")
            : Shell.Current.GoToAsync("..");
    }

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
        StopSpeaking();
        StopHolding();
        _ticker?.Stop();

        liveSync.Progress -= OnLiveSyncProgress;
        liveSync.Failed -= OnLiveSyncFailed;
        liveSync.Measured -= OnLiveSyncMeasured;
        _ = liveSync.StopAsync();
    }

    /// <summary>
    /// The reader page's shell, read once from the app package and kept for the life of the process.
    ///
    /// It used to be a pair of raw string literals in this file — three hundred lines of HTML, CSS
    /// and JavaScript with no syntax highlighting, no way to open the page in a browser to see what
    /// a gesture actually did, and every brace competing with C#'s own. As a file it is none of
    /// those things, and this class lost a third of its length in the move.
    /// </summary>
    private static string? _shell;

    private static async Task<string> ShellAsync()
    {
        if (_shell is not null) return _shell;

        await using var stream = await FileSystem.OpenAppPackageFileAsync("reader.html");
        using var reader = new StreamReader(stream);

        return _shell = await reader.ReadToEndAsync();
    }

    /// <summary>
    /// Wraps a document's body in the shell that makes it readable and interactive.
    ///
    /// The spans are already in the HTML — placed by the extractor in the same pass that produced
    /// the offsets alignment uses — so the shell only adds the styling that shows the highlight and
    /// the click handler that reports which sentence was tapped.
    /// </summary>
    private string BuildPage(string bodyHtml) =>
        (_shell ?? "")
            .Replace("{{FONTSIZE}}", FontSize.ToString())
            .Replace("{{FONTFAMILY}}", FontCss)
            .Replace("{{LETTERSPACING}}", "normal")
            .Replace("{{BACKGROUND}}", Theme.Background)
            .Replace("{{FOREGROUND}}", Theme.Foreground)
            .Replace("{{HIGHLIGHT}}", Theme.Highlight)
            .Replace("{{TRANSLATE_LABEL}}", Strings.Reader_TranslateAction)
            .Replace("{{BOOKMARK_LABEL}}", Strings.Reader_BookmarkAction)
            // Last, so a book whose own text happens to contain one of the names above is left
            // alone rather than having it substituted out from under it.
            .Replace("{{BODY}}", bodyHtml);

    // ---- Reading it aloud ----

    /// <summary>
    /// The sentence at the top of the page, as the page last reported it. Where the voice starts.
    /// </summary>
    private int _topSentence;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeakLabel))]
    public partial bool IsSpeaking { get; set; }

    /// <summary>
    /// Whether the phone can read this book out loud.
    ///
    /// Only a book with no narration of its own. Where a real reader exists, a synthesised one is a
    /// worse version of what the book already has, and the two would fight over the same page — so
    /// attaching an audiobook to a book later takes this away, which is the right way round.
    /// </summary>
    public bool CanSpeak => !HasAudio && !IsBusy;

    public string SpeakLabel => IsSpeaking ? "⏸︎" : "▶︎";

    private CancellationTokenSource? _speech;

    /// <summary>
    /// Starts the voice, or stops it.
    ///
    /// Deliberately not an async command. An async one disables its own button for as long as it
    /// is running, and this one runs for as long as the book is being read aloud — so the pause
    /// button was dead for exactly the stretch anybody would press it. Starting and stopping is
    /// instant; the reading itself is left running behind it.
    /// </summary>
    [RelayCommand]
    private void ToggleSpeech()
    {
        if (IsSpeaking)
        {
            StopSpeaking();
            _ = SavePositionAsync(null);
            return;
        }

        if (_text is null || _text.Sentences.Count == 0) return;

        var speech = new CancellationTokenSource();
        _speech = speech;
        IsSpeaking = true;
        FollowStatus = "";

        _ = ReadAloudAsync(speech);
    }

    private async Task ReadAloudAsync(CancellationTokenSource speech)
    {
        try
        {
            await SpeakAsync(speech.Token);
        }
        catch (OperationCanceledException)
        {
            // Pressing pause, or leaving the page.
        }
        catch (Exception ex)
        {
            // A phone with no speech engine, or one that has none for this book's language. Said on
            // screen rather than only in the log: from the reader's side the button simply did
            // nothing, and that is the one outcome worth explaining.
            AppLog.Error("reading the book aloud", ex);
            FollowStatus = Strings.Reader_NoVoice;
        }
        finally
        {
            if (ReferenceEquals(_speech, speech)) StopSpeaking();
            await SavePositionAsync(null);
        }
    }

    /// <summary>
    /// Speaks one sentence at a time, from wherever the reader is looking.
    ///
    /// Sentence by sentence rather than the whole book at once: it is what makes the text follow
    /// along, what lets pausing happen within a few words rather than at the end of a chapter, and
    /// what keeps a synthesiser from being handed half a megabyte of prose in one call.
    /// </summary>
    private async Task SpeakAsync(CancellationToken ct)
    {
        if (_text is null) return;

        var voice = await VoiceForBookAsync();
        var index = Math.Clamp(_lastSentence >= 0 ? _lastSentence : _topSentence, 0, _text.Sentences.Count - 1);
        var spokenSinceSave = 0;

        while (!ct.IsCancellationRequested && index < _text.Sentences.Count)
        {
            var sentence = _text.Sentences[index];

            SpeakingAt(sentence);

            var words = _text.PlainText[sentence.Start..sentence.End].Trim();

            // Sentences that are only punctuation or a stray heading marker: nothing to say, and
            // some engines answer an empty string by never completing.
            if (words.Length > 0)
                await TextToSpeech.Default.SpeakAsync(words, new SpeechOptions { Locale = voice }, ct);

            index++;

            if (++spokenSinceSave >= 10)
            {
                spokenSinceSave = 0;
                await SavePositionAsync(null);
            }
        }
    }

    /// <summary>
    /// Puts the sentence being spoken on screen and lights it up.
    ///
    /// Deliberately its own path rather than a call into the narration's follow loop: that loop is
    /// driven by where the audio is and belongs to the alignment, and a voice with no recording
    /// behind it has no business inside it.
    /// </summary>
    private void SpeakingAt(Sentence sentence)
    {
        var previousDocument = SpineIndex;
        _lastSentence = sentence.Index;

        ShowDocumentAt(sentence.Start);

        // A document change reloads the page, and the script that highlights will not exist until
        // it has. The page re-applies PendingHighlight once it is ready.
        if (SpineIndex == previousDocument) HighlightRequested?.Invoke(this, sentence.Index);
    }

    /// <summary>
    /// A voice in the book's own language, when the phone has one.
    ///
    /// The language was worked out at import and is already on the book, so there is nothing to
    /// guess: a Croatian novel read in an English voice is unlistenable in a way that is obvious in
    /// one sentence. Null hands the choice back to the system default, which is the right answer
    /// when the phone has nothing closer.
    /// </summary>
    private async Task<Locale?> VoiceForBookAsync()
    {
        if (_book?.Language is not { Length: > 0 } language) return null;

        try
        {
            var voices = await TextToSpeech.Default.GetLocalesAsync();

            return voices.FirstOrDefault(v =>
                v.Language.StartsWith(language, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            AppLog.Error("looking for a voice", ex);
            return null;
        }
    }

    private void StopSpeaking()
    {
        _speech?.Cancel();
        _speech?.Dispose();
        _speech = null;

        IsSpeaking = false;
    }
}
