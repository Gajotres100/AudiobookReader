using AudioBookReader.App.Resources.Strings;
using System.Collections.ObjectModel;
using System.Net;
using AudioBookReader.App.Services;
using AudioBookReader.App.Views;
using AudioBookReader.Core.Alignment;
using AudioBookReader.Core.Books;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plugin.Maui.OCR;

namespace AudioBookReader.App.ViewModels;

[QueryProperty(nameof(BookId), "id")]
public partial class BookViewModel(
    LibraryDatabase database,
    LibraryService library,
    SyncMapStore syncMaps,
    BookImporter importer,
    BookFilePicker picker,
    PlaybackController playback,
    AlignmentQueue alignment,
    LiveSyncRunner liveSync,
    BookTextExtractors extractors,
    PhotoPicker photos,
    IOcrService ocr,
    AlignmentSettingsStore alignmentSettings,
    WhisperModelStore models,
    AlignmentShare share,
    MediaReferences references) : ObservableObject, IDisposable
{
    /// <summary>The speeds the button cycles through. Nothing below 0.75 or above 2 is useful for narration.</summary>
    private static readonly float[] Speeds = [1f, 1.25f, 1.5f, 1.75f, 2f, 0.75f];

    private Book? _book;
    private List<Chapter> _chapters = [];
    private Chapter? _currentChapter;
    private IDispatcherTimer? _ticker;
    private DateTime _lastSaved = DateTime.MinValue;

    [ObservableProperty]
    public partial int BookId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChapterHeading))]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string Author { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCover))]
    [NotifyPropertyChangedFor(nameof(HasNoCover))]
    public partial string? CoverPath { get; set; }

    public bool HasCover => !string.IsNullOrEmpty(CoverPath) && File.Exists(CoverPath);

    public bool HasNoCover => !HasCover;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoAudio))]
    [NotifyPropertyChangedFor(nameof(CanRemoveAudio))]
    [NotifyPropertyChangedFor(nameof(CanRemoveText))]
    public partial bool HasAudio { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoText))]
    [NotifyPropertyChangedFor(nameof(CanRemoveAudio))]
    [NotifyPropertyChangedFor(nameof(CanRemoveText))]
    [NotifyPropertyChangedFor(nameof(CanLocatePage))]
    [NotifyPropertyChangedFor(nameof(ShowsPageFinder))]
    public partial bool HasText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartAlignment))]
    [NotifyPropertyChangedFor(nameof(CanClearAlignment))]
    [NotifyPropertyChangedFor(nameof(CanShareAlignment))]
    [NotifyPropertyChangedFor(nameof(ShowsAlignmentCard))]
    public partial bool IsPaired { get; set; }

    /// <summary>
    /// The book came whole as an EPUB 3 with its own narration. Its files and its alignment came
    /// together and only make sense together, so the page offers nothing to change them — only
    /// finding a page from a photo, and delete.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsAlignmentCard))]
    [NotifyPropertyChangedFor(nameof(ShowsFilesCard))]
    public partial bool IsReadAlong { get; set; }

    public bool ShowsAlignmentCard => IsPaired && !IsReadAlong;

    public bool ShowsFilesCard => !IsReadAlong;

    /// <summary>A read-along keeps this one: finding the page from a photo changes nothing about the book.</summary>
    public bool ShowsPageFinder => HasText;

    public bool HasNoAudio => !HasAudio;

    public bool HasNoText => !HasText;

    /// <summary>
    /// Removing a medium is only offered while the other one would survive it. Taking away the
    /// last one is deleting the book, and that has its own button — offering it here would put the
    /// user in front of a control that can only fail.
    /// </summary>
    public bool CanRemoveAudio => HasAudio && HasText;

    public bool CanRemoveText => HasText && HasAudio;

    // ---- Transport ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayGlyph))]
    public partial bool IsPlaying { get; set; }

    /// <summary>The one control that has to be readable at arm's length, so it carries a glyph, not a word.</summary>
    // U+FE0E asks for the text presentation. Without it iOS draws U+25B6 as a blue emoji tile
    // inside the brass button; Android happened not to, which is why it went unnoticed.
    public string PlayGlyph => IsPlaying ? "❚❚" : "▶︎";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    [NotifyPropertyChangedFor(nameof(Progress))]
    [NotifyPropertyChangedFor(nameof(PlayedWidth))]
    public partial long PositionMs { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    [NotifyPropertyChangedFor(nameof(Progress))]
    public partial long DurationMs { get; set; }

    [ObservableProperty]
    public partial string SpeedText { get; set; } = "1×";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChapterHeading))]
    public partial string ChapterTitle { get; set; } = "";

    /// <summary>What the middle of the chapter bar says. Falls back to the title so it is never blank.</summary>
    public string ChapterHeading => string.IsNullOrWhiteSpace(ChapterTitle) ? Title : ChapterTitle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSleepTimer))]
    [NotifyPropertyChangedFor(nameof(SleepButtonText))]
    public partial string SleepText { get; set; } = "";

    public bool HasSleepTimer => SleepText.Length > 0;

    /// <summary>Shows the time left once a timer is armed, so its state is visible without opening the menu.</summary>
    public string SleepButtonText => HasSleepTimer ? $"⏱ {SleepText}" : Strings.Player_SleepAfter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>
    /// The scrubber and the times run across the current chapter, not the whole book.
    ///
    /// Over eleven hours a book-wide bar barely moves and says nothing useful; within a chapter it
    /// answers the question a listener actually has — how far into this bit am I, and how much of
    /// it is left.
    /// </summary>
    public long ChapterPositionMs =>
        _currentChapter?.StartMs is { } start ? Math.Max(0, PositionMs - start) : PositionMs;

    public long ChapterDurationMs =>
        _currentChapter is { DurationMs: > 0 } chapter ? chapter.DurationMs : DurationMs;

    public string PositionText => Format(ChapterPositionMs);

    public string DurationText => Format(ChapterDurationMs);

    public double Progress =>
        ChapterDurationMs > 0 ? Math.Clamp(ChapterPositionMs / (double)ChapterDurationMs, 0, 1) : 0;

    /// <summary>
    /// How wide the bar is on screen, reported by the page as it lays out.
    ///
    /// The part already listened to is drawn as a thicker bar over the track rather than tinted,
    /// and a bar has to be given a width in pixels — a fraction is not something a layout can use.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayedWidth))]
    public partial double TrackWidth { get; set; }

    public double PlayedWidth => Math.Max(0, TrackWidth * Progress);

    // ---- Alignment ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdleWithMessage))]
    public partial string AlignmentMessage { get; set; } = "";

    /// <summary>A finished, stopped or failed run still has something to say once it is over.</summary>
    public bool IsIdleWithMessage => !IsAligning && AlignmentMessage.Length > 0;

    [ObservableProperty]
    public partial double AlignmentFraction { get; set; }

    /// <summary>
    /// How far through the chapter being worked on.
    ///
    /// Shown beside the book-wide figure because on a forty-chapter book the overall bar advances
    /// by a fortieth every couple of minutes, which for the first two minutes is indistinguishable
    /// from nothing happening at all.
    /// </summary>
    [ObservableProperty]
    public partial double ChapterFraction { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartAlignment))]
    [NotifyPropertyChangedFor(nameof(IsIdleWithMessage))]
    [NotifyPropertyChangedFor(nameof(CanClearAlignment))]
    [NotifyPropertyChangedFor(nameof(CanShareAlignment))]
    public partial bool IsAligning { get; set; }

    /// <summary>
    /// The two ways to get a read-along, offered as a choice because that is what they are.
    ///
    /// Turning one on turns the other off. They are not settings that combine: either the whole
    /// book is sampled in advance and the gaps interpolated, or nothing is done ahead of time and
    /// the passage being read is measured exactly as it is reached.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartAlignment))]
    [NotifyPropertyChangedFor(nameof(CanClearAlignment))]
    [NotifyPropertyChangedFor(nameof(CanShareAlignment))]
    [NotifyPropertyChangedFor(nameof(ClearAlignmentText))]
    [NotifyPropertyChangedFor(nameof(AlignmentSummary))]
    public partial bool MeasuresWhileReading { get; set; }

    [ObservableProperty]
    public partial bool AlignsInAdvance { get; set; }

    /// <summary>Guards against the two toggles setting each other back and forth forever.</summary>
    private bool _switchingMode;

    partial void OnMeasuresWhileReadingChanged(bool value) => ApplyMode(value, isLive: true);

    partial void OnAlignsInAdvanceChanged(bool value) => ApplyMode(value, isLive: false);

    private void ApplyMode(bool value, bool isLive)
    {
        if (_switchingMode || _book is null) return;

        _switchingMode = true;

        try
        {
            // One of the two is always on: switching a mode off means choosing the other.
            var live = isLive ? value : !value;

            MeasuresWhileReading = live;
            AlignsInAdvance = !live;

            _book.MeasureWhileReading = live;
            _ = database.UpdateBookAsync(_book);

            if (live) _ = FetchModelAsync();
        }
        finally
        {
            _switchingMode = false;
        }
    }

    /// <summary>What the speech model is doing, for the line under the reading-along choice.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelStatus))]
    public partial string ModelStatus { get; set; } = "";

    public bool HasModelStatus => ModelStatus.Length > 0;

    /// <summary>
    /// Fetches the speech model as soon as reading-along is chosen.
    ///
    /// The reader deliberately used to leave this alone, on the grounds that opening a book is no
    /// reason to spend thirty megabytes of someone's data. Choosing reading-along is — it is the
    /// user asking for exactly the thing that needs the model. Waiting until the reader opened only
    /// meant the first read-along session began with nothing to follow and a hint pointing at a
    /// button this mode switches off.
    /// </summary>
    private async Task FetchModelAsync()
    {
        // Nothing could ever load it here, and it is tens of megabytes on a device that is often
        // short of space.
        if (!SpeechSupport.IsAvailable) return;

        var model = alignmentSettings.Model;
        if (models.IsDownloaded(model)) return;

        var lastPercent = -1;

        // Whole percents only: progress arrives every 80 KB, and each report crosses to the UI.
        var progress = new Progress<double>(fraction =>
        {
            var percent = (int)(fraction * 100);
            if (percent == lastPercent) return;

            lastPercent = percent;
            ModelStatus = string.Format(Strings.Progress_DownloadingModelPercent, percent);
        });

        ModelStatus = Strings.Progress_DownloadingModel;

        try
        {
            await models.EnsureAsync(model, progress);
            ModelStatus = Strings.Model_Ready;
        }
        catch (Exception ex)
        {
            AppLog.Error("downloading the speech model", ex);
            ModelStatus = string.Format(Strings.Model_DownloadFailed, ex.Message);
        }
    }

    public bool CanStartAlignment => CanAlign && IsPaired && !IsAligning && !MeasuresWhileReading;

    /// <summary>
    /// Whether this book can be aligned here: the device must be able to recognise speech (see
    /// <see cref="SpeechSupport"/>), and the audio must be on the device — recognition reads it in
    /// short bursts for hours, which a book played from the server does not have to give.
    /// </summary>
    public bool CanAlign => SpeechSupport.IsAvailable && !StreamedAudio.Is(_book?.AudioPath);

    public bool CannotAlign => !CanAlign;

    /// <summary>Why not, in words, when alignment is not on offer.</summary>
    public string AlignmentUnavailableReason =>
        !SpeechSupport.IsAvailable ? Strings.Details_AlignmentUnsupported
        : StreamedAudio.Is(_book?.AudioPath) ? Strings.Details_StreamedNoAlign
        : "";

    /// <summary>Chapters holding real anchors — not chapters merely visited by a run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlignmentSummary))]
    [NotifyPropertyChangedFor(nameof(StartAlignmentText))]
    [NotifyPropertyChangedFor(nameof(CanClearAlignment))]
    [NotifyPropertyChangedFor(nameof(CanShareAlignment))]
    public partial int AlignedChapterCount { get; set; }

    /// <summary>How much audio has genuinely been measured, which is what live measuring produces.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlignmentSummary))]
    [NotifyPropertyChangedFor(nameof(CanClearAlignment))]
    [NotifyPropertyChangedFor(nameof(CanShareAlignment))]
    public partial long MeasuredMs { get; set; }

    /// <summary>Only worth offering once there is something to discard.</summary>
    /// <summary>
    /// Whether there is measured alignment to throw away.
    ///
    /// Offered in both modes. Sync on the fly deliberately builds on whatever is already stored,
    /// which is right when the point is to have a working book — but it leaves no way to start
    /// clean, and starting clean is exactly what you need when you are trying to find out whether
    /// the live measuring works at all.
    /// </summary>
    /// <summary>
    /// Always offered for a paired book, running or not, measured or not.
    ///
    /// It used to hide itself while a run was active and while nothing had been measured yet —
    /// which meant the one moment someone most wants to bail out of a bad alignment, mid-run, was
    /// exactly when the button to do it was gone. Clicking it now stops whatever is running first.
    /// </summary>
    public bool CanClearAlignment => IsPaired;

    /// <summary>What the button does, which is not the same thing in the two modes.</summary>
    public string ClearAlignmentText => Strings.Align_Clear;

    /// <summary>The chapter a resume would begin with, computed the same way the aligner does.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlignmentSummary))]
    public partial int ResumeFromChapter { get; set; }

    /// <summary>
    /// States plainly how much is already done and where a resume would begin.
    ///
    /// Both numbers are read from the sync map rather than from a stored high-water mark, because
    /// the two can disagree: a chapter can be visited by a run and still hold nothing, and quoting
    /// the mark would promise alignment the reader will not find and name a resume point the
    /// aligner will not use.
    /// </summary>
    public string AlignmentSummary
    {
        get
        {
            if (Chapters.Count == 0) return "";

            if (MeasuresWhileReading)
            {
                // The count is here so the mode can be told apart from doing nothing: it starts at
                // whatever was already stored and climbs as you read, which is the only visible
                // evidence that measuring is happening at all.
                // Time, not chapters. A chapter counts as holding a measurement after one
                // fifteen-second window, so passing through three of them claimed three chapters
                // aligned while under a minute of the book had actually been heard.
                var measured = MeasuredMs <= 0
                    ? Strings.Align_NothingMeasured
                    : string.Format(
                        Strings.Align_MeasuredOf,
                        Format(MeasuredMs),
                        Format(_book?.DurationMs ?? 0));

                return string.Format(Strings.Align_LiveSummary, measured);
            }

            if (HasStaleAlignment)
                return Strings.Align_Stale;

            if (AlignedChapterCount == 0) return Strings.Align_NotAligned;

            if (AlignedChapterCount >= Chapters.Count)
                return string.Format(Strings.Align_FullyAligned, Chapters.Count);

            return string.Format(Strings.Align_AlignedOf, AlignedChapterCount, Chapters.Count) +
                   string.Format(Strings.Align_ResumeFrom, ResumeFromChapter + 1);
        }
    }

    public string StartAlignmentText =>
        AlignedChapterCount > 0 && AlignedChapterCount < Chapters.Count
            ? Strings.Align_Continue
            : Strings.Align_Start;

    public ObservableCollection<ChapterRow> Chapters { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChapterToggleText))]
    public partial bool HasChapters { get; set; }

    /// <summary>
    /// Collapsed by default. An audiobook can carry hundreds of chapters, and leaving them all on
    /// screen buries the controls the user actually came for.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChapterToggleText))]
    public partial bool ChaptersExpanded { get; set; }

    public string ChapterToggleText =>
        string.Format(Strings.Chapter_Toggle, Chapters.Count, ChaptersExpanded ? "▲" : "▼");

    [RelayCommand]
    private void ToggleChapters() => ChaptersExpanded = !ChaptersExpanded;

    /// <summary>
    /// Opens the book's files, alignment and deletion.
    ///
    /// Importing files, alignment and deletion are things you do to a book once; reading or playing
    /// it is what you do every day. They used to be a panel drawn over the player, which worked
    /// only for as long as every book had a player — a text-only book has none, and the reader
    /// needs the same panel just as much.
    /// </summary>
    [RelayCommand]
    private Task OpenDetailsAsync() => Shell.Current.GoToAsync($"details?id={BookId}");

    /// <summary>
    /// Whether this view model is driving a player, or only describing the book.
    ///
    /// The details page shares this view model because it acts on the same book, but shows nothing
    /// that moves — so it neither loads the player nor runs the once-a-second tick.
    /// </summary>
    public bool TracksPlayback { get; set; } = true;

    /// <summary>
    /// Leaves the player.
    ///
    /// A plain "back" is right for the details page, which sits on top of whichever view opened
    /// it and should return there. It is wrong for the player itself when the reader is directly
    /// underneath: the two are opened as alternating views of the same paired book (see
    /// <see cref="OpenReaderAsync"/>), so a single pop would only hop across to the reader instead
    /// of leaving the book — which is what the button is for. Popping past it too closes the book
    /// completely, wherever it was opened from.
    /// </summary>
    [RelayCommand]
    private Task CloseAsync()
    {
        var stack = Shell.Current.Navigation.NavigationStack;

        return stack.Count >= 2 && stack[^1] is Views.BookPage && stack[^2] is Views.ReaderPage
            ? Shell.Current.GoToAsync("../..")
            : Shell.Current.GoToAsync("..");
    }

    private static readonly int[] SleepMinutes = [5, 10, 15, 20, 30, 45, 60, 90];

    // Read once rather than declared const: a const is baked in at compile time, and these have to
    // be able to come back in a different language.
    private static string EndOfChapterChoice => Strings.Player_SleepEndOfChapter;

    private static string CancelSleepChoice => Strings.Player_SleepOff;

    // ---- Lifecycle ----

    public async Task LoadAsync()
    {
        _book = await database.GetBookAsync(BookId);
        if (_book is null) return;

        await database.MarkOpenedAsync(BookId);

        Title = _book.Title;
        Author = _book.Author ?? "";
        CoverPath = _book.CoverPath;
        HasAudio = _book.HasAudio;
        HasText = _book.HasText;
        IsPaired = _book.IsPaired;
        IsReadAlong = _book.IsReadAlong;

        _chapters = await database.GetChaptersAsync(BookId);

        Chapters.Clear();
        foreach (var chapter in _chapters)
        {
            Chapters.Add(new ChapterRow(
                chapter,
                string.IsNullOrWhiteSpace(chapter.Title)
                    ? string.Format(Strings.Chapter_Numbered, chapter.Index + 1)
                    : chapter.Title,
                chapter.StartMs is { } startMs ? Format(startMs) : ""));
        }

        HasChapters = Chapters.Count > 0;
        OnPropertyChanged(nameof(ChapterToggleText));

        // Read back without letting the toggles write it straight out again.
        _switchingMode = true;
        MeasuresWhileReading = _book.MeasureWhileReading;
        AlignsInAdvance = !_book.MeasureWhileReading;
        _switchingMode = false;

        // A book switched to reading-along before the model could be fetched — offline at the time,
        // or set up by an earlier version that never fetched it — gets it now rather than never.
        if (_book.MeasureWhileReading && _book.IsPaired) _ = FetchModelAsync();

        OnPropertyChanged(nameof(CanStartAlignment));
        OnPropertyChanged(nameof(CanClearAlignment));
        OnPropertyChanged(nameof(CanAlign));
        OnPropertyChanged(nameof(CannotAlign));
        OnPropertyChanged(nameof(AlignmentUnavailableReason));

        await RefreshAlignmentProgressAsync();

        // LoadAsync is reached from the page appearing and from every attach or removal, so the
        // handler is dropped before it is added; otherwise each one orphans a subscription on a
        // singleton that then does database work for a page that no longer exists.
        alignment.Changed -= OnAlignmentChanged;
        alignment.Changed += OnAlignmentChanged;
        ApplyAlignmentStatus(alignment.Status);

        if (!TracksPlayback) return;

        // Not awaited: connecting to the playback service starts it cold whenever nothing else
        // has touched it since the app began, which measured at roughly a second on top of
        // preparing the file itself — all of it paid again on every switch between two audio-only
        // books, since each one is a different file. None of that needs to hold up the page: the
        // title and chapter list above are already on screen, and Tick, started right after this,
        // picks up the real position and duration the moment the service actually answers.
        if (HasAudio) _ = LoadPlaybackInBackgroundAsync();

        StartTicking();
    }

    private async Task LoadPlaybackInBackgroundAsync()
    {
        try
        {
            await StartPlaybackAsync();
        }
        catch (Exception ex)
        {
            // Logged rather than shown: this used to fail silently to the same place when it was
            // awaited inline and the page's own catch swallowed it, so staying quiet here changes
            // nothing about what the reader sees, only when the rest of the page becomes usable.
            AppLog.Error("connecting to playback", ex);
        }
    }

    private async Task StartPlaybackAsync()
    {
        // Playback outlives this page, so if this book is already loaded the player's position is
        // the truth and the page adopts it. Reloading and seeking to the stored position would
        // drag the listener back to wherever they were when they last left the page — which is
        // exactly what going to the reader and back would do.
        if (playback.BookId == BookId && playback.DurationMs > 0)
        {
            AdoptPlayerState();

            // Back to a book left paused here, which another device may have carried on with since.
            if (!playback.IsPlaying) _ = OfferPositionFromElsewhereAsync();
            return;
        }

        var state = await database.GetReadingStateAsync(BookId);
        var startMs = state?.AudioPositionMs ?? 0;
        var speed = state?.Speed ?? 1f;

        // Shown before the player is asked for anything, because none of it needs the player: where
        // this book was left, how long it is and how fast it was being read are all already in hand
        // from the database. Waiting for the service to come up cold and for the file to be prepared
        // — measured together at a few seconds for a long m4b held on the user's own storage — meant
        // the page sat at 0:00 with an empty scrubber and no chapter for the whole of it, which is
        // what opening a book felt slow doing.
        PositionMs = startMs;
        DurationMs = _book!.DurationMs;
        SpeedText = FormatSpeed(speed);
        UpdateChapterTitle();

        AppLog.Info($"loading book {BookId} at {startMs} ms, speed {speed}");

        await playback.LoadAsync(
            BookId, _book.AudioPath!, startMs, speed,
            title: Title, author: Author, coverPath: _book.CoverPath,
            chapterStarts: ChapterStarts());

        _ = OfferPositionFromElsewhereAsync();
    }

    /// <summary>
    /// Offers to carry on from where another device got to, when that is newer and somewhere else.
    ///
    /// Asked rather than done, by the user's choice: someone who went back an hour on purpose must
    /// not be thrown forward again by a phone left in a drawer. Asked once the book is already loaded
    /// where this device left it, so a slow network never holds up pressing play.
    /// </summary>
    /// <param name="patience">How long the server gets to answer; short when play is waiting on it.</param>
    private async Task OfferPositionFromElsewhereAsync(TimeSpan? patience = null)
    {
        try
        {
            if (_book is null || ProgressSync.Current is not { } sync) return;
            if (await sync.FindNewerAsync(_book, forText: false, patience: patience) is not { AudioMs: { } at } elsewhere)
                return;

            // Left, or moved to another book, while the server was being asked.
            if (playback.BookId != BookId) return;

            var go = await Dialogs.AskAsync(
                Strings.Sync_ElsewhereTitle,
                string.Format(Strings.Sync_ElsewhereAudio, Clock(at), Clock(PositionMs)),
                Strings.Sync_ElsewhereGo,
                Strings.Sync_ElsewhereStay);

            // Either way it has been answered, which is what stops this same place being offered again.
            sync.Acknowledge(elsewhere);

            if (!go) return;

            playback.SeekTo(at);
            PositionMs = at;
            await database.SaveReadingStateAsync(BookId, at, speed: playback.Speed);
        }
        catch (Exception ex)
        {
            AppLog.Error("offering the position reached on another device", ex);
        }
    }

    private static string Clock(long ms) => TimeSpan.FromMilliseconds(ms) is var t && t.TotalHours >= 1
        ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
        : $"{t.Minutes}:{t.Seconds:00}";

    private void AdoptPlayerState()
    {
        PositionMs = playback.PositionMs;
        DurationMs = playback.DurationMs;
        SpeedText = FormatSpeed(playback.Speed);
        IsPlaying = playback.IsPlaying;

        UpdateChapterTitle();
    }

    /// <summary>
    /// Polls the player once a second while the page is visible.
    ///
    /// Playback lives in a service that outlives this page, so the page reads state rather than
    /// owning it. Polling beats subscribing here because the only consumer is a scrubber that is
    /// on screen or not — when the page goes away there is nothing to unsubscribe and no listener
    /// left holding a reference to a dead page.
    /// </summary>
    private void StartTicking()
    {
        // The page is reloaded on every appearance, so without this each visit would leave another
        // timer running against the same view model.
        if (_ticker is not null)
        {
            _ticker.Start();
            return;
        }

        _ticker = Application.Current?.Dispatcher.CreateTimer();
        if (_ticker is null) return;

        _ticker.Interval = TimeSpan.FromSeconds(1);
        _ticker.Tick += (_, _) => Tick();
        _ticker.Start();
    }

    private void Tick()
    {
        if (!HasAudio) return;

        // Nothing to read until the player actually holds this book. Before that the controller
        // answers for a service that is not up yet — position zero, not playing — and copying that
        // in would drag the scrubber back to the start of the book a second after the page had
        // correctly shown where the reader left off, then push that zero into the database on the
        // save below. The same guard covers a player that is busy with a different book, whose
        // position says nothing about this one.
        if (playback.BookId != BookId) return;

        IsPlaying = playback.IsPlaying;
        PositionMs = playback.PositionMs;

        if (playback.DurationMs > 0) DurationMs = playback.DurationMs;

        // Hours are shown when there are any: a ninety-minute timer formatted as mm:ss reads as
        // thirty minutes, which is exactly the number you must not get wrong at bedtime.
        SleepText = playback.SleepRemaining is { } left && left > TimeSpan.Zero
            ? left.TotalHours >= 1 ? $"{left:h\\:mm\\:ss}" : $"{left:mm\\:ss}"
            : "";

        UpdateChapterTitle();

        // Saved every few seconds rather than every tick: frequent enough that a crash costs
        // seconds of position, rare enough not to write to the database sixty times a minute.
        if (DateTime.UtcNow - _lastSaved > TimeSpan.FromSeconds(5))
        {
            _lastSaved = DateTime.UtcNow;
            _ = SavePositionAsync();
        }
    }

    /// <summary>
    /// Not when the page never got a position from the player — it starts at zero and only learns
    /// the real one once the player holds this book. Leaving before that saved the zero, and the
    /// whole book's listening progress with it.
    /// </summary>
    public Task SavePositionAsync() =>
        HasAudio && PositionMs > 0 && playback.BookId == BookId && playback.DurationMs > 0
            ? database.SaveReadingStateAsync(BookId, PositionMs, speed: playback.Speed)
            : Task.CompletedTask;

    /// <summary>
    /// Keeps the chapter-derived state in step with playback: the heading, the chapter-relative
    /// scrubber, and which row in the list is lit up.
    ///
    /// Run from these two hooks rather than only from the explicit calls elsewhere, so there is
    /// never a window where PositionMs or DurationMs has already moved but the chapter has not
    /// caught up to it. That window used to be real: StartPlaybackAsync set PositionMs, then
    /// DurationMs to the whole book's length, and only on the next line called this — and setting
    /// DurationMs fires its own PropertyChanged for Progress on the way, computed against the
    /// still-null current chapter, which is the book-wide ratio rather than the chapter's. The
    /// generated setters call these automatically, on every assignment, so the correction is never
    /// more than one step behind rather than a whole statement behind.
    /// </summary>
    partial void OnPositionMsChanged(long value) => UpdateChapterTitle();

    partial void OnDurationMsChanged(long value) => UpdateChapterTitle();

    private void UpdateChapterTitle()
    {
        var chapter = CurrentChapter();
        if (ReferenceEquals(chapter, _currentChapter)) return;

        _currentChapter = chapter;
        ChapterTitle = chapter?.Title ?? "";

        foreach (var row in Chapters) row.IsCurrent = row.Chapter.Index == chapter?.Index;

        // The chapter changed, so the scrubber's scale did too.
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(PlayedWidth));
    }

    private Chapter? CurrentChapter() =>
        _chapters.LastOrDefault(c => c.HasAudioRange && PositionMs >= c.StartMs)
        ?? _chapters.FirstOrDefault();

    // ---- Transport commands ----

    [RelayCommand]
    private async Task TogglePlayAsync()
    {
        // About to start: another device may have carried on with this book while it sat paused
        // here. Asked with a short fuse, since play is waiting on the answer.
        if (!playback.IsPlaying) await OfferPositionFromElsewhereAsync(TimeSpan.FromSeconds(2));

        playback.TogglePlayPause();
        IsPlaying = playback.IsPlaying;
    }

    [RelayCommand]
    private void Back() => playback.Nudge(-10_000);

    [RelayCommand]
    private void Forward() => playback.Nudge(10_000);

    /// <summary>Where each chapter begins, for the controls outside the app to step between.</summary>
    private long[] ChapterStarts() =>
        [.. _chapters.Where(c => c.HasAudioRange).Select(c => c.StartMs ?? 0).Order()];

    [RelayCommand]
    private void PreviousChapter()
    {
        var current = CurrentChapter();
        if (current is null) return;

        // Within the first few seconds, "previous" means the chapter before this one; later it
        // means the start of this one — the behaviour every music player has trained people on.
        var restartThresholdMs = 3_000;
        var target = PositionMs - (current.StartMs ?? 0) > restartThresholdMs
            ? current
            : _chapters.LastOrDefault(c => c.Index < current.Index) ?? current;

        // Restarting the chapter you are in is asked for by name; only moving to another resumes.
        if (target.Index == current.Index) playback.SeekTo(target.StartMs ?? 0);
        else GoToChapter(target);
    }

    [RelayCommand]
    private void NextChapter()
    {
        if (CurrentChapter() is not { } current) return;

        var next = _chapters.FirstOrDefault(c => c.Index > current.Index);

        if (next is null) playback.SeekTo(DurationMs);
        else GoToChapter(next);
    }

    /// <summary>
    /// How close to its end a chapter may be left and still count as finished — so it is started
    /// from the top next time rather than resumed for a closing line or two.
    /// </summary>
    private const long ChapterFinishedWithinMs = 20_000;

    /// <summary>
    /// Moves to another chapter: notes where the one being left had got to, and resumes the one
    /// being entered where it was last left, or at its start.
    /// </summary>
    private void GoToChapter(Chapter target)
    {
        if (CurrentChapter() is { StartMs: { } from } leaving && leaving.Index != target.Index)
        {
            var to = leaving.EndMs
                     ?? _chapters.FirstOrDefault(c => c.Index > leaving.Index)?.StartMs
                     ?? DurationMs;

            ChapterPositions.Leave(
                ChapterPositions.Audio, BookId, leaving.Index, PositionMs, from, to, ChapterFinishedWithinMs);
        }

        var resume = ChapterPositions.Enter(ChapterPositions.Audio, BookId, target.Index);
        playback.SeekTo(resume ?? target.StartMs ?? 0);
    }

    [RelayCommand]
    private void CycleSpeed()
    {
        var index = Array.FindIndex(Speeds, s => Math.Abs(s - playback.Speed) < 0.01f);
        var next = Speeds[(index + 1) % Speeds.Length];

        playback.SetSpeed(next);
        SpeedText = FormatSpeed(next);
    }

    /// <summary>Jumps to a chapter picked from the list.</summary>
    [RelayCommand]
    private void SelectChapter(ChapterRow? row)
    {
        if (row?.Chapter.StartMs is not { } start) return;

        // Picking the chapter already playing means its start; picking another resumes it.
        if (CurrentChapter()?.Index == row.Chapter.Index) playback.SeekTo(start);
        else GoToChapter(row.Chapter);

        // The list covers the player while it is open, and having picked a chapter the user wants
        // to see the thing they just moved.
        ChaptersExpanded = false;
    }

    /// <summary>Seeks within the current chapter, matching what the scrubber shows.</summary>
    [RelayCommand]
    private void SeekToFraction(double fraction)
    {
        var start = _currentChapter?.StartMs ?? 0;
        playback.SeekTo(start + (long)(Math.Clamp(fraction, 0, 1) * ChapterDurationMs));
    }

    // ---- Sleep timer ----

    /// <summary>
    /// Offers the sleep-timer choices in a system menu, the way every other player does it.
    ///
    /// A row of buttons for this was the wrong shape: it is a one-off choice from a longish list,
    /// and it was taking permanent space on the screen for something used once a night.
    /// </summary>
    [RelayCommand]
    private Task ChooseSleepAsync() => GuardAsync(async () =>
    {
        var minuteOptions = SleepMinutes
            .Select(m => string.Format(Strings.Player_SleepMinutes, m))
            .ToArray();

        string[] options = [.. minuteOptions, EndOfChapterChoice];

        var cancel = Strings.Common_Cancel;

        var choice = await Dialogs.ChooseAsync(
            Strings.Player_SleepAfterTitle,
            cancel,
            // Only offered while a timer is actually running.
            HasSleepTimer ? CancelSleepChoice : null,
            options);

        if (choice is null || choice == cancel) return;

        if (choice == CancelSleepChoice)
        {
            playback.CancelSleep();
            SleepText = "";
            return;
        }

        // Stops at the end of what is being listened to — the option that fits an audiobook, where
        // a fixed timer tends to cut off mid-scene.
        if (choice == EndOfChapterChoice)
        {
            if (_currentChapter?.EndMs is { } end) playback.SleepAtPosition(end);
            return;
        }

        // Matched by where it sits in the list rather than by reading the number back out of it,
        // so the wording of the option is free to change with the language.
        var picked = Array.IndexOf(minuteOptions, choice);
        if (picked >= 0) playback.SleepAfter(TimeSpan.FromMinutes(SleepMinutes[picked]));
    });

    // ---- Bookmarks ----

    /// <summary>
    /// Saves the spot being listened to. Reached by holding the flag, not tapping it.
    ///
    /// Tapping opens the list, because that is the thing you do often — placing a bookmark is
    /// deliberate and rare, and a control that silently writes a record on a stray tap gives no
    /// sign either way.
    /// </summary>
    [RelayCommand]
    private Task AddBookmarkAsync() => GuardAsync(async () =>
    {
        var existing = await database.GetBookmarksAsync(BookId);

        if (existing.Count >= BookmarksViewModel.Limit)
        {
            Error = string.Format(Strings.Bookmarks_LimitReachedBook, BookmarksViewModel.Limit);
            return;
        }

        await database.AddBookmarkAsync(new Bookmark
        {
            BookId = BookId,
            PositionMs = HasAudio ? PositionMs : null,
            ChapterIndex = CurrentChapter()?.Index ?? 0,
            CreatedUtc = DateTime.UtcNow,
        });

        // A saved bookmark that says nothing looks like a button that does nothing.
        BookmarkNote = HasAudio
            ? string.Format(Strings.Bookmarks_SavedAt, PositionText)
            : Strings.Bookmarks_Saved;
        await Task.Delay(2500);
        BookmarkNote = "";
    });

    /// <summary>Brief confirmation that a bookmark was placed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBookmarkNote))]
    public partial string BookmarkNote { get; set; } = "";

    public bool HasBookmarkNote => BookmarkNote.Length > 0;

    [RelayCommand]
    private Task OpenBookmarksAsync() => Shell.Current.GoToAsync($"bookmarks?id={BookId}");

    // ---- Media management ----

    /// <summary>What an import is doing, so a long copy does not look like a hang.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImporting))]
    public partial string ImportMessage { get; set; } = "";

    public bool IsImporting => ImportMessage.Length > 0;

    [ObservableProperty]
    public partial double ImportFraction { get; set; }

    [RelayCommand]
    private Task AddAudioAsync() => AttachAsync(
        picker.PickAudioAsync(Strings.Picker_AddAudiobook),
        (picked, progress, ct) => importer.ImportAudioAsync(picked, BookId, progress, ct));

    [RelayCommand]
    private Task AddEbookAsync() => AttachAsync(
        picker.PickEbookAsync(Strings.Picker_AddEbook),
        (picked, progress, ct) => importer.ImportEbookAsync(picked, BookId, progress, ct));

    private async Task AttachAsync(
        Task<PickedMedia?> pick,
        Func<PickedMedia, IProgress<ImportProgress>, CancellationToken, Task<Book>> attach)
    {
        Error = null;

        var picked = await pick;
        if (picked is null) return;

        var hadText = HasText;

        try
        {
            AppLog.Info($"attach starting: '{picked.FileName}' from '{picked.Location}'");

            ImportMessage = "Kopiram…";

            var progress = new Progress<ImportProgress>(p =>
            {
                ImportMessage = p.Message;
                ImportFraction = p.Fraction;
            });

            await attach(picked, progress, CancellationToken.None);

            // Gaining a second medium turns this into a read-along, and alignment wants the audio
            // as a local copy rather than as a reference into wherever the user keeps it.
            var book = await database.GetBookAsync(BookId);
            if (book?.IsPaired == true) await importer.EnsureLocalAudioAsync(BookId, progress, CancellationToken.None);

            var justGainedText = !hadText && book?.HasText == true;

            await LoadAsync();

            // A book that has just gained text is a different thing from the one that was open a
            // moment ago, and it belongs on the reading side. Landing back on the player with a
            // "Čitaj" button means pressing one more thing to reach what you added.
            if (justGainedText)
            {
                // To the library first, then into the reader: this is reached from the details page
                // stacked on top of a player, and both of those are now the wrong place to come
                // back to. Going absolute clears them instead of leaving them under the reader.
                await Shell.Current.GoToAsync("//library");
                await Shell.Current.GoToAsync($"reader?id={BookId}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"attach of '{picked.FileName}'", ex);
            Error = string.Format(Strings.Error_AddFailed, ex.Message);
        }
        finally
        {
            ImportMessage = "";
            ImportFraction = 0;
        }
    }

    /// <summary>Removes the book and everything belonging to it, then returns to the library.</summary>
    [RelayCommand]
    private Task DeleteBookAsync() => GuardAsync(async () =>
    {
        // Either file will do to ask about: the switch covers the book, and a book with only
        // one medium has only that one to offer.
        var file = await importer.OwnFileAsync(BookId, audio: true)
                   ?? await importer.OwnFileAsync(BookId, audio: false);

        if (await ConfirmDeletionAsync(
                Strings.Dialog_DeleteBookTitle,
                string.Format(Strings.Dialog_DeleteBookBody, Title),
                file) is not { } choice)
        {
            return;
        }

        await importer.DeleteBookAsync(BookId, choice.AlsoFile);

        // All the way to the library, not one page back. Deletion is reached from the details page,
        // which sits on top of a reader or a player still showing the book that no longer exists —
        // stepping back one page landed on exactly that.
        await Shell.Current.GoToAsync("//library");
    });

    /// <summary>
    /// Runs a command body, turning a failure into a message on screen instead of a crash.
    ///
    /// An unhandled exception out of an async command reaches the Android runtime and takes the
    /// whole process down — a brutal answer to something as ordinary as a file that will not open.
    /// </summary>
    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            Error = null;
            await action();
        }
        catch (Exception ex)
        {
            AppLog.Error("command", ex);
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private Task RemoveAudioAsync() => GuardAsync(async () =>
    {
        if (await ConfirmRemovalAsync(Strings.Delete_RemoveAudioTitle, audio: true) is not { } choice)
            return;

        await importer.RemoveAudioAsync(BookId, choice.AlsoFile);
        await LoadAsync();
    });

    [RelayCommand]
    private Task RemoveEbookAsync() => GuardAsync(async () =>
    {
        if (await ConfirmRemovalAsync(Strings.Delete_RemoveEbookTitle, audio: false) is not { } choice)
            return;

        await importer.RemoveEbookAsync(BookId, choice.AlsoFile);
        await LoadAsync();
    });

    /// <summary>
    /// Removing a medium is confirmed, and the confirmation carries a second question: whether the
    /// file itself should go as well.
    ///
    /// The message still says what survives, because the point of the feature is that the other
    /// half of the book does — but until now the answer to "and the file?" was decided for the
    /// user, and it was only ever one of the two things they might have meant.
    /// </summary>
    private async Task<DeleteChoice?> ConfirmRemovalAsync(string title, bool audio) =>
        await ConfirmDeletionAsync(title, Strings.Dialog_RemoveBody, await importer.OwnFileAsync(BookId, audio));

    /// <summary>
    /// Asks whether to delete, and how far the deletion should reach.
    ///
    /// Two shapes, because there are two questions or one. When the book has a file of the user's
    /// own there is a real second decision and it needs somewhere to live; when the only copy is
    /// the app's, that copy goes either way and there is nothing to decide — so it stays the plain
    /// alert it always was rather than a full page with one question on it and a screen of empty
    /// space underneath.
    /// </summary>
    public static async Task<DeleteChoice?> ConfirmDeletionAsync(string title, string body, string? file)
    {
        if (file is null)
        {
            var yes = await Dialogs.AskAsync(
                title, body, Strings.Common_Delete, Strings.Common_Cancel, destructive: true);

            return yes ? new DeleteChoice(AlsoFile: false) : null;
        }

        var page = new DeleteConfirmPage(title, body, file);

        await Shell.Current.Navigation.PushModalAsync(page);

        return await page.Answer;
    }

    // ---- Locate a page from a photo ----
    //
    // For a physical copy or a Kindle open on the same book: a photo of whatever page someone has
    // reached tells the reader where to open, without them hunting for the spot by hand. Entirely
    // on-device -- the OCR model is bundled rather than fetched from Play Services, and finding
    // the page is the same phrase matching alignment already does against a spoken probe, run
    // here against the photo's text instead. Only offered once the book has an ebook to search.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLocatePage))]
    public partial bool IsLocatingPage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPageLocatorNote))]
    public partial string PageLocatorNote { get; set; } = "";

    public bool HasPageLocatorNote => PageLocatorNote.Length > 0;

    public bool CanLocatePage => HasText && !IsLocatingPage;

    /// <summary>
    /// The camera was refused, and only the system's settings can undo that — once declined, neither
    /// Android nor iOS will ask again, so the way there is offered right under the explanation.
    /// </summary>
    [ObservableProperty]
    public partial bool CameraRefused { get; set; }

    [RelayCommand]
    private static void OpenAppSettings()
    {
        try
        {
            AppInfo.Current.ShowSettingsUI();
        }
        catch (Exception ex)
        {
            AppLog.Error("opening the app's settings", ex);
        }
    }

    [RelayCommand]
    private async Task LocatePageAsync()
    {
        if (_book is not { EbookPath: { } ebookPath }) return;

        var source = new PhotoSourcePage();
        await Shell.Current.Navigation.PushModalAsync(source);

        if (await source.Answer is not { } chosen) return;

        byte[]? imageData;
        try
        {
            // Two different pickers for two different questions. The camera goes through
            // MediaPicker, which is what the camera permission is declared for. An existing
            // photograph goes through the system photo picker instead of MediaPicker's gallery:
            // that one insists on a storage permission this app deliberately does not declare (see
            // the manifest) and so threw before anything opened, while the photo picker shows the
            // gallery, with thumbnails, and asks for nothing.
            imageData = chosen is PhotoSource.Camera
                ? await CapturedPhotoAsync()
                : await photos.PickFromGalleryAsync();
        }
        catch (PermissionException ex)
        {
            AppLog.Error($"permission for the {chosen} photo source", ex);
            PageLocatorNote = Strings.Details_LocatePageNoPermission;
            CameraRefused = true;
            return;
        }
        catch (FeatureNotSupportedException ex)
        {
            // No camera on this device — choosing an existing photo still works, so this is only
            // reachable by picking the one source that cannot.
            AppLog.Error($"the {chosen} photo source is unavailable", ex);
            PageLocatorNote = Strings.Details_LocatePageNoPermission;
            return;
        }

        if (imageData is null)
        {
            AppLog.Info($"page photo: nothing came back from {chosen}");
            return;
        }

        IsLocatingPage = true;
        PageLocatorNote = "";
        CameraRefused = false;

        try
        {
            await ocr.InitAsync();
            var recognized = await ocr.RecognizeTextAsync(imageData, tryHard: true);

            // Logged because the ways this fails are invisible on screen and tell very different
            // stories: a photograph that read as nothing, one that read plenty but matched
            // nothing, and one that matched all end up as a single line to the user. The word
            // count is what separates them. Deliberately counts only — what was read is the
            // book's own text, and a diagnostic is no reason to copy it into a file that gets
            // sent along with a bug report.
            var words = TextNormalizer.NormalizeTranscript(recognized.AllText ?? "");

            AppLog.Info(
                $"page photo: {chosen}, {imageData.Length} bytes, ocr={recognized.Success}, " +
                $"{recognized.AllText?.Length ?? 0} chars, {words.Count} words");

            if (!recognized.Success || string.IsNullOrWhiteSpace(recognized.AllText))
            {
                PageLocatorNote = Strings.Details_LocatePageNoText;
                return;
            }

            // Off the UI thread, all three steps of it. Searching the whole book is a
            // Smith-Waterman over every word in it against every word read off the photo: measured
            // at 26 million cells and close to two seconds for an 800,000-character novel on a
            // desktop, so several times that on a phone. Left on the main thread it froze the app
            // — spinner included — for long enough to be an ANR rather than a wait. Parsing the
            // ebook, which happens here whenever the reader has not already cached it, is the same
            // story on a smaller scale.
            // The size of what was searched comes back with the answer rather than being asked for
            // separately: a failure is worth being able to tell apart from a book that extracted
            // to almost nothing, and running the search twice to learn that would double the wait
            // on exactly the attempt that already disappointed someone.
            var (location, chars, tokens) = await Task.Run(async () =>
            {
                var extracted = await extractors.ExtractAsync(ebookPath);
                var tokenized = TokenizedText.Create(extracted.Text.PlainText);

                return (PageLocator.Locate(tokenized, recognized.AllText),
                    extracted.Text.PlainText.Length, tokenized.Count);
            });

            AppLog.Info(location is { } found
                ? $"page photo: matched at char {found.CharOffset}, confidence {found.Confidence:0.00}"
                : $"page photo: no match for {words.Count} words in {chars} chars / {tokens} tokens");

            if (location is null)
            {
                PageLocatorNote = Strings.Details_LocatePageNotFound;
                return;
            }

            // To the library first, then into the reader. This page can itself be sitting on top of
            // a reader — it is reached from one as readily as from the player — and pushing a
            // second copy would leave two of them stacked, each holding the whole book, with the
            // newer one's close button landing back here instead of leaving the book. The same
            // reset is why opening a bookmark behaves itself (see BookmarksViewModel.OpenAsync).
            await Shell.Current.GoToAsync("//library");
            await Shell.Current.GoToAsync($"reader?id={BookId}&offset={location.Value.CharOffset}");
        }
        catch (Exception ex)
        {
            AppLog.Error("locating a page from a photo", ex);
            PageLocatorNote = Strings.Details_LocatePageError;
        }
        finally
        {
            IsLocatingPage = false;
        }
    }

    /// <summary>Takes a photograph and hands back its bytes, or null when nobody took one.</summary>
    private static async Task<byte[]?> CapturedPhotoAsync()
    {
        if (await MediaPicker.Default.CapturePhotoAsync() is not { } photo) return null;

        await using var stream = await photo.OpenReadAsync();
        using var buffer = new MemoryStream();

        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    // ---- Alignment ----

    [RelayCommand]
    private Task StartAlignmentAsync() => GuardAsync(async () =>
    {
        // Asked for before starting, because from Android 13 the progress notification simply does
        // not appear without it — and a long job running with no notification is a job the user
        // cannot see, pause, or trust.
        var permission = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
        if (permission != PermissionStatus.Granted)
            await Permissions.RequestAsync<Permissions.PostNotifications>();

        alignment.Start(BookId);
    });

    [RelayCommand]
    private void StopAlignment() => alignment.Stop();

    // ---- Sending the alignment to another device ----

    /// <summary>Whether there is an alignment here worth sending: something measured, of a paired book.</summary>
    public bool CanShareAlignment => IsPaired && !IsAligning && (AlignedChapterCount > 0 || MeasuredMs > 0);

    /// <summary>How the last send went, or what it is doing now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShareStatus))]
    public partial string ShareStatus { get; set; } = "";

    public bool HasShareStatus => ShareStatus.Length > 0;

    /// <summary>
    /// Sends this book's alignment to another device on the network — a television that cannot
    /// align for itself, say — which takes it only for a book made of the same files, and only once
    /// someone there agrees.
    /// </summary>
    [RelayCommand]
    private async Task ShareAlignmentAsync()
    {
        try
        {
            ShareStatus = Strings.Share_Looking;

            var devices = await AlignmentShare.FindAsync(TimeSpan.FromSeconds(2));
            var labels = devices.Select(d => $"{d.Name} ({d.Address})").ToList();

            var choice = await Dialogs.ChooseAsync(
                Strings.Share_ChooseTitle, Strings.Common_Cancel, null,
                [.. labels, Strings.Share_ScanQr, Strings.Share_TypeAddress, Strings.Share_AsFile]);

            IPAddress? to = null;
            var name = "";

            if (choice == Strings.Share_ScanQr)
            {
                await SendByQrAsync();
                return;
            }

            if (choice == Strings.Share_AsFile)
            {
                ShareStatus = "";
                await SendAsFileAsync();
                return;
            }

            if (choice == Strings.Share_TypeAddress)
            {
                var typed = await Dialogs.PromptAsync(
                    Strings.Share_AddressTitle, Strings.Share_AddressBody, Strings.Share_Send, Strings.Common_Cancel, "192.168.1.");

                if (typed is not null && IPAddress.TryParse(typed.Trim(), out var parsed))
                {
                    to = parsed;
                    name = parsed.ToString();
                }
            }
            else if (labels.IndexOf(choice) is var index and >= 0)
            {
                to = devices[index].Address;
                name = devices[index].Name;
            }

            if (to is null)
            {
                ShareStatus = "";
                return;
            }

            ShareStatus = string.Format(Strings.Share_Sending, name);

            var reply = await share.SendAsync(BookId, to);

            ShareStatus = reply.Accepted
                ? string.Format(Strings.Share_Done, name)
                : reply.Reason switch
                {
                    AlignmentTransfer.NoSuchBook => string.Format(Strings.Share_NoSuchBook, name),
                    AlignmentTransfer.Declined => string.Format(Strings.Share_Declined, name),
                    _ => Strings.Share_Unreachable,
                };
        }
        catch (Exception ex)
        {
            AppLog.Error("sending the alignment to another device", ex);
            ShareStatus = ex.Message;
        }
    }

    /// <summary>
    /// Shows a QR code for this book, for the device that has its alignment to scan. What arrives
    /// with it is taken for this book without a question; anything for another book is turned away.
    /// </summary>
    [RelayCommand]
    private async Task ReceiveAlignmentAsync()
    {
        ShareStatus = "";
        await Shell.Current.Navigation.PushModalAsync(new Views.ReceiveAlignmentPage(share, BookId, Title));
    }

    /// <summary>Takes an alignment someone sent as a file, for this book.</summary>
    [RelayCommand]
    private async Task ImportAlignmentFileAsync()
    {
        try
        {
            if (await picker.PickFileAsync(Strings.Share_PickFile) is not { } picked) return;

            await using (var file = await Task.Run(() => references.OpenRead(picked.Location)))
                ShareStatus = await share.ImportAsync(file);

            await LoadAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("importing an alignment file", ex);
            ShareStatus = ex.Message;
        }
    }

    /// <summary>
    /// Sends to the device showing a QR code: a photo of the code says where it is and carries the
    /// key that lets the alignment in without a question on the other side.
    /// </summary>
    private async Task SendByQrAsync()
    {
        byte[]? photo;

        try
        {
            photo = await CapturedPhotoAsync();
        }
        catch (PermissionException)
        {
            ShareStatus = Strings.Details_LocatePageNoPermission;
            return;
        }

        if (photo is null)
        {
            ShareStatus = "";
            return;
        }

        ShareStatus = Strings.Share_ReadingQr;

        var code = PairingCode.Parse(await Task.Run(() => QrReader.Read(photo)));
        if (code is null)
        {
            ShareStatus = Strings.Share_QrNotFound;
            return;
        }

        var name = code.Name.Length > 0 ? code.Name : code.Addresses[0].ToString();
        ShareStatus = string.Format(Strings.Share_Sending, name);

        var reply = await share.SendAsync(BookId, code);

        ShareStatus = reply.Accepted
            ? string.Format(Strings.Share_Done, name)
            : reply.Reason switch
            {
                AlignmentTransfer.NoSuchBook => string.Format(Strings.Share_NoSuchBook, name),
                AlignmentTransfer.Declined => string.Format(Strings.Share_Declined, name),
                _ => Strings.Share_Unreachable,
            };
    }

    /// <summary>
    /// Hands the alignment to the share sheet as a file: AirDrop, Quick Share, a chat, e-mail —
    /// whatever reaches the other device, on any network or none in common.
    /// </summary>
    private async Task SendAsFileAsync()
    {
        var path = await share.ExportAsync(BookId);

        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = Strings.Share_AsFile,
            File = new ShareFile(path, "application/octet-stream"),
        });
    }

    /// <summary>
    /// Discards everything aligned so far and starts again.
    ///
    /// Offered because resuming can only fill gaps, never correct what a run already decided —
    /// and an early chapter aligned from a poor estimate stays wrong forever otherwise.
    /// </summary>
    [RelayCommand]
    private Task ClearAlignmentAsync() => GuardAsync(async () =>
    {
        var confirmed = await Dialogs.AskAsync(
            Strings.Dialog_ClearTitle,
            string.Format(Strings.Dialog_ClearBody, AlignedChapterCount, Chapters.Count),
            Strings.Common_Delete,
            Strings.Common_Cancel,
            destructive: true);

        if (!confirmed) return;

        // Stopped before the file is touched, and waited for — both kinds of run, because the
        // button is now reachable while either is active.
        //
        // Sync on the fly holds the map in memory and writes it out every couple of windows; the
        // background align-ahead run checkpoints every twenty probes. Either one deleting the file
        // out from under it only meant the next save put everything back moments later, which is
        // exactly what "I deleted it and a third of chapter one was still there" looks like.
        await liveSync.StopAsync();
        await alignment.StopAndWaitAsync();

        await library.ResetAlignmentAsync(BookId);
        await RefreshAlignmentProgressAsync();

        // Nothing is started afterwards, deliberately.
        //
        // This used to clear and then immediately begin again, which made it impossible to simply
        // throw a bad alignment away: the only way to get rid of one was to start another. Deleting
        // and aligning are two decisions, and the page has a button for each.
    });

    private void OnAlignmentChanged(object? sender, AlignmentStatus status) =>
        MainThread.BeginInvokeOnMainThread(() => ApplyAlignmentStatus(status));

    private void ApplyAlignmentStatus(AlignmentStatus status)
    {
        if (status.BookId is not null && status.BookId != BookId) return;

        var wasRunning = IsAligning;

        AlignmentMessage = status.Message;
        AlignmentFraction = status.Fraction;
        ChapterFraction = status.ChapterFraction;
        IsAligning = status.IsRunning;

        // A run that just ended moved the high-water mark, so the summary has to be re-read or it
        // would keep claiming whatever was true when the page opened.
        if (wasRunning && !IsAligning) _ = RefreshAlignmentProgressAsync();
    }

    /// <summary>Set when the stored map came from an older method and will be recomputed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlignmentSummary))]
    public partial bool HasStaleAlignment { get; set; }

    private async Task RefreshAlignmentProgressAsync()
    {
        if (await database.GetBookAsync(BookId) is { } book) _book = book;

        var map = await syncMaps.LoadAsync(BookId);
        var boundary = new AlignmentSettings().BoundaryConfidence;

        // A map the aligner will throw away must not be counted as progress. Quoting its chapter
        // count would promise following that the reader will not get, which is the same lie as
        // quoting a high-water mark the aligner does not use.
        HasStaleAlignment = map is { IsStale: true };

        AlignedChapterCount = HasStaleAlignment ? 0 : map?.MeasuredChapterCount(boundary) ?? 0;

        // The window the live run uses, which is what decides whether two anchors bracket audio
        // that was heard or a gap that was interpolated across.
        MeasuredMs = HasStaleAlignment
            ? 0
            : map?.MeasuredMs(AlignmentSettings.Refinement.ProbeDurationMs, boundary) ?? 0;
        ResumeFromChapter = HasStaleAlignment ? 0 : map?.FirstChapterNeedingWork(Chapters.Count, boundary) ?? 0;

        OnPropertyChanged(nameof(AlignmentSummary));
        OnPropertyChanged(nameof(StartAlignmentText));
    }

    // ---- Reader ----

    /// <summary>
    /// Goes to the text.
    ///
    /// Back rather than forward when the reader is the page underneath, which is the usual case now
    /// that a paired book opens there: pushing a second copy would leave two readers on the stack,
    /// each with its own web view holding the whole book.
    /// </summary>
    [RelayCommand]
    private Task OpenReaderAsync() =>
        Shell.Current.Navigation.NavigationStack is [.., Views.ReaderPage, _]
            ? Shell.Current.GoToAsync("..")
            : Shell.Current.GoToAsync($"reader?id={BookId}");

    public void Dispose()
    {
        _ticker?.Stop();
        alignment.Changed -= OnAlignmentChanged;
    }

    private static string Format(long ms)
    {
        var span = TimeSpan.FromMilliseconds(ms);
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }

    private static string FormatSpeed(float speed) =>
        speed == 1f ? "1×" : $"{speed:0.##}×".Replace(",", ".");
}
