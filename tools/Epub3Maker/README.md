# epub3maker

Makes **EPUB 3 read-along books** out of an ebook and its audiobook. The output is an EPUB with
**Media Overlays**: the narration is inside the file, and every sentence knows when it is spoken.
A reading system that supports Media Overlays highlights the text as the narrator reads it. Those
include [Syncbook](../../README.md), Thorium Reader, Apple Books and others.

It works in two ways:

- **One book, now:** `epub3maker book.epub book.m4b`
- **A whole library, unattended:** `epub3maker scan /audiobooks --output /readalong`. It is meant to
  run every night on the server that holds your audiobooks, next to Audiobookshelf or any other
  audiobook server. It finds every folder with an EPUB and its audio, makes what is missing, and
  remembers what it has done.

It runs on Windows, Linux and Docker. It needs .NET 10 and FFmpeg; the Docker image and the
release builds bring .NET with them.

---

## Quality

How hard it listens. This is the one setting worth thinking about.

| `quality` | How | Precision | 10 h book, 8 cores | 10 h book, 2 vCPU |
|---|---|---|---|---|
| `light`  | Whisper *tiny*, listens to 10 s of every minute | ~1–1.5 s | ~10 min | ~30–40 min |
| `better` | Whisper *base*, listens to 15 s of every 30 s | ~0.5–1 s | ~40 min | ~2–4 h |
| `best`   | Meta's MMS forced aligner, every letter of the whole book | 20–80 ms | ~2 h | ~8–12 h |

- **`light` and `better`** find *anchors*: places where what the narrator says is found in the
  text. Between anchors the timing is estimated. With `anchors` you set how often it listens:
  `sparse` (every 60 s), `medium` (30 s), `dense` (15 s), or a number of seconds. Denser anchors
  mean more work and less estimating.
- **`best`** measures every sentence and estimates nothing. It is slow on a small server, but a
  book in progress survives any stop, so a night window works. The model is Meta's MMS, 356 MB,
  downloaded once. Its licence is CC-BY-NC 4.0: fine for your own books, not for selling them.
- The times in the table are estimates. A cloud "2 vCPU" is usually one physical core. The log
  shows the real speed (`1.7x` means 1.7 times faster than real time).

## What it looks for

A folder that holds both an **EPUB** and **audio** is a book, however deep it sits:
`Author/Series/Title/`. This is the layout Audiobookshelf uses.

```
Audiobooks/
  Lewis Carroll/Alice in Wonderland/
    alice.epub
    alice.m4b                       one file
  Some Author/Long Book/
    book.epub
    01 - Chapter One.mp3            a file per chapter, joined in natural order
    02 - Chapter Two.mp3
  Other Author/Big Book/
    book.epub
    CD1/*.mp3  CD2/*.mp3            discs in subfolders
  Series/
    Book One.epub  Book One.m4b     several books in one folder:
    Book Two.epub  Book Two.m4b     each ebook takes the audio of the same name
```

- **More than one audio file** are joined into one without re-encoding. They must all be the same
  format: all MP3 or all M4A/M4B.
- **EPUBs that already have narration** are skipped. That includes the tool's own output, so the
  output folder can sit inside the library.
- **Folders it cannot pair with certainty** are reported instead of guessed, for example two ebooks
  and one audiobook with nothing linking them. Aligning a book to the wrong narration wastes a night.

