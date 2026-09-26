# Syncbook / Čitko — building an audiobook + ebook read-along app with Claude Code

> An audiobook player that also reads the ebook, and — when you give it both halves of the same
> book — lines the text up with the narration on the phone itself, so the sentence being spoken is
> highlighted as you listen. Android, Android TV and iOS, from one C# codebase.
>
> Built in four weeks (30 Aug – 26 Sep 2026) by one person describing what he wanted, in Croatian,
> and [Claude Code](https://claude.com/claude-code) writing, testing, deploying and debugging the
> code. This document is the whole story: what was built, how, and every bug that was reported
> along the way.

| | |
|---|---|
| Commits | 125+, over 17 active days |
| Code | ~36,000 lines of C#/XAML in 207 files (Core 7.3k, App 20.6k, tests 5.9k, tools 1.8k) |
| Tests | 335 automated tests (xUnit), all green |
| Platforms | Android phone, Android Auto, Android TV (incl. 32-bit ARM), iOS/iPadOS |
| Languages | English and Croatian UI; alignment works for any language Whisper knows |

---

## 1. The idea

No mainstream player does what Amazon's Whispersync does for Kindle + Audible: follow the text
while the narrator reads. The reason is that it needs **forced alignment** — working out, for
every sentence of the ebook, when it is spoken in the audio — and ready-made solutions exist only
for the rare EPUB 3 books that ship with it built in.

The requirements, as set at the start:

- a real audiobook player first (chapters, speed, sleep timer, bookmarks, lock screen, headset
  buttons) — useful on its own even if nothing else works;
- a real ebook reader second (EPUB, TXT, later PDF and EPUB 3 read-alongs);
- and when both halves of one book are present, **align them on the device**, with no server and
  no PC step, without cooking the phone.

That last constraint shaped everything: the person had tried other on-device transcription apps
and his complaint was that they peg the CPU until the phone is hot.

## 2. How it was built

The workflow was deliberately simple:

1. The user describes a feature or a bug in plain Croatian, often with a phone screenshot.
2. Claude Code reads the relevant code, proposes a plan for anything non-trivial, and writes the
   change.
3. It builds for Android and iOS, runs the test suite, installs on the real device over `adb`
   (USB or Wi-Fi), reads the app's own log file off the phone, and reports back — in Croatian.
4. The user tries it and says what is still wrong.

Things Claude Code did directly, beyond writing code:

- installed the Android SDK from the command line (no Visual Studio installer);
- drove `adb` against a Galaxy S22, a second phone, and a Sony Android TV over the network;
- pulled SQLite databases and sync maps off the phone with `run-as` to diagnose bugs from real data;
- set up a Codemagic CI pipeline so iOS builds happen without owning a Mac, and an unsigned
  `.ipa` workflow for sideloading with a free Apple ID;
- researched the Play Console review questions, Android TV requirements, CarPlay entitlements,
  the Audiobookshelf API and state-of-the-art forced alignment (with sources);
- wrote a design plan before each big phase and kept it updated with what was *measured*, not
  assumed.

What the human did: decided what to build, tested on real devices every day, and reported what
felt wrong. None of the bugs below were found by reading code alone — most came from "I tried it
and this happened".

## 3. Architecture

```
src/
  AudioBookReader.Core/    net10.0 — models, EPUB/TXT/PDF parsing, the alignment engine,
                           sync maps, SQLite library, Audiobookshelf client. No MAUI dependency.
  AudioBookReader.App/     .NET MAUI — UI, Media3 (Android) / AVPlayer (iOS) playback,
                           hardware audio decoding, whisper.cpp, platform services.
  AudioBookReader.Tuner/   desktop console harness for developing the aligner on a PC.
tests/
  AudioBookReader.Core.Tests/  xUnit, 335 tests.
tools/
  Epub3Maker/              PC tool: ebook + audiobook → EPUB 3 with Media Overlays.
```

Decisions that paid off:

- **Core has no UI dependency.** The whole alignment pipeline can be tested in seconds on a PC
  with a synthetic narrator, instead of minutes per deploy cycle. It also made the iOS port and the
  desktop EPUB 3 tool nearly free: both just reference Core.
- **Platform code only through `partial` classes** (`Services/X.cs` declares, `Platforms/Android/X.Android.cs`
  and `Platforms/iOS/X.iOS.cs` implement). No `#if ANDROID` anywhere, so adding iOS was additive.
- **Pairing is optional and reversible.** A book can be audio-only, text-only or both; either half
  can be added, swapped or removed at any time. The rules for what that does to chapters and to the
  alignment live in one service (`LibraryService`), not in the screens.
- **The alignment map is keyed by content hashes** of the two files (SHA-256 of the length plus
  three 1 MB samples), so it survives moves and re-imports, refuses the wrong edition, and can be
  sent to another device.

## 4. The alignment engine

The key insight: **we already have the exact text.** Speech recognition isn't needed to find out
*what* is said — only *when*. So the book is never transcribed in full.

1. **Decode** the audio with the phone's hardware decoder (Android `MediaCodec`, iOS
   `AVAssetReader`) to 16 kHz mono, streamed — ten hours of float PCM would be 2.3 GB.
