**Title:** I built an audiobook + ebook "read-along" app in 4 weeks by talking to Claude Code — it aligns the text to the narration on the phone itself (open source)

---

I wanted what Kindle + Audible Whispersync does — the ebook follows the narrator, sentence by
sentence — but for my own audiobooks and ebooks, without a server and without a PC step. Nothing
did it, so I built it. More precisely: I described what I wanted (in Croatian, my native language)
and Claude Code wrote, tested, deployed and debugged it, while I tested on real devices every day.

**What it does**

- A full audiobook player (m4b chapters, speed, sleep timer, bookmarks, lock screen, Android Auto)
  and a full ebook reader (EPUB, TXT, PDF).
- Give it both halves of the same book and it **aligns them on the phone**: Whisper listens to
  short samples, the recognised phrases are found in the text, and the sentence being spoken is
  highlighted. Or "align while reading", which measures just ahead of where you are.
- Keeps the phone cool: background-priority threads on the efficiency cores, duty cycling, thermal
  back-off, "only while charging".
- Audiobookshelf: browse, download, **stream without downloading**, and **sync progress** between
  devices.
- Android TV (including a 32-bit TV that can't run Whisper — the phone sends it the alignment over
  Wi-Fi), iOS via CI without owning a Mac.
- Photograph a page of the paper book → it finds that place in the ebook and audio.
- A PC tool that uses Meta's MMS forced-alignment model to produce standard **EPUB 3 read-alongs**
  accurate to 20–80 ms per sentence, which the app imports as already-aligned books.

**Numbers**: 4 weeks, 125+ commits, ~36k lines of C#/.NET MAUI, 335 automated tests, Android +
Android TV + iOS from one codebase.

**How the collaboration actually worked**

Claude Code didn't just write code — it installed the Android SDK from the command line, deployed
to my phone and TV over adb, pulled the app's database and logs off the device to diagnose bugs from
real data, set up Codemagic so I can build iOS without a Mac, and researched things like Play
Console requirements and forced-alignment libraries (with sources).

My job was deciding what to build and reporting what felt wrong. It wasn't magic: some fixes
introduced regressions, and twice a test double was "nicer" than the real thing so the tests stayed
green while the phone produced nothing. The full write-up lists **every bug I reported**, what
caused it and how it was fixed — I think that's the most honest picture of what building with an AI
agent looks like.

- Full project story and bug log: https://github.com/Gajotres100/AudiobookReader/blob/main/docs/PROJECT_STORY.md
- Source: https://github.com/Gajotres100/AudiobookReader

Happy to answer questions about the alignment approach or the workflow.