The EPUB 3 goes to the output folder, mirroring the library's folders: `readalong/Lewis Carroll/Alice in
Wonderland/alice.epub`. **Add that folder to Audiobookshelf as its own library** (type *Books*). Your
original library stays untouched and can be read-only. Syncbook recognises an EPUB 3 with narration
when it downloads one from the server and opens it as a read-along book.

Each EPUB 3 carries the audio inside it, so it is about as big as the audiobook.

Before the first real run, check what it found:

```bash
epub3maker scan /path/to/audiobooks --output /path/to/readalong --dry-run
```

---

## Settings

Every setting can be given in four places. Each one overrides the one before it:

1. **A settings file.** By default `epub3maker.conf` in the data folder, or `--config <file>`.
2. **Environment variables**, such as `EPUB3MAKER_QUALITY=best`. This is what Docker uses.
3. **The command line**, such as `--quality best`.
4. **One book's own file**, named `.epub3maker` in its folder. This applies to that book only.

| Setting | Command line | Environment | Default |
|---|---|---|---|
| library (audiobooks) | first argument | `EPUB3MAKER_LIBRARY` | — |
| output folder | `-o`, `--output` | `EPUB3MAKER_OUTPUT` | — |
| quality | `-q`, `--quality` | `EPUB3MAKER_QUALITY` | `better` (`best` for one book) |
| anchor spacing | `--anchors` | `EPUB3MAKER_ANCHORS` | per quality |
| Whisper model | `-m`, `--model` | `EPUB3MAKER_MODEL` | per quality |
| narration language | `--language` | `EPUB3MAKER_LANGUAGE` | recognised from the text |
| threads | `--threads` | `EPUB3MAKER_THREADS` | all cores |
| time window | `--window 01:00-07:00` | `EPUB3MAKER_WINDOW` | any time |
| keep running | `--watch 60m` | `EPUB3MAKER_WATCH` | one pass, then exit |
| retry failed books | `--retry` | `EPUB3MAKER_RETRY=1` | no |
| priority | `--priority low\|normal` | `EPUB3MAKER_PRIORITY` | `low` |
| data folder | `--data` | `EPUB3MAKER_DATA` | your profile's `Epub3Maker` |
| FFmpeg | `--ffmpeg` | `EPUB3MAKER_FFMPEG` | `ffmpeg` on the PATH |

An example `epub3maker.conf`:

```ini
library = D:\Audiobooks
output  = D:\ReadAlong
quality = better
anchors = medium
window  = 01:00-07:00
```

An example `.epub3maker` in one book's folder:

```ini
quality = best     # this one deserves a night of listening
language = hr
# skip             # or: leave this book alone
```

Croatian names work too: `kvaliteta = najbolje`, `sidra = gusto`, `prozor`, `izlaz`, `preskoči`.

### What happens when

- **It is stopped** (end of the window, Ctrl+C, `docker stop`, a reboot): the book in progress
  carries on from where it was the next time. The whole book is never started again.
- **A book is done:** it is remembered by its files' names, sizes and dates. It is made again only
  if one of its files changes, or if its own `.epub3maker` asks for a different quality or anchor
  spacing. Changing the default quality does not remake books that are already done.
- **A book fails:** the reason goes into the report and the book is left alone until its files
  change or you run with `--retry`.
- **Only one scan runs at a time.** A second one started while the first is still working exits
  straight away.

### Files in the data folder

| File | What it is |
|---|---|
| `report.txt` | the summary: done, in progress, waiting, failed and why |
| `log.txt` | everything, with times: speed, anchors, warnings |
| `state.json` | what it remembers between runs. Deleting it is safe: finished EPUB 3s are found again in the output folder. |
| `models/` | the speech models, downloaded once (up to ~360 MB) |
| `work/` | books in progress. It is emptied as each book is finished. |

---

## Windows

**1. Install FFmpeg** (in an elevated PowerShell):

```powershell
winget install Gyan.FFmpeg
```

**2. Get epub3maker.** You can download `epub3maker-win-x64.zip` from the
[releases](https://github.com/Gajotres100/AudiobookReader/releases) and unzip it. Or you can build it
yourself with the [.NET 10 SDK](https://dotnet.microsoft.com/download):

```powershell
git clone https://github.com/Gajotres100/AudiobookReader.git
cd AudiobookReader
dotnet publish tools/Epub3Maker -c Release -r win-x64 --self-contained -o C:\Epub3Maker
copy tools\Epub3Maker\deploy\install-windows.ps1 C:\Epub3Maker\
```

**3. One book, to try it:**

```powershell
C:\Epub3Maker\epub3maker.exe "D:\Books\alice.epub" "D:\Books\alice.m4b" --quality light
```

**4. Every night, as a scheduled task.** Run this in an elevated PowerShell, from the folder with
`epub3maker.exe`:

```powershell
.\install-windows.ps1 -Library "D:\Audiobooks" -Output "D:\ReadAlong" -Quality better -Window "01:00-07:00"
```

It writes `C:\Epub3Maker\data\epub3maker.conf` and registers a task named **epub3maker** that runs
as SYSTEM. The task starts every night when the window opens, and at startup, so a server rebooted
at night carries on. To change settings later, edit the `.conf` file; you don't need to install
again.

```powershell
# what did it find?
C:\Epub3Maker\epub3maker.exe scan --data C:\Epub3Maker\data --dry-run

# run it now, during the day, outside the window
C:\Epub3Maker\epub3maker.exe scan --data C:\Epub3Maker\data --window off

# follow the log, see the summary
Get-Content C:\Epub3Maker\data\log.txt -Tail 30 -Wait
Get-Content C:\Epub3Maker\data\report.txt

