using System.Collections.ObjectModel;
using AudioBookReader.App.Services;
using AudioBookReader.Core.Alignment;
using AudioBookReader.Core.Books;
using AudioBookReader.Core.Data;
using AudioBookReader.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioBookReader.App.ViewModels;

[QueryProperty(nameof(BookId), "id")]
public partial class BookViewModel(
    LibraryDatabase database,
    LibraryService library,
    SyncMapStore syncMaps,
    BookImporter importer,
    BookFilePicker picker,
    PlaybackController playback,
    AlignmentQueue alignment) : ObservableObject, IDisposable
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
    public partial bool HasText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartAlignment))]
    [NotifyPropertyChangedFor(nameof(CanRealign))]
    public partial bool IsPaired { get; set; }

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
    public string PlayGlyph => IsPlaying ? "❚❚" : "▶";

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
    public string SleepButtonText => HasSleepTimer ? $"⏱ {SleepText}" : "⏱ Zaustavi nakon";

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartAlignment))]
    [NotifyPropertyChangedFor(nameof(IsIdleWithMessage))]
    [NotifyPropertyChangedFor(nameof(CanRealign))]
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
    [NotifyPropertyChangedFor(nameof(CanRealign))]
    [NotifyPropertyChangedFor(nameof(AlignmentSummary))]
    public partial bool MeasuresWhileReading { get; set; }

    [ObservableProperty]
    public partial bool AlignsInAdvance { get; set; } = true;

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
        }
        finally
        {
            _switchingMode = false;
        }
    }

    public bool CanStartAlignment => IsPaired && !IsAligning && !MeasuresWhileReading;

    /// <summary>Chapters holding real anchors — not chapters merely visited by a run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlignmentSummary))]
    [NotifyPropertyChangedFor(nameof(StartAlignmentText))]
    [NotifyPropertyChangedFor(nameof(CanRealign))]
    public partial int AlignedChapterCount { get; set; }

    /// <summary>Only worth offering once there is something to discard.</summary>
    public bool CanRealign => IsPaired && !IsAligning && !MeasuresWhileReading && AlignedChapterCount > 0;

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
                return "Sync on the fly: tekst se mjeri dok čitaš — otvori „Čitaj” i pusti zvuk. " +
                       "Poravnanje unaprijed nije potrebno.";

            if (HasStaleAlignment)
                return "Ranije poravnanje napravljeno je starijom, manje preciznom metodom i bit će " +
                       "izračunato ispočetka.";

            if (AlignedChapterCount == 0) return "Još nije poravnano.";

            if (AlignedChapterCount >= Chapters.Count)
                return $"Poravnano u cijelosti ({Chapters.Count} poglavlja).";

            return $"Poravnano {AlignedChapterCount} od {Chapters.Count} poglavlja. " +
                   $"Nastavak kreće od {ResumeFromChapter + 1}.";
        }
    }

    public string StartAlignmentText =>
        AlignedChapterCount > 0 && AlignedChapterCount < Chapters.Count
            ? "Nastavi poravnanje"
            : "Pokreni poravnanje";

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

    public string ChapterToggleText => $"Poglavlja ({Chapters.Count})  {(ChaptersExpanded ? "▲" : "▼")}";

    [RelayCommand]
    private void ToggleChapters() => ChaptersExpanded = !ChaptersExpanded;

    /// <summary>
    /// Whether the housekeeping panel is showing over the player.
    ///
    /// Importing files, alignment and deletion are things you do to a book once; playing it is what
    /// you do every day. Keeping them on the same scroll made the second one look like the first,
    /// so the rare half now lives behind one button.
    /// </summary>
    [ObservableProperty]
    public partial bool IsDetailsOpen { get; set; }

    [RelayCommand]
    private void ToggleDetails() => IsDetailsOpen = !IsDetailsOpen;

    [RelayCommand]
    private Task CloseAsync() => Shell.Current.GoToAsync("..");

    private static readonly int[] SleepMinutes = [5, 10, 15, 20, 30, 45, 60, 90];

    private const string EndOfChapterChoice = "Do kraja poglavlja";
    private const string CancelSleepChoice = "Isključi";

    // ---- Lifecycle ----

    public async Task LoadAsync()
    {
        _book = await database.GetBookAsync(BookId);
        if (_book is null) return;

        Title = _book.Title;
        Author = _book.Author ?? "";
        CoverPath = _book.CoverPath;
        HasAudio = _book.HasAudio;
        HasText = _book.HasText;
        IsPaired = _book.IsPaired;

        _chapters = await database.GetChaptersAsync(BookId);

        Chapters.Clear();
        foreach (var chapter in _chapters)
        {
            Chapters.Add(new ChapterRow(
                chapter,
                string.IsNullOrWhiteSpace(chapter.Title) ? $"Poglavlje {chapter.Index + 1}" : chapter.Title,
                chapter.StartMs is { } startMs ? Format(startMs) : ""));
        }

        HasChapters = Chapters.Count > 0;
        OnPropertyChanged(nameof(ChapterToggleText));

        // Read back without letting the toggles write it straight out again.
        _switchingMode = true;
        MeasuresWhileReading = _book.MeasureWhileReading;
        AlignsInAdvance = !_book.MeasureWhileReading;
        _switchingMode = false;

        OnPropertyChanged(nameof(CanStartAlignment));
        OnPropertyChanged(nameof(CanRealign));

        await RefreshAlignmentProgressAsync();

        // LoadAsync is reached from the page appearing and from every attach or removal, so the
        // handler is dropped before it is added; otherwise each one orphans a subscription on a
        // singleton that then does database work for a page that no longer exists.
        alignment.Changed -= OnAlignmentChanged;
        alignment.Changed += OnAlignmentChanged;
        ApplyAlignmentStatus(alignment.Status);

        if (HasAudio) await StartPlaybackAsync();

        StartTicking();
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
            return;
        }

        var state = await database.GetReadingStateAsync(BookId);
        var startMs = state?.AudioPositionMs ?? 0;
        var speed = state?.Speed ?? 1f;

        AppLog.Info($"loading book {BookId} at {startMs} ms, speed {speed}");

        await playback.LoadAsync(BookId, _book!.AudioPath!, startMs, speed);

        PositionMs = startMs;
        DurationMs = _book.DurationMs;
        SpeedText = FormatSpeed(speed);
        UpdateChapterTitle();
    }

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

    public Task SavePositionAsync() =>
        HasAudio
            ? database.SaveReadingStateAsync(BookId, PositionMs, speed: playback.Speed)
            : Task.CompletedTask;

    /// <summary>
    /// Keeps the chapter-derived state in step with playback: the heading, the chapter-relative
    /// scrubber, and which row in the list is lit up.
    /// </summary>
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
    private void TogglePlay()
    {
        playback.TogglePlayPause();
        IsPlaying = playback.IsPlaying;
    }

    [RelayCommand]
    private void Back() => playback.Nudge(-10_000);

    [RelayCommand]
    private void Forward() => playback.Nudge(10_000);

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

        playback.SeekTo(target.StartMs ?? 0);
    }

    [RelayCommand]
    private void NextChapter()
    {
        if (CurrentChapter() is not { } current) return;

        var next = _chapters.FirstOrDefault(c => c.Index > current.Index);
        playback.SeekTo(next?.StartMs ?? DurationMs);
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

        playback.SeekTo(start);

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
        string[] options = [.. SleepMinutes.Select(m => $"{m} min"), EndOfChapterChoice];

        var choice = await Shell.Current.DisplayActionSheetAsync(
            "Zaustavi nakon",
            "Odustani",
            // Only offered while a timer is actually running.
            HasSleepTimer ? CancelSleepChoice : null,
            options);

        switch (choice)
        {
            case null or "Odustani":
                return;

            case CancelSleepChoice:
                playback.CancelSleep();
                SleepText = "";
                return;

            // Stops at the end of what is being listened to — the option that fits an audiobook,
            // where a fixed timer tends to cut off mid-scene.
            case EndOfChapterChoice:
                if (_currentChapter?.EndMs is { } end) playback.SleepAtPosition(end);
                return;

            default:
                if (int.TryParse(choice.Split(' ')[0], out var minutes))
                    playback.SleepAfter(TimeSpan.FromMinutes(minutes));
                return;
        }
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
            Error = $"Dosegnut je limit od {BookmarksViewModel.Limit} bookmarka za ovu knjigu.";
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
        BookmarkNote = HasAudio ? $"Spremljeno na {PositionText}" : "Spremljeno";
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
        picker.PickAudioAsync("Dodaj audioknjigu"),
        (picked, progress, ct) => importer.ImportAudioAsync(picked, BookId, progress, ct));

    [RelayCommand]
    private Task AddEbookAsync() => AttachAsync(
        picker.PickEbookAsync("Dodaj e-knjigu"),
        (picked, progress, ct) => importer.ImportEbookAsync(picked, BookId, progress, ct));

    private async Task AttachAsync(
        Task<PickedMedia?> pick,
        Func<PickedMedia, IProgress<ImportProgress>, CancellationToken, Task<Book>> attach)
    {
        Error = null;

        var picked = await pick;
        if (picked is null) return;

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

            await LoadAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error($"attach of '{picked.FileName}'", ex);
            Error = $"Dodavanje nije uspjelo: {ex.Message}";
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
        var confirmed = await Shell.Current.DisplayAlertAsync(
            "Obrisati knjigu?",
            $"„{Title}” i njene datoteke bit će trajno obrisane.",
            "Obriši",
            "Odustani");

        if (!confirmed) return;

        await importer.DeleteBookAsync(BookId);
        await Shell.Current.GoToAsync("..");
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
        if (!await ConfirmRemovalAsync("audioknjigu")) return;

        await importer.RemoveAudioAsync(BookId);
        await LoadAsync();
    });

    [RelayCommand]
    private Task RemoveEbookAsync() => GuardAsync(async () =>
    {
        if (!await ConfirmRemovalAsync("e-knjigu")) return;

        await importer.RemoveEbookAsync(BookId);
        await LoadAsync();
    });

    /// <summary>
    /// Removing a medium throws away its file, so it is confirmed. The message says what survives,
    /// because the point of the feature is that the other half of the book does.
    /// </summary>
    private static Task<bool> ConfirmRemovalAsync(string what) =>
        Shell.Current.DisplayAlertAsync(
            $"Ukloniti {what}?",
            $"Ostatak knjige ostaje. Vratiš li kasnije istu datoteku, poravnanje se neće ponavljati.",
            "Ukloni",
            "Odustani");

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

    /// <summary>
    /// Discards everything aligned so far and starts again.
    ///
    /// Offered because resuming can only fill gaps, never correct what a run already decided —
    /// and an early chapter aligned from a poor estimate stays wrong forever otherwise.
    /// </summary>
    [RelayCommand]
    private Task RealignAsync() => GuardAsync(async () =>
    {
        var confirmed = await Shell.Current.DisplayAlertAsync(
            "Poravnati iznova?",
            $"Dosadašnje poravnanje ({AlignedChapterCount} od {Chapters.Count} poglavlja) bit će obrisano i " +
            "izračunato ispočetka. Sama knjiga i tvoja pozicija ostaju.",
            "Poravnaj iznova",
            "Odustani");

        if (!confirmed) return;

        await library.ResetAlignmentAsync(BookId);
        await RefreshAlignmentProgressAsync();

        await StartAlignmentAsync();
    });

    private void OnAlignmentChanged(object? sender, AlignmentStatus status) =>
        MainThread.BeginInvokeOnMainThread(() => ApplyAlignmentStatus(status));

    private void ApplyAlignmentStatus(AlignmentStatus status)
    {
        if (status.BookId is not null && status.BookId != BookId) return;

        var wasRunning = IsAligning;

        AlignmentMessage = status.Message;
        AlignmentFraction = status.Fraction;
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
        ResumeFromChapter = HasStaleAlignment ? 0 : map?.FirstChapterNeedingWork(Chapters.Count, boundary) ?? 0;

        OnPropertyChanged(nameof(AlignmentSummary));
        OnPropertyChanged(nameof(StartAlignmentText));
    }

    // ---- Reader ----

    [RelayCommand]
    private Task OpenReaderAsync() => Shell.Current.GoToAsync($"reader?id={BookId}");

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
