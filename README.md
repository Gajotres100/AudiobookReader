# AudioBookReader

Android audiobook player that also reads along with you.

Load an audiobook and the ebook of the same title, and the text follows the narration — sentence by
sentence, with the current one highlighted. Press and hold a sentence to play from there. The
alignment between voice and text is computed **entirely on the phone**; nothing is uploaded and no
desktop step is required.

It also works as an ordinary audiobook player with no ebook attached, and as an ordinary ebook
reader with no audio attached. Either half can be added or removed at any time.

## Features

**Player** — chapters, ±10 s, speed to 2×, sleep timer with an end-of-chapter option, bookmarks,
resume, lock-screen and headset controls (Media3 / ExoPlayer).

**Reader** — paged text that turns with a swipe, pinch to resize, paper/sepia/night colouring,
selectable typeface. Controls hide themselves while the narration plays.

**Read-along** — the point of the project. Two ways to get there:

- *Align in advance.* Samples ten seconds of every minute and interpolates between, mapping a
  ten-hour book in about fifty minutes of background work.
- *Measure while reading.* Transcribes continuously just ahead of the playhead, so nothing is
  interpolated at all. Costs about half of real time, and only for the passage being read.

**CPU budget** — presets from *Štedljivo* to *Brzo*, with duty cycling, background-priority threads
that keep the work on the efficiency cores, automatic thermal backoff, and constraints such as "only
while charging". The stated aim is that the phone never becomes unpleasant to hold.

## How the alignment works

The book text is already known, so recognition does not have to discover the words — only locate
them. Each probe is transcribed with whisper (ggml tiny, quantized) and matched into the book text
by Smith-Waterman over words, which tolerates the substitutions, drops and inventions that
recognition produces. Anchors are timed by whisper's own segment timestamps rather than by when the
probe began, and each match yields two of them — one at each end of the window.

## Layout

```
src/AudioBookReader.Core/   models, ebook parsing, alignment, sync map — no MAUI dependency
src/AudioBookReader.App/    MAUI Android app, Media3 playback, on-device recognition
src/AudioBookReader.Tuner/  console harness for working on alignment from a desktop
tests/                      xUnit, 183 tests
```

Core carries no MAUI dependency so the alignment engine can be exercised in seconds on a desktop
rather than in minutes of deploy cycles. The tuner is a development tool and is not shipped —
alignment always runs on the phone.

## Building

Requires the .NET 10 SDK, the `maui-android` workload, the Android SDK and JDK 17.

```bash
dotnet build src/AudioBookReader.App -t:Run -f net10.0-android
```

```bash
dotnet test tests/AudioBookReader.Core.Tests
```

## Status

Working on-device: import, playback, reading, alignment both ways, read-along.
Still to come: bookmarks list, MOBI/AZW3 parsing, Play Store packaging.