# remove the task
.\install-windows.ps1 -Uninstall
```

---

## Linux

**1. Install FFmpeg and .NET 10.** Debian/Ubuntu shown; see
[Install .NET on Linux](https://learn.microsoft.com/dotnet/core/install/linux) for other distributions.

```bash
sudo apt install ffmpeg libgomp1
```

**2. Get epub3maker.** You can download `epub3maker-linux-x64.zip` from the releases and unzip it
to `/opt/epub3maker`. That build is self-contained and needs no .NET. Or build it yourself:

```bash
git clone https://github.com/Gajotres100/AudiobookReader.git
cd AudiobookReader
sudo dotnet publish tools/Epub3Maker -c Release -r linux-x64 --self-contained -o /opt/epub3maker
```

**3. One book, to try it:**

```bash
/opt/epub3maker/epub3maker ~/books/alice.epub ~/books/alice.m4b --quality light
```

**4. Every night, as a systemd service.** It runs in the background, looks for new books every
hour and works only inside the window. Create `/etc/systemd/system/epub3maker.service`:

```ini
[Unit]
Description=epub3maker: EPUB 3 read-along books from the audiobook library
After=network-online.target

[Service]
ExecStart=/opt/epub3maker/epub3maker scan --data /var/lib/epub3maker
Environment=EPUB3MAKER_LIBRARY=/srv/audiobooks
Environment=EPUB3MAKER_OUTPUT=/srv/readalong
Environment=EPUB3MAKER_QUALITY=better
Environment=EPUB3MAKER_WINDOW=01:00-07:00
Environment=EPUB3MAKER_WATCH=60m
Nice=19
CPUQuota=150%
Restart=on-failure
TimeoutStopSec=60

[Install]
WantedBy=multi-user.target
```

```bash
sudo mkdir -p /var/lib/epub3maker
sudo systemctl daemon-reload
sudo systemctl enable --now epub3maker
journalctl -u epub3maker -f               # follow it
cat /var/lib/epub3maker/report.txt        # the summary
```

The service runs as root by default. If your audiobooks belong to another user, such as the one
Audiobookshelf runs as, add `User=` and make sure that user can write to the output and data
folders.

---

## Docker

The image contains everything: .NET, FFmpeg and the tool. The models are downloaded into the
`/data` volume the first time they are needed.

| Path in the container | What goes there |
|---|---|
| `/library` | your audiobooks. Can be read-only (`:ro`). |
| `/output` | where the EPUB 3s go |
| `/data` | models, work in progress and the report. Keep it on a volume, or every restart downloads the models again and starts unfinished books over. |

**Once, to see what it finds:**

```bash
docker run --rm \
  -v /srv/audiobooks:/library:ro -v /srv/readalong:/output -v epub3maker-data:/data \
  ghcr.io/gajotres100/epub3maker scan --dry-run
```

**Every night, with docker compose,** next to Audiobookshelf. See
[`docker-compose.yml`](docker-compose.yml):

```yaml
services:
  epub3maker:
    image: ghcr.io/gajotres100/epub3maker:latest
    restart: unless-stopped
    volumes:
      - /srv/audiobookshelf/audiobooks:/library:ro
      - /srv/audiobookshelf/readalong:/output
      - epub3maker-data:/data
    environment:
      EPUB3MAKER_QUALITY: better
      EPUB3MAKER_WINDOW: "01:00-07:00"
      EPUB3MAKER_WATCH: 60m
      TZ: Europe/Zagreb
    cpus: 1.5
    stop_grace_period: 1m

volumes:
  epub3maker-data:
```

```bash
docker compose up -d
docker compose logs -f epub3maker
docker compose exec epub3maker cat /data/report.txt
```

`TZ` sets the time zone the window is read in. Without it the window is in UTC.

**Build the image yourself** from a clone of the repository:

```bash
docker build -f tools/Epub3Maker/Dockerfile -t epub3maker .
```

---

## Repairing EPUB 3s already made

Versions before October 2026 could time a chapter's heading and first sentences into the last
seconds of the chapter before, so a reader jumping to that chapter played the end of the previous
one. New books are written correctly; books already made are put right in place, without listening
again — a few minutes per book:

```bash
epub3maker repair "E:\ReadAlong"          # a folder, with its subfolders
epub3maker repair "Blackwing.epub"         # or single books
```

A book with nothing to repair is left untouched, so running it twice is harmless.

## One book: every option

```
epub3maker <book.epub> <audiobook.m4b|.mp3> [options]

  -o, --output <file>       where to write (default: "<book> - EPUB3.epub" next to the ebook)
  -q, --quality <q>         light | better | best (default: best)
  --anchors <a>             light/better: sparse | medium | dense | seconds between probes
  -m, --model <name>        Whisper model: tiny | base | small | medium
  --map <book-N.sync.json>  use an alignment exported from the Syncbook app, no listening
  --language <hr|en|…>      narration language, if it is not recognised
  --threads <n>             processor threads (default: all)
  --ffmpeg <path>           ffmpeg, if it is not on PATH
  --data <folder>           models and work in progress
```

`epub3maker --help` and `epub3maker scan --help` list everything.