2. **Probe**: every 60 s, listen to 10 s with Whisper `tiny` (or `base`), greedy decoding, language
   named from the text instead of auto-detected.
3. **Match** each recognised phrase into the book text with a windowed Smith-Waterman search around
   where interpolation says it should be; each phrase gives two anchors (its start and end).
4. **Keep only consistent anchors** — a confidence-weighted longest increasing subsequence throws
   out the occasional wild match.
5. **Interpolate** between anchors. Narration pace is constant to ±5–12 %, so the error between
   anchors is small.

Measured, not guessed:

- Dating an anchor by *when the words were spoken* (from Whisper's timestamps) instead of when the
  probe started removed a one-directional bias of up to +64 characters. Two anchors per probe halved
  the median error. Neither cost a second of extra recognition.
- Denser probing was measured twice and **does not** improve the tail error (p95 stuck at ~23
  characters from 60 s spacing down). The remaining error was traced to Whisper's segment
  timestamps being spread evenly across words — so word-level token timestamps were switched on.

### Keeping the phone cool

- Worker threads at background priority, which Android pins to the efficiency cores.
- Duty cycling (work 2 s, rest 2 s) so the SoC never reaches thermal throttling.
- A thermal listener that halves the duty cycle at "moderate" and pauses at "severe".
- Presets (Thrifty / Balanced / Fast) plus "only while charging", "only with the screen off" and
  "only above X % battery".

### Sync on the fly

Instead of aligning the whole book up front, "align while reading" transcribes contiguous 15 s
windows just ahead of the playback position while the reader is open. There is nothing to
interpolate, so the highlight is exact where you are. This became the default for new books.

### Desktop: letter-level forced alignment

For maximum precision there is now a PC tool (`tools/Epub3Maker`) that uses Meta's MMS
forced-alignment model (wav2vec2, 1130 languages) through ONNX Runtime. It reads the whole book
into per-letter probabilities every 20 ms, finds unique 10-letter runs shared by a rough greedy
transcript and the book to anchor the two, and then runs CTC Viterbi alignment between anchors.
Against ground-truth word times from Windows TTS, sentence starts landed within **20–80 ms**. The
output is a standard EPUB 3 with Media Overlays, which the app imports as an already-aligned book.

## 5. Features

**Player** — chapters from m4b markers, speed, ±10 s, sleep timer and end-of-chapter (in the
service, so they work with the screen off), fade out, bookmarks, lock screen and headset controls,
audio focus, Android Auto browsing.

**Reader** — sentence highlight, auto-scroll, double-tap a sentence to seek the audio there,
long-press a word to translate or explain it (with a drag-to-select loupe), themes, fonts.

**Library** — import by reference (a 500 MB audiobook is not copied unless alignment needs it),
match an ebook to its audiobook by file name, covers from either file, PDF support, EPUB 3
read-along import.

**Audiobookshelf** — sign in (with rotating refresh tokens), browse shelves, download into the
user's own folder laid out by author/title, **stream without downloading** (bounded HTTP range
reads), and **share listening and reading progress** — the app asks "continue from where you were
on the other device?" when another device is actually ahead.

**Find a page from a photo** — photograph a page of the paper book or a Kindle, OCR it on-device,
and jump to that place in the ebook and audio.

**Android TV** — leanback launcher banner, full D-pad focus navigation, 32-bit ARM build, import
from USB through a file manager when the TV has no document picker, alignment received from a phone.

**Send alignment over Wi-Fi** — a phone that aligned a book sends the map to a TV or tablet that
cannot align for itself (UDP discovery, TCP transfer, the receiving side asks first).

**iOS** — AVPlayer + Now Playing, security-scoped bookmarks for files, whisper with the GPU off in
the background, `BGContinuedProcessingTask` to keep aligning after leaving the app.

## 6. Bug log

Every entry below was either reported by the user from a real device or found in a code review the
user asked for. Format: **symptom** → cause → fix.

### Alignment

- **The whole-book alignment of a book that had previously aligned perfectly now produced nothing**
  ("0 strong anchors of 2" for every chapter), and restarting after stopping at chapter 7 went back
  to chapter 1. → A change that multiplied anchor confidence by Whisper's segment probability; but
  Whisper.net reports probability 0 unless `WithProbabilities()` is set, so every real anchor scored
  0 and the outlier filter kept only the two boundary guesses. The test double defaulted probability
  to 1, which is why 278 tests stayed green. → Reverted, resume now trusts the book's own progress
  record, poisoned maps self-heal, and the fake narrator was fixed to behave like the real one.
- **A 7-hour Croatian MP3 aligned for hours and saved nothing.** → An MP3 without chapter marks is one
  7-hour chapter, and the map was only saved at chapter end. → Checkpoints every 20 probes, resume
  inside a chapter, log file on disk (logcat had already rotated the evidence).
- **Sync on the fly sometimes never started, silently.** → A `Task.Run` lambda read a field that
  `Stop` could null in between; the exception went nowhere. → Capture locals.
- **Two live-sync runs at once** (two models, two decoders, two writers of one map). → `Stop` cleared
  its fields before awaiting, so `Start`'s "stop the previous run first" saw nothing to stop. →
  `SemaphoreSlim` around start/stop.
- **A found phrase was logged as "no match"** and the search radius tripled for nothing. → The
  anchor was rejected by the map but the search position was never advanced.
- **One bad early anchor poisoned its chapter forever.** → Switching from a rebuild (which could
  out-vote a bad anchor) to insert-only (which always rejects the newcomer). Loaded maps were also no
  longer sanitised, so two anchors at the same offset could divide by zero → `SeekTo(long.MinValue)`.
- **Live alignment took about a minute to find its place** after Audible intros, table of contents,
  maps and "previously on" recaps. → It searched only near where it expected to be. → If nothing is
  located, search the whole book for phrases of 10+ words, rejecting ambiguous matches.
- **Alignment stopped when the iPhone was locked.** → iOS refuses Metal in the background. → CPU-only
  whisper plus `BGContinuedProcessingTask`.
- **The 32-bit Android TV "could not find" anything while aligning live.** → whisper has no 32-bit
  build; the switches were shown anyway. → The app now says plainly why alignment isn't available.

### Reader and player

- **The play button in the reader went grey** while alignment warmed up. → Playback was gated on the
  map. → Play is never disabled; the gate only decides where to start.
- **The pause button stopped working while the book was being read aloud.**
- **Reopening a paired book jumped to where the voice last was, not where I was reading**, and in
  another case **played from the page last left instead of the page asked for**.
- **The reader saved a guessed audio position over a real one.**
- **A chapter jump in the text didn't move the narration**, and after moving it the voice started
  before the seek had landed.
- **Opening a book waited on the playback service** (and sometimes claimed a book was loaded after
  the service had died).
- **Deleting a book that was playing kept playing it, and its space was never freed.** → Linux keeps a
  deleted-but-open file until it is closed. → Unload the player first.
- **The reading highlight was invisible on the Dim and Night themes.**
- **The sleep countdown overlapped the toolbar.**

### Library and import

- **An ebook and audiobook with the same name, added separately, became two books.** → Now a file
  that matches a book missing exactly that half joins it.
- **Removing the audio from a paired but not-yet-aligned book deleted all its chapters, with no way
  back.** → The replacement chapters from the ebook were never passed along. A test existed — for an
  overload production never called.
- **Croatian `.txt` books lost every č, ć, ž, š, đ** to U+FFFD. → UTF-8 with a replacement fallback.
  → Strict UTF-8, falling back to Windows-1250.
- **Library sorting didn't work** — it sorted by a column nothing ever wrote.
- **Lost updates on the book row** — three writers each saved a whole stale snapshot, so toggling a
  switch during alignment erased 40 minutes of progress.
- **The reading position occasionally jumped backwards** — a read-modify-write race.
- **An audiobook named `…m4b.mp3` was rejected.** → Format now read from the file's bytes, with the
  platform's own extractor as a fallback when the tag reader refuses a file.
- **A picker that dies with the process left the import spinner forever.**
- **An EPUB 3 added as an ordinary ebook showed no narration.** → The phone still had the build from
  before EPUB 3 import existed; an EP3 import button now makes the intent explicit.

### Audiobookshelf

- **Asked to sign in again every hour.** → Access tokens are 1 h; refresh tokens rotate. → Single-flight
  refresh with the new refresh token saved.
- **A book already on the phone was offered for download again.** → Titles from audio tags and
  server metadata disagree. → The server item id is stored with the book.
- **Streaming failed with an error.** → `NetworkOnMainThreadException` on Android, and whole-file
  range requests. → Bounded ranges (1 MB after a seek, doubling to 16 MB), hashing off the UI thread.
- **"Continue where you were?" never appeared**, then appeared again after leaving to the shelf. →
  The local "last read" timestamp was bumped by idle saves. → Separate activity and "seen" markers.
- **Server downloads were deleted from the user's own folder on iOS.**
- **Sign-in failures said nothing.** → Plain messages where the failure happened.

### UI

- **Dialog buttons did nothing** ("Yes and No don't react"). → `TaskCompletionSource` continuations
  run synchronously, so the answer resumed the caller while the dialog was still on the stack, and it
  was popped from the wrong navigation. → Pop first, then answer.
- **The dialog stretched full-screen with its text misaligned.** → A card in an Auto grid row.
- **Two switches on the book page looked "very strange" in dark mode.** → The switch thumb was the
  same colour as the card behind it.
- **The bottom bar was tall and uneven.** → Android reserves space for tab icons even when there are
  none. → Real icons.
- **Tapping the already-selected Books tab did nothing.** → Android reports that as "reselected",
  which Shell doesn't forward. → Hook the native navigation view.
- **The half-download button ate its own label; the first-run folder warning was unreadably small;
  the server login cut "Audiobookshelf" to "Audioboo".**
- **The TV banner was jammed against the left edge.**

### Crashes and platform

- **Reopening the app after swiping it away crashed** on Android. → A discarded shell still answered
  tab-reselect events (`ObjectDisposedException`).
- **Switching language crashed**.
- **First iOS install crashed at launch on an iPad.** → Whisper.net's iOS static libraries are built
  for iOS 18.5 and use newer Accelerate symbols. → The real minimum is iOS 18.5.
- **The camera was refused and there was no way back.** → A button that opens the app's settings page.
- **Android TV: the remote couldn't reach anything**, focus was invisible, and chapter titles couldn't
  be selected. → RecyclerView and Shell's ViewPager swallow focus; MAUI layouts don't draw a focus
  ring (`willNotDraw`); labels and images weren't focusable at all.
- **The TV had no way to pick a file** — Sony ships no document picker. → A file-manager path via
  `GET_CONTENT`, with a message naming the file manager to install.
- **The TV ran out of space importing one book.** → Staged files moved instead of copied, and a sweep
  that removes files an interrupted import left behind.
- **A Play Console release got stuck** — version code 3 was orphaned in a discarded draft.

## 7. What went wrong in the process, honestly

- **Fixes introduced regressions.** A review round found seven bugs that the *previous* round of
  fixes had created. Since then, every fix to a subtle area gets its own test first.
- **Test doubles lied.** Twice, a fake behaved "nicer" than the real thing (probability 1 instead
  of 0; words spread evenly, exactly the assumption being tested), and the suite stayed green while
  the device produced nothing. The fake narrator now mirrors the real recognizer.
- **Claims without measurements.** An early "it took 5 s because of JSON parsing" turned out to be
  a 146 ms parse. The plan document now separates *measured* from *assumed*, and optimisations wait
  for a number.
- **The simplest explanation first.** "The EPUB 3 shows no narration" was simply an old build on the
  phone — checked with one `adb` command before touching any code.

## 8. Limitations

- Alignment needs 64-bit hardware (whisper has no 32-bit build); a 32-bit TV receives alignments from
  a phone instead.
- EPUB 3 read-alongs whose narration is split into several audio files aren't supported yet.
- The desktop MMS model is CC-BY-NC — fine for personal use, not for selling books.
- iOS builds need CI and sideloading without a paid Apple account, and expire every 7 days.

## 9. Links

- Source: <https://github.com/Gajotres100/AudiobookReader>
- Forced alignment references: [Storyteller](https://storyteller-platform.dev/docs/the-algorithm/),
  [ctc-forced-aligner](https://github.com/MahmoudAshraf97/ctc-forced-aligner),
  [MMS forced aligner](https://huggingface.co/MahmoudAshraf/mms-300m-1130-forced-aligner)
