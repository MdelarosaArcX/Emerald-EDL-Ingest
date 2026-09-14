# Emerald

A Windows desktop application (C# / WPF, .NET 8) for SDI playout, recording and review on
DELTACAST hardware. It is self-contained: no server, no network transport, nothing to
connect to.

Emerald opens on a **shell** with a live confidence monitor on an SDI receiver, and five
ways in:

- **Playback** — the capture deck's opposite number: a transmitter, a confidence monitor on
  whatever comes back, and **tidal lock**, which puts the capture deck's receiver to air a
  fixed time later.
- **EDL Generator** — composes an EDL playout command and plays it out of a DELTACAST SDI
  output, cued to house timecode, recording the return feed while it is on air.
- **Ingest Controller** — schedules recordings off an RX input: pick a board and port, a
  start timecode and a duration, and it rolls on the frame, verifies the file and registers
  it with the media store. Several jobs can be queued across different boards and ports.
- **Live Edit** — the editing workspace over what has been captured. Layout only for now;
  trimming and rendering are not built.
- **Monitor** — everything from capture to on air in one ordered, filterable record, kept on
  disk, with a live strip across the top showing what each stage of the chain is doing.

They open as windows in the **same process**, which is what lets them share the hardware
(see *One receiver, three claimants* below) and one station clock. Launching Emerald a second
time does not start a second copy — the new process hands its request to the running one and
exits, so `run-edl.bat` and `run-ingest.bat` give you two windows side by side.

## The projects

```
Emerald.sln
├─ src\Emerald.App            the capture and playback decks, and the way in   [WinExe]
├─ src\Emerald.EDL            the EDL generator window and the playout engine
├─ src\Emerald.Ingest         the Ingest Controller: scheduling, queue, job store
├─ src\Emerald.LiveEdit       the editing workspace (layout only so far)
├─ src\Emerald.Media          media management: scan, probe, and the capture store
├─ src\Emerald.Video          ffmpeg: decode, audio beds, and RX recording
├─ src\Emerald.Deltacast      VideoMaster interop, boards, TX output, RX arbitration
├─ src\Emerald.Core           timecode, the station clock, settings, the activity record
├─ tests\Emerald.Core.Tests
├─ tests\Emerald.Edl.Tests
├─ tests\Emerald.Ingest.Tests
└─ tests\Emerald.Video.Tests
```

References run one way only, `Core ← Deltacast ← Video ← Media`, with the UI projects on top
and `Emerald.App` referencing everything. `Emerald.Ingest` sits above Deltacast, Video and
Media and orchestrates them; it owns no hardware, no encoder and no clock of its own.

Only one NuGet package is used outside the test projects: `Microsoft.EntityFrameworkCore.Sqlite`,
which backs the ingest job store. Everything else is the framework.

Two placements are worth explaining, because the obvious reading is the wrong one:

- **`VideoFormat` is in Deltacast, not Video.** It maps a frame rate to an SDI standard,
  interface and frame size. That is an SDI table which ffmpeg happens to consume.
- **`SdiCapture` is in Video, not Deltacast.** It is the recording engine; it reads a
  receiver through Deltacast, which is precisely why `Video → Deltacast` exists.

Every project is **x64 only** — `VideoMasterHD.dll` in `System32` is 64-bit, so every
assembly loaded into the process must match. The solution maps `Any CPU → x64` for each
project; a new project added without `<Platforms>x64</Platforms>` will fail the build.

## Build and run

Double-click one of the three run scripts, or from a terminal:

```
run.bat                 build (Release) and open the capture deck
run-edl.bat             build (Release) and open the EDL Generator
run-ingest.bat          build (Release) and open the Ingest Controller
run-monitor.bat         build (Release) and open the Monitor

run.bat Debug           build Debug and launch
run.bat Release -n      skip the build, just launch what is already built

build.bat               build only (Release)
build.bat Debug         build Debug
build.bat Release -r    force a package restore first
```

All of them launch the **same executable** with a different argument
(`Emerald.App.exe`, `--edl`, `--ingest`, `--monitor`). Emerald runs once: if an instance is already up,
the script hands its request over and that instance opens the window alongside whatever is
already on screen — **nothing is closed**, and the build is skipped, because the running
instance holds a lock on its own exe. To rebuild, close Emerald first.

Every script exits non-zero on failure, so they drop straight into CI or a scheduled task,
and they pause before closing only when double-clicked, so the output stays readable.

The equivalent by hand:

```powershell
dotnet build Emerald.sln -c Release
dotnet test  Emerald.sln -c Release
.\src\Emerald.App\bin\x64\Release\net8.0-windows\win-x64\Emerald.App.exe
.\src\Emerald.App\bin\x64\Release\net8.0-windows\win-x64\Emerald.App.exe --ingest
```

Building the solution sets `Platform=x64` explicitly, which MSBuild folds into the output
path — hence the `bin\x64\` segment. Building `Emerald.App.csproj` on its own lands in
`bin\Release\` instead.

To produce a redistributable folder:

```powershell
dotnet publish src\Emerald.App\Emerald.App.csproj -c Release -r win-x64 --self-contained false
```

## Installing on another PC

### 1. Prerequisites

| | Why | Where |
|---|---|---|
| **Windows 10/11 x64** | `VideoMasterHD.dll` is 64-bit only, so the whole process is pinned to x64 | — |
| **.NET 8 SDK** (to build) | `dotnet build` / `dotnet test` | <https://dotnet.microsoft.com/download/dotnet/8.0> |
| **.NET 8 Desktop Runtime** (to run only) | Enough for a published build; the SDK already includes it | same page — pick *Desktop Runtime* |
| **DELTACAST VideoMaster driver** | The app loads `VideoMasterHD.dll` from `System32`. Without it, board scanning reports the error inline and the app still opens | DELTACAST |
| **ffmpeg + ffprobe** | Decode for playout, encode for recording, probe for media info | <https://ffmpeg.org> — the usual place is `C:\ffmpeg\bin` |
| **git** | To clone | <https://git-scm.com> |

The DELTACAST card and driver are only needed on the machine that will actually touch SDI.
The Ingest Controller has a **SIMULATE** toggle that runs against simulated boards and a
simulated recorder, so the queue, the scheduler and the whole UI can be worked on — and
demonstrated — on a laptop with no card in it.

### 2. Clone and build

```powershell
git clone https://github.com/MdelarosaArcX/Emerald-EDL-Ingest.git Emerald
cd Emerald
.\build.bat
```

The first build restores `Microsoft.EntityFrameworkCore.Sqlite` and the test packages, so it
needs internet once. After that the build is offline.

Then check it works before touching hardware:

```powershell
dotnet test Emerald.sln -c Release
```

Everything here is pure logic — timecode arithmetic, the ingest state machine, job
persistence and recovery, the input mask — so it passes with no card and no ffmpeg.

### 3. Point it at this site

Three things are machine-specific. All of them are set in the UI and stored in
`%APPDATA%\Emerald\settings.json`; none of them are in the repository.

- **Timecode generator.** The address of the timecode API, `http://<host>:8888/api/timecode`.
  There is one setting shared by all three modules — set it in the deck's *Boards and
  modules* panel, the EDL's *Timecode API* field or the Ingest Controller's *Timecode
  generator* field, press **Apply**, and every open window follows immediately. Until it is
  reachable the clock reads `--:--:--:--` with a red status dot, and an ingest cannot be
  scheduled, because there is no clock to schedule it against.
- **Board and port.** **Rescan** enumerates what is present. If nothing is found the log
  says so rather than pretending; turn on **SIMULATE** to work without a card.
- **ffmpeg.** Found automatically next to the app, then on `PATH`, then `C:\ffmpeg\bin`. To
  pin a specific build, set `ffmpegPath` in `settings.json`.

### 4. Where it keeps things

| Path | What | In git? |
|---|---|---|
| `%APPDATA%\Emerald\settings.json` | Boards, ports, timecode address, recording profile, last-used fields | No — per machine |
| `%APPDATA%\Emerald\ingest.db` | The ingest job queue and history (SQLite). Created on first run | No — per machine |
| `<clone>\media\` | Recordings and ingested clips | No — the folder is tracked, its contents are not |

`media\` runs to hundreds of gigabytes on a working machine, which is why `.gitignore`
excludes its contents while keeping the folder: `RecordingSetup` refuses to record into a
directory that does not exist, so a fresh clone still has somewhere to write. Point the
recording and ingest directories at a fast local disk with room — the Ingest Controller
estimates the space a job needs and refuses one that will not fit.

### 5. Moving an existing install

Nothing but the clone needs to move. To carry an operator's setup across, copy
`%APPDATA%\Emerald\settings.json` to the new machine; copy `ingest.db` beside it as well if
you want the ingest history. Recorded media is just files on disk — move `media\` if you want
it, or leave it behind.

On first run Emerald also reads `%APPDATA%\EdlGenerator\settings.json` if its own is absent,
so board, port, media source and audio tracks carry across from the pre-Emerald app.

### 6. If it does not start

| Symptom | Cause |
|---|---|
| *VideoMasterHD.dll was not found* | The DELTACAST driver is not installed. The app still opens; board scanning is what fails |
| *could not be loaded. The application must run as 64-bit* | A project was added without `<Platforms>x64</Platforms>` |
| *No DELTACAST board detected* | Driver present, no card — use **SIMULATE** |
| `RX<n> is already open in another application` | dCARE, or another app, holds the receiver. Close it |
| *ffmpeg was not found* | Playout and recording are unavailable; everything else works |
| Clock reads `--:--:--:--` | The timecode generator is unreachable — check the address and the network |
| Build fails on a file lock | Emerald is running. Close it; the run scripts skip the build rather than killing it |

## One receiver, three claimants

A DELTACAST receiver allows **exactly one open handle**. That is already why dCARE must be
closed before Emerald can record; inside Emerald it means the shell's preview, the EDL's
recorder and an ingest cannot all hold the same input.

`Emerald.Deltacast/RxLease.cs` referees this. The preview takes a **yielding** lease; the
recorder takes an **exclusive** one. When a message goes on air, the recorder's claim revokes
the preview, which closes its stream before the recorder opens the channel — and when
recording stops the preview takes the input back on its own. The shell says which is
happening:

```
Preview released - this input is recording.
RX1 locked to 1080p25.
```

Two separate processes could not negotiate this, which is why the modules are windows rather
than executables of their own — and why launching Emerald twice opens a second window in the
first process instead of a second copy of the application.

## Audio meters and the monitor

The deck's **Audio Tracks** panel shows the level of every stereo pair arriving on the
receiver. One row per pair actually on the wire — a feed carrying one pair shows one row, and
a four-language message grows the panel when it starts. A pair the feed is not carrying is
dimmed and cannot be selected, which is a different thing from one that is present and silent.

The bars are drawn on a **-60 to 0 dBFS** scale, not on raw amplitude: a linear bar spends all
its travel in the last few dB and reads as either full or empty. They jump to a rise instantly
and fall back gradually, the way a desk meter does — a bar that fell as fast as it rose would
be a flicker, and one that rose as slowly as it falls would miss the transient entirely. Amber
past -10 dBFS, red at full scale.

The levels come from **whichever of the preview and the recorder is holding the receiver**, so
they keep moving across the handover when a recording starts. That is why the confidence
preview now opens the receiver `JOINED` rather than video-only: without ANC there is no
embedded audio to read, and the meters had nothing to show unless a recording was running.

**LISTEN** sends the selected pair to this PC's speakers, with its own volume. It is a
confidence monitor and nothing more — it is not in the path to air, it is not what gets
recorded, and its volume changes neither. The receiver and the sound card run off different
clocks and drift apart over a long session, which is handled by dropping a frame when the
queue runs long rather than letting the delay grow without bound. A machine with no sound
device says so in the status line rather than leaving a button that appears to have worked.

### Levels on the EDL's audio tracks

Each language row in the EDL carries the same meter, showing what that track is **actually
sending to air** — read after gain and mute, which is the whole point of watching it while
trimming a level.

| Control | What it does |
|---|---|
| **− / + / 0** | This language's level, a decibel at a time, from -40 dB to +12 dB. Applied to the samples on their way to the card, so it **changes what is transmitted** — unlike the deck's monitor volume. Clamped rather than wrapped, so a boosted track distorts like an overdriven desk instead of inverting into noise. |
| **SOLO** | Mutes every other language on air, so one can be checked on its own. Pressing it again on the same track clears the solo and brings them all back. A muted track is still decoded and still advanced — stalling it would leave it at the wrong position the moment it came back — it is simply embedded as silence. |

Solo is a change to what goes out, not to what this PC hears, which is why it is logged.

## The capture store

`Emerald.Media/MediaLibrary.cs` owns where recordings live — `media\` beside the solution by
default, overridable in the EDL's **Recording folder**. Recordings land there full
resolution with their audio, exactly as they came off the wire, and Live Edit lists that same
folder. Nothing is copied or transcoded on the way in.

Settings live in `%APPDATA%\Emerald\settings.json`. On first run Emerald reads
`%APPDATA%\EdlGenerator\settings.json` if its own is absent, so board, port, media source and
audio tracks carry across from the pre-Emerald app.

## EDL fields

| Field | Notes |
|---|---|
| **Capture Board** / **Playback Board** | Chosen **independently** — the RX can be on board 0 and the TX on board 1, or the other way round. Rendered as `0. DELTA-3G-elp-d 22`, matching the DELTACAST index/model convention. **Rescan** re-enumerates without restarting. |
| **Capture Port** | `RX0`…`RXn` on the capture board, where *n* comes from `VHD_CORE_BP_NB_RXCHANNELS`. |
| **Playback Port** | `TX0`…`TXn` on the playback board, from `VHD_CORE_BP_NB_TXCHANNELS`. A board with no TX channels (e.g. `DELTA-12G-elp-h 20`, which is 8 RX / 0 TX) shows an amber warning and blocks Send. On startup each side defaults to the first board that can actually do that job. |
| **Timecode API** | Default `http://10.0.0.31:8888/api/timecode`. **Apply** switches source at runtime — and it is **one setting for the whole application**, so the deck and the Ingest Controller re-dial with it and show the new address immediately. |
| **Start Timecode** | On-air time on the house clock. `HH:MM:SS:FF`, masked (see below) and validated against the live frame rate. **Now** stamps the realtime timecode **plus two minutes** — stamping the clock exactly would put the cue in the past by the time the rest of the form is filled in, and a start that has gone by plays immediately. |
| **SOM** (in-point, from the head) | How far **into the clip** to start, measured from its first frame and independent of whatever timecode the file carries. `00:01:00:00` plays from one minute in, on any clip — ffmpeg is handed it as a seek. Must be shorter than the media. It does **not** delay the cue. |
| **EOM** (out-point, from the head) | Where to stop, on the same elapsed scale, so `EOM − SOM` is the duration. A 3-minute clip with SOM `00:01:00:00` and EOM `00:03:00:00` plays its last two minutes. Choosing a file **seeds SOM to `00:00:00:00` and EOM to the clip's length**, so it plays whole until you trim it. **Set EOM equal to SOM** for no fixed duration: the media loops until stopped. |
| **Duration** | **Editable**, and tied to EOM both ways: type a duration and EOM follows (`SOM + duration`); type an EOM and the duration follows (`EOM − SOM`). There is no mode to choose — whichever you last typed into is the one you are driving, and the calculated one is tinted. Moving **SOM** then leaves the field you set alone and re-derives the other. |
| **Stop Time** | **Read-only** — start timecode + duration, wrapped at 24 h. Start `20:57:26:00` with a two-minute duration stops at `20:59:26:00`, whatever SOM is. |
| **Audio Tracks** | **Selecting media loads every language embedded in it**, one row per audio stream, named from the stream's own title or language tag. Beds in separate files can be added alongside with **Add track...** or by dropping them. **All of them are transmitted at once**, each on its own SDI channel pair — track 1 on CH 1-2, track 2 on CH 3-4, up to 8 tracks (16 channels). The pair is shown on each row. Each has its own **+ / −** trim at **10 ms per tick** (±500 ms) with a **0** reset, a **level** in decibels, a live **meter**, and **SOLO** to mute every other language — all independent per language and adjustable while on air. |
| **Media Source** | **Optional.** Drop a folder or file onto the panel, or browse. Folders are scanned one level deep for playable containers (`.mxf .mov .mp4 .avi .mkv .ts .m2t .mpg .dv .gxf .lxf .webm .yuv .wav` …) and sent as an ordered playlist. |
| **Post Play** | What the TX carries once the message ends and until the next one cues: **Black Screen** or **Freeze on last frame**. |
| **Recording folder** | Where RX recordings are written, in 2-minute segments, while a message is on air. Leave empty to record nothing. Validated as you type, with free space shown. |

**Send EDL** stays disabled until every field is valid; the reason is shown under the button
and it explains what is missing.

## What Send EDL does

**Send EDL queues the message.** It does not interrupt whatever is on air. Two things
happen, in this order:

1. **Records** the command in the activity log — start, SOM/EOM, duration, stop time, post
   play, signal path, media. **Copy JSON** puts the composed record on the clipboard.
2. **Queues it.** The engine plays queued messages in the order they were added, each cued
   to its own start timecode.

Both are local. The board and the timecode clock are all it needs, so you can confirm the
output in **dCARE** with nothing else running.

## The queue

The QUEUE panel lists every message with its live state:

| State | Meaning |
|---|---|
| **QUEUED** | Loaded and waiting its turn. |
| **CUED** | Next up and holding. |
| **PLAYING** | On air. |
| **DONE** / **STOPPED** / **FAILED** | Finished. **Clear finished** removes them. |

Each row carries start, duration, stop time, TX channel and post-play setting.

### The countdown

Every row that has somewhere to be shows a live countdown on the right, in timecode:

| Row | Reads | Counting to |
|---|---|---|
| **QUEUED** / **CUED** | `T- 00:01:23:04`, **to air** | Its start timecode. This is the clock that starts the moment you press **Send EDL**. |
| **PLAYING** | `T- 00:00:41:11`, **left** | Its stop time — how much of the message is still to run. |
| **PLAYING**, no fixed duration | `ON AIR`, **open-ended** | Nothing; it loops until stopped. |

It turns amber inside ten seconds and red inside one, and it runs on the **house clock**,
not on wall time — it is the same sum the engine waits out, so the number on screen reaches
zero on the frame the message cues. A start timecode that has already gone by reads `ON AIR`
rather than counting down the best part of a day to it, which is also what the engine does
with it.

The engine holds **one** output open for the whole queue — a TX channel cannot be opened
twice, so giving each message its own output would make back-to-back playout impossible.
It also means the post-play fill of one message keeps the line up until the next cues,
with no black flash between them. When the queue drains, the last message's post-play
holds the output until you press **STOP**.

The log narrates the same thing, so a run can be reconstructed after the fact:

```
17:04:08  NOW PLAYING: EDL d5b28914 - stops 17:04:12:00 (00:00:03:00)
17:04:08  NEXT UP: nothing queued - freeze on last frame will hold after this message.
17:04:12  Message complete - 75 frames (00:00:03:00). Post play: freeze on last frame.
17:04:12  Queue empty - holding freeze on last frame on TX until the next message is queued.
```

## Playout

The generator drives the SDI output itself through VideoMaster.

- **Cue.** The TX channel opens as soon as you hit Send and holds **legal black**, so the
  output is locked and visible in dCARE while it waits. At the start timecode it cuts to
  the media. A start timecode that has just gone by cues immediately rather than waiting
  almost a full day for the clock to come round.
- **Format.** 1080p at the timecode server's frame rate (25 fps → 1080p25 over SMPTE 292;
  50/60 fps use 3G). Sources of any size or rate are scaled, pinned to the centre with
  black padding, and rate-converted, so mixed media plays without you matching formats.
- **Pacing.** Frames are handed to the card one slot at a time and the card blocks until
  it is ready, so playout is clocked by the SDI output rather than by a software timer.
  There is no drift.
- **SOM/EOM.** Both are marks measured **from the head of the media**, and SOM is where the
  media is entered. The output cues at the Start Timecode and cuts to the media at that
  moment, from the frame SOM names — ffmpeg is handed SOM as an in-point before `-i`, so the
  seek is by keyframe and then decoded forward, which is frame-accurate. The duration is
  `EOM − SOM`, and the playlist loops to fill it if the media runs out first.

  The file's own embedded timecode is deliberately **not** part of this arithmetic. It is
  read and shown under the drop zone
  (`length 00:03:00:00, 2 audio tracks, media TC starts 00:01:00:00`) and travels in the record as
  `mediaStartTimecode`, but the marks do not follow it — otherwise the same SOM would mean a
  different frame on every clip, which is precisely the trap a clip ingested with its own
  start timecode sets.
- **The seek is exact.** Verified end to end on a 3-minute clip carrying burnt-in timecode:
  its head reads `REC TC 02:23:49:02`, and SOM `00:01:00:00` produces a first frame reading
  `REC TC 02:24:49:02` — one minute later, to the frame.
- **Decode errors are surfaced.** ffmpeg's stderr is captured, so a file that supplies
  fewer frames than the duration needs is reported with ffmpeg's own explanation rather
  than silently rolling on to the next file.
- **Looping and duration.** Files play in the folder's sort order and the playlist repeats.
  Playout stops the frame the duration expires, mid-clip if that is where it lands. An
  empty EOM loops until you press **STOP**.
- **Post play.** When a message ends, the TX holds black or freezes on the last frame until
  the next message cues, so the line never drops.
- **Stopping.** The strip under Send EDL shows *Armed*, *On air* with elapsed time and
  current file, or *Finished*. **STOP** clears the queue and releases the output.

## Video and audio are independent

Video and each audio track have **their own ffmpeg process**, so what you select for one has
no bearing on the other:

| Selection | On air |
|---|---|
| Video only | Video to TX, **silent** — the video file's own audio is deliberately *not* used |
| Audio only | **Black screen** to TX, carrying the audio |
| Video + one track | Video with that track on channels 1-2 |
| Video + several tracks | Video with **every language at once**, one per channel pair |

At least one of the two is required; either alone is a valid message.

### Where the languages come from

**Selecting media loads its own languages.** A multi-language master already carries them, so
choosing the file fills the Audio Tracks panel with one row per audio stream, in file order,
each named from the stream's own `title` / `name` tag and falling back to its language code
(`eng`, `ara`) and then to `Track 2`. Each row shows what it actually is underneath —
`master.mov  -  stream 2 - aac 2ch [ara]` — because on a master every row names the same file
and the stream is the only thing telling them apart. `und` is not treated as a language.

Tracks in **separate files** still work exactly as before: **Add track…** or drop them on the
panel, and they sit after the media's own. The two kinds mix freely.

Choosing different media replaces the tracks that came from the old media and **leaves
hand-added ones alone**. Names you type and trims you set survive a restart, matched back to
their stream.

Under the hood each language is one ffmpeg process reading `-map 0:a:N` — so eight languages
in one master are eight readers of the same file at different streams, which is the same
arrangement as eight separate bed files and behaves identically from here on.

### Multiple languages

Every track is decoded, advanced and **embedded every frame**. Track 1 goes to SDI channels
1-2, track 2 to 3-4, and so on up to the 16 channels the standard carries — the SMPTE 299
ordering, so a router or a downstream device chooses which language to listen to. Emerald
does not choose for it, and nothing has to be switched on air to hear a different one.

A track left un-advanced would stall its decoder against its ring buffer and sit at the
wrong position, so all of them run whether or not anything downstream is listening.

The cost is one ffmpeg process and about 576 KB of ring buffer per language, which is
nothing for the eight tracks SDI can carry.

Each track loops its own files to fill the duration, so a short bed under a long message
keeps sound on air throughout.

### Adjusting on air

Each language has **its own** offset, adjustable **while a message is playing**, at **10 ms
per tick** up to ±500 ms. Picture is never touched, and because every track is on air at
once, a nudge trims that one channel pair without disturbing the others.

The offset is never passed to ffmpeg. Audio is always decoded from the natural start of the
file into a rolling three-second buffer, and the offset simply moves **where the play loop
reads from** — re-pointing an index, not restarting a process. The loop re-reads the offset
every frame, so a nudge lands on the next frame:

- **positive** reads back in time, so picture leads (audio delayed);
- **negative** reads ahead, so sound leads (audio advanced).

The buffer carries both history and lookahead, so neither direction needs a re-seek and
there is no gap or click on adjustment. Verified on the card: 90 offset changes during a
20-second message still output exactly 500 frames with no interruption to picture.

- Audio is decoded to 48 kHz 16-bit stereo, de-interleaved, and embedded into the SDI one
  mono channel at a time: track *n* fills channels *2n+1* and *2n+2*, which is group
  *n / 2*, channels *(n × 2) mod 4* and the next. Every frame rate the app outputs divides
  48000 exactly, so a frame is a whole number of samples and audio cannot drift against
  picture.
- A track whose file carries no audio stream is warned about in the log and plays silent.

Two details of the SDI side are worth recording, because both are easy to get wrong:

- The TX stream must be opened with **`VHD_SDI_STPROC_JOINED`**, not `DISJOINED_VIDEO`.
  Embedded audio travels in ANC, and a video-only stream rejects `VHD_SlotEmbedAudio` with
  `VHDERR_INVALIDSTREAM`.
- `VHD_AUDIOINFO` nests two `#pragma pack(1)` blocks inside an 8-byte-aligned outer struct,
  so it is built by hand at explicit offsets (`SdiOutput.EmbedAudio`) rather than trusting
  the default marshaller. The layout was verified against the SDK on a DELTA-3G.

### Requirement: ffmpeg

VideoMaster takes uncompressed frames and does no decoding, so ffmpeg does the decode,
scale and rate conversion. It is looked up next to the application, then on `PATH`, then
`C:\ffmpeg\bin\ffmpeg.exe`. To point at a specific build, set `ffmpegPath` in
`%APPDATA%\Emerald\settings.json`.

Without ffmpeg the app still composes and records the EDL — only playout is skipped, with
a warning in the log.

## The playback deck

Laid out as the capture deck's opposite number — same scaled design, monitor on the left,
controls down the right — because it is the same job in the other direction. What differs is
the configuration: the capture deck has one receiver, this has a **transmitter** to put
pictures out of and, independently, a **receiver** to watch them come back on. They are
chosen separately and are usually on different boards.

| Field | Notes |
|---|---|
| **Transmit** board / TX port | Where playout goes. Only boards with TX channels are offered; a board with eight receivers and no transmitter is never a playback board. |
| **Preview** board / RX port | An independent receiver to monitor on, normally the return feed. It takes a **yielding** claim, so a recorder that wants the same input takes it and the preview steps aside rather than failing the recording. |

Nothing here decodes or transmits on its own: the picture comes from `RxPreview`, exactly as
the capture deck's does, and everything reaching the transmitter goes through the same
`PlayoutService` the EDL plays out with, cued against the same station clock.

### Tidal lock

Tidal lock puts the capture deck.s receiver to air a fixed time later — a minute by default,
selectable from thirty seconds to five, or **No delay**.

**No delay** is the one that is not a delay: the transmitter takes the frame the receiver has
just produced, so the feed reaches air as soon as there is a frame rather than after a fill.
It is what you want when re-arming part-way through a recording that is already running and
you do not want to wait out another minute of black — stop the lock, start it again on *No
delay*, and it is back on air immediately.

It cannot make up a delay from a recording that already exists. The ring holds raw frames and
is allocated empty, so a fresh one has nothing in it however long the recording has been
running; reaching back into the written files would be playing a recording late, which is
precisely the design this feature is not (see *Where the delay lives*). The ring is also much
smaller with no delay to hold — about 200 MB rather than six gigabytes.

1. On the playback deck, choose the transmitter and press **ARM TIDAL LOCK**.
2. The delay begins filling, and the countdown over the picture shows how much is buffered.
3. A minute later the delayed feed cuts to the transmitter and stays exactly that far behind.

**The order does not matter.** Arming before the recording rolls and arming half an hour into
one both work: the deck remembers the format the receiver locked to, so there is always
something to size a ring from. Arming mid-recording used to do nothing at all — the delay line
was built from a one-shot event raised at the start of a recording, so the lock sat armed and
waited for the *next* one.

**Disarming and arming again starts a fresh minute.** The old ring is let go and a new one
built, so the countdown runs from now rather than rejoining a part-filled buffer. An operator
who disarms and re-arms is asking for a minute from this moment, not for whatever was left
over.

**Where the delay lives.** In a ring of **raw frames**, memory-mapped from a file that is
allocated once and reused: `RX → recorder → ring → transmitter`. Nothing is encoded, nothing
is written to a container, nothing is parsed back — the bytes the receiver produced are the
bytes the transmitter sends, and the delay is a number of frames rather than something that
emerges from how fast a decoder happens to read.

That is the difference between a delay line and a recording played back late. There is no
codec in the path to air, so no generation loss and nothing to go wrong beyond the copy
itself; and because the recorder and the transmitter are both paced by the same SDI plant,
the gap between the write head and the read head does not move.

A minute of 1080p25 is 1550 slots of 4 MB — **6.0 GB**, allocated up front under
`%LOCALAPPDATA%\Emerald\delay` (override with `tidalLockRingFolder` in `settings.json`). It
is checked for free space before arming and refused, in words, if it will not fit. Rings left
behind by a crash are swept at start-up.

**How it stays safe.** There is no lock on the hot path — a 4 MB copy under a lock would put
the transmitter's cadence at the mercy of the writer. Each slot carries its own sequence
number, written last and checked by the reader both before and after copying; if it changed
in between, the writer came round mid-copy and the frame is discarded rather than transmitted
torn. The reader alternates between two buffers so a discarded frame is never the one it was
about to fall back on. The capture loop itself only ever does one bounded, non-blocking
hand-off — the copy into the ring happens on the line's own thread, because the receiver has
four slots of headroom and cannot wait for a disk.

**Only progressive 1080 is transmitted.** Nothing in a raw path scales, and
`VideoFormat.ForFrameRate` silently falls back to 1080p25 for a rate it does not carry — so a
720p receiver would have its 1.8 MB frames copied into the top of a 4 MB raster and put
garbage on air. Tidal lock therefore works from an allowlist of known progressive 1080
standards and refuses everything else by name, interlaced included: 1080i50 is 1920×1080
counted at 25 and would otherwise pass a raster-and-rate check.

**What happens when things go wrong.**

| | |
|---|---|
| Ring not yet full | Black. This *is* the countdown. |
| Reader catches the writer | Repeat the picture, but mute the audio — repeating samples clicks, one silent frame does not. |
| Writer laps the reader | Re-establish the delay and report it loudly: that is a visible cut, and the operator has to know it happened. |
| Clocks drift apart | Corrected a frame at a time, outside a ±0.5 s deadband. A fixed delay does not need to be fixed to the frame. |
| Receiver goes quiet | Hold the last frame for ten seconds, then cut to black — a freeze is indistinguishable from a working feed on a monitor. |
| **Recording stops** | **Keep going.** A whole delay of programme is still in the ring and has not been to air. Stop-record does not mean stop-transmit; the lock shows `DRAINING` until the ring empties. |

All of that is `DelayPacer.Decide`, a pure function over integers, so the behaviour that
matters most on air is the part that is easiest to test without a card.

## The Ingest Controller

Where the EDL puts something on air, the Ingest Controller takes something off the wire: it
schedules a recording from an RX input against the station clock.

Fill in a board and port, a **start timecode**, a **duration** and a **clip name**, choose a
directory, and press **START INGEST**. The job goes on the queue, waits for its timecode,
rolls, records for the duration, stops, has its file verified and registered, and appears
under *Recent Ingests*.

### The timing model

| Field | Meaning |
|---|---|
| **Timecode (Start)** | When the recorder rolls, on the station clock. **Exactly this** — there is no preroll and nothing is subtracted from it. **NOW** stamps the current realtime timecode. |
| **Duration** | How long it records for. |
| **EOM** | Start + duration, i.e. when it stops. Editable, and tied to Duration both ways — the same automatic linkage the EDL has. |
| **SOM** | **Not a time at all**: the timecode written into the head of the recorded file, so the clip carries whatever numbering the station edits against. Defaults to `01:00:00:00`. |

So a job with start `20:57:26:00`, duration `00:15:00:00` and SOM `01:00:00:00` rolls at
`20:57:26:00`, stops at `21:12:26:00`, and produces a file whose first frame reads
`01:00:00:00` and whose last reads `01:15:00:00`. The schedule preview on the right spells
all of that out before you commit to it.

### The queue

Several jobs can be queued at once, on different boards and ports, and the scheduler runs
them concurrently where the hardware allows. A job passes through
`Created → Scheduled → Waiting → Recording → Completed`, with `Cancelled` and `Failed`
reachable from anywhere that is not already finished; illegal moves throw rather than being
tidied away, so the history can be trusted.

Timing uses two clocks deliberately. The wall clock says which *day* a start belongs to,
which a timecode that repeats every 24 hours cannot; inside three seconds of the mark the
station timecode takes over and the decision becomes frame-accurate. A start that has been
missed by more than five seconds **fails with a reason** rather than rolling late and
quietly producing a clip that is not the one that was ordered.

### Audio

The receiver's own sound is always recorded and is always **track 1**, labelled `Original`.
Nothing you do in the *Audio* panel replaces it.

Beyond that you can add up to eight further audio files — language beds, commentaries, a
clean mix — with **Add track…** or by dropping them on the panel. Each becomes its own track
in the recorded file, in list order, after the original:

| Track | What it is |
|---|---|
| 1… | `Original` — the embedded audio off the receiver, **one track per stereo pair on the wire**. A single-pair feed is one track named `Original`; a four-language feed is four, named for the channels each came off. The first is the default track. |
| then | The files you added, in the order shown, under whatever name you type next to each. |

A track shorter than the ingest repeats to fill it, and the recording still ends on the
duration rather than on the audio — so a thirty-second bed under a fifteen-minute ingest is
fine. Files are checked before the receiver is opened, so a path that has moved is refused
while it is still only a form, not a recording. `ffprobe` on the result shows every track
with its name against it.

### What it writes

Each ingest produces the pair Emerald records everywhere: a DNxHD 145 master under
`<directory>\high\` and an H.264 proxy under `<directory>\low\`, both named after the clip
and both stamped with the SOM timecode, so the EDL and any NLE read the marks back correctly.
Both carry the same audio tracks.

### Nothing is lost, and nothing is overwritten

- Jobs are persisted to `%APPDATA%\Emerald\ingest.db` **before** anything is told about
  them. On start-up, a job still ahead of its time is re-armed; one whose moment passed while
  Emerald was not running is failed with a reason; one that was mid-recording is failed as
  interrupted and its partial file left on disk as evidence.
- A clip name already on disk or already booked is refused when the job is created, and
  checked again the instant before the encoder opens the file.
- Two jobs cannot book the same receiver at overlapping times.
- Free space is estimated before scheduling and monitored while recording — an ingest that
  will not fit is refused, and one that runs the disk down is stopped safely and marked.

### Working without a card

The **SIMULATE** toggle swaps in simulated boards and a simulated recorder, so the queue,
the scheduler and the whole UI run on a machine with no DELTACAST hardware. It is opt-in
only — a missing board is reported as an error, never silently simulated — the window
carries a standing amber banner while it is on, every job and history row is marked
*(simulated)*, and the placeholder files it writes say in plain text what they are. It
cannot be switched while anything is queued or recording.

## Typing a timecode

Every timecode field in Emerald — in the EDL and in the Ingest Controller — is a **fixed
mask**. The shape never changes: the box always holds `HH:MM:SS:FF`, eleven characters, from
the moment it appears. There are no zeros to type that are already there, and no state in
which the field is not a timecode.

Typing **overwrites** the digit under the caret and moves on:

```
00:00:00:00   caret on the 4th digit, type 1   ->  00:01:00:00
00:00:00:00   Home, type 1 2 3 4 5 6 7 8       ->  12:34:56:78
12:34:56:78   two backspaces                   ->  12:34:56:00
```

- Backspace and Delete put a **zero back** rather than closing the gap, so the field cannot
  collapse or shift.
- Tab into a field and the caret lands on the first digit, so eight keystrokes replace the
  whole value. Clicking a colon snaps to the digit after it.
- Anything that is not a digit is ignored, so `10:20:30:04` typed or pasted in full lands
  correctly — the colons are dropped and re-inserted by the mask.
- A field is never empty. Where a value used to mean "no limit" by being blank, it now says
  so as `00:00:00:00` — in the EDL, an EOM equal to SOM.

The mask only enforces shape, and it **never corrects a number you typed**: `99:99:99:99`
stays exactly that and is reported as invalid at 25 fps, rather than being quietly rounded
into something you did not ask for. Range checking stays with the validator.

## The Monitor

**Monitor** in the nav bar is the whole plant on one page: what is happening now across the
top, and everything that has happened underneath it.

Before it, each module narrated into a list of its own that lasted until its window closed —
the EDL kept 500 lines, the playback deck 400, the Ingest Controller 500 and rebuilt them
whenever SIMULATE was toggled, and the capture deck kept **one**, in a label the next message
overwrote. None of it was written down. "What happened at 21:30" had no answer.

### The strip

Five segments, in the order the pictures travel: **capture → tidal lock → EDL queue → on air
→ ingest**. Each is fed by the module that owns it at the moment its state changes, not
reconstructed from the log — a status panel built by replaying lines is one that lies the
moment a line is missed, and a page that says a recording is running when it stopped ten
minutes ago is worse than no page.

### The record

One merged, ordered stream: time to the millisecond, the **house timecode that was on air**,
the source, the level, a correlation id, the media file, and the message.

| | |
|---|---|
| **Ordering** | Every line carries a sequence number taken with its place in the record. A wall clock cannot order two threads inside one millisecond, and an NTP step would put them in the wrong order entirely. |
| **Timecode** | Sampled ten times a second, not asked per line — `TimecodeService.TryGetCurrent` takes the clock's own lock and moves its anti-backstep mark, and calling it from the capture thread at log rate would contend with the deck's render timer. |
| **Correlation** | A recording, an EDL command and an ingest job each have an eight-character id, and everything they cause carries it. Double-click any line to follow that id — one click from "queued" to the frame leaving the transmitter. |
| **Filters** | Source, minimum level, free text over message/file/id/event, and an id box. **FOLLOW** keeps the newest line in view and turns itself off when you scroll back. |

### Where it is kept

`%APPDATA%\Emerald\logs\emerald-YYYYMMDD.jsonl`, one JSON object per line, rolled on the date
and at 32 MB, kept **14 days** and swept at start-up. Override with `logFolder` and
`logRetentionDays` in `settings.json`; a negative retention keeps everything.

The file is opened `FileShare.ReadWrite`, so it can be tailed while Emerald still holds it.

**Nothing writes to disk on a real-time thread.** The capture loop, the playout loop and the
scheduler all call the same `Write`, and the playout loop is between two frames going to a
transmitter — so `Write` stamps the line, appends it to a ring, hands it to a queue and
returns. A background thread owns the file. The queue is bounded; if the disk wedges, lines
are dropped and the drop is reported rather than the process being eaten.

The once-a-second playout progress is marked transient: it reaches the page and never the
file, or the record would be a heartbeat trace with the events buried in it.

### Per file, how many EDL

Queuing a command writes one line per media file it is made of, tagged `edl.media` and
carrying the full path and the command id; the moment a file reaches the transmitter writes
`playout.file` against the same id. Filter on a file name to see every message that used it.
Before this the command's file list existed only in the JSON behind **Copy JSON**, and the log
said "3 file(s) from D:\promo" — a count, not the files.

## The activity log

Each module keeps its own panel, and they are now **views of the same record**, filtered to
that module. Nothing is appended locally; the panel shows what came back out of the log. They
gained lines they never had — the EDL's panel now sees what the recorder and the playout
engine say, whichever window caused it. **Clear** empties the panel and never the record.

Lines are colour-coded by level: **blue** for informational, **green** for success,
**amber** for warnings, **red** for errors.

On Send, the log states what is about to run, then tracks the playout itself:

```
14:52:14  EDL 53faae8d is about to start                               blue
14:52:14      start     10:00:00:00   (in 19:07:45:13)
14:52:14      duration  00:00:20:00   (500 frames @ 25 fps)
14:52:14      end       10:00:20:00
14:52:14      path      RX3 @ 1. DELTA-12G-elp-h 20  ->  TX1 @ 0. DELTA-3G-elp-d 22
14:52:14      media     3 file(s) from D:\media\promo
14:52:14  Opening TX1 on board 0 at 1080p25...
14:52:14  Holding black on TX1 until 10:00:00:00 (in 19:07:45:13).                amber
...
10:00:20  Playout complete - 500 frames (00:00:20:00).                            green
```

`start` carries a countdown measured against the live realtime timecode, so you can see how
far away the cue is. With an empty duration, `duration` and `end` both read `open-ended`.
The end timecode is `start + duration` wrapped at 24 h, and it also ships in the payload as
`timing.endTimecode`.

## How the timecode stays accurate

Polling an HTTP endpoint 25 times a second would be wasteful and still jittery. Instead the
API is polled twice a second, and between polls the display free-wheels from a monotonic
stopwatch at the reported frame rate. Every poll re-seats the baseline, so the counter is
smooth frame-to-frame and drift never accumulates. If the API goes away the display blanks
to `--:--:--:--` and the status dot turns red rather than silently free-wheeling forever.

The frame rate the API reports also drives timecode validation — at 25 fps the app rejects
`…:25` as a frame number.

## Layout

```
src/Emerald.Core/
  Timecode.cs               non-drop SMPTE parse/format/arithmetic
  TimecodeService.cs        polled API link with local free-wheel
  TimecodeLink.cs           the one station clock, shared by every module
  TimecodeCalculation.cs    the SOM/EOM/duration convention, stated once
  TimecodeMask.cs           fixed-width HH:MM:SS:FF input mask
  ActivityLog.cs            the one record: capture to on air, ordered, on disk
  PipelineState.cs          what each stage is doing now, for the Monitor strip
  AppSettings.cs            %APPDATA%\Emerald\settings.json, migrated from EdlGenerator

src/Emerald.Deltacast/
  VideoMasterHD.cs          P/Invoke into VideoMasterHD.dll
  BoardInfo.cs              board + RX/TX channel descriptors
  BoardService.cs           board scan; never throws, reports SDK errors inline
  SdiOutput.cs              a configured, running TX channel; PushFrame paces playout
  VideoFormat.cs            frame rate -> SDI standard, interface and frame size
  RxLease.cs                who owns a receiver: preview yields, recording does not

src/Emerald.Video/
  DelayLine.cs              the tidal lock ring: raw frames, memory-mapped
  DelayTransmitter.cs       drains the ring to a transmitter, N frames behind
  DelayPacer.cs             what to do when the two ends of the delay disagree
  DelayFormats.cs           which receiver formats may be transmitted raw
  Ffmpeg.cs                 one answer per run for where ffmpeg lives
  FrameSource.cs            ffmpeg -> raw UYVY frames
  AudioSource.cs            ffmpeg -> a ring of 16-bit stereo samples, per language
  SdiCapture.cs             RX -> segmented MP4, video on stdin and audio on a named pipe

src/Emerald.Media/
  MediaScanner.cs           folder/file -> ordered playlist
  MediaCodec.cs             the codec, read from the container rather than probed
  StorageWarden.cs          keeps the store under a size, oldest out first
  MediaProbe.cs             ffprobe: duration, start timecode, stream layout
  MediaLibrary.cs           the capture store and what is in it

src/Emerald.EDL/
  PlayoutService.cs         cue on timecode, loop the playlist, honour the duration
  EdlCommand.cs             the EDL record, rendered as JSON in the UI
  AudioTrackRow.cs          one language row - a bed file, or a stream of the media
  EdlWindow.xaml(.cs)       the EDL UI

src/Emerald.Ingest/
  Models/                   IngestJob, IngestRecording, the status state machine
  Services/
    IngestControllerService.cs  validate, create, persist, recover - the way in
    IngestSchedulerService.cs   the queue, and when each job rolls
    IngestRecorder.cs           adapter onto SdiCapture, plus the simulated one
    IngestMediaRegistrar.cs     verify the file, register it with the store
    IngestStore.cs              the SQLite job store
    IngestHardware.cs           board enumeration, real and simulated
    IngestClock.cs              the station clock, as the scheduler sees it
    ClipNameService.cs          the naming convention, in one replaceable place
    DiskSpace.cs                will it fit, and is there still room
  ViewModels/IngestRows.cs      flattened rows for the queue and history lists
  Views/                        the Ingest Controller and history windows

src/Emerald.LiveEdit/
  LiveEditWindow.xaml(.cs)  viewer, transport and timeline over the capture store

src/Emerald.App/
  App.xaml(.cs)             the application, the theme, single-instance and --edl/--ingest
  ShellWindow.xaml(.cs)     the capture deck: preview, recorder, and the module buttons
  PlaybackWindow.xaml(.cs)  the playback deck: transmit, preview, tidal lock
  RxPreview.cs              RX -> a decimated BGRA thumbnail, ~12 fps

build.bat                   build only
run.bat / run-edl.bat / run-ingest.bat
                            build, then open the deck, the EDL or the Ingest Controller
PROTOCOL.md                 field-by-field description of the EDL record JSON
```

The theme lives in `App.xaml`'s `Application.Resources` and nowhere else. Because the modules
are windows in the same process, WPF resource lookup walks up to the application, so
`{StaticResource Bg}` resolves inside `Emerald.EDL` and `Emerald.LiveEdit` with no assembly
reference and no `pack://` URI. The XAML *designer* will not preview those brushes inside the
library projects; at runtime they are correct.

Settings (API URL, last board/ports, last media path, audio tracks) persist to
`%APPDATA%\Emerald\settings.json` and are restored on launch.

## Recording (RX capture)

While a message is on air, the **capture** board/port is recorded to the Recording folder as
`capture_YYYY-MM-DD_HH-MM-SS.mp4`, in **2-minute segments**, with video and audio muxed
together — which is the point, since the reason to record is to check lip-sync against what
was transmitted. Recording starts when playout starts and stops when the message ends; an
empty folder means no recording.

Verified on the loop: a 15-second message came back as **374 frames of 1080p25 h264 with
48 kHz stereo AAC**, video and audio ending within 20 ms of each other, and the audio
measurably present (`mean_volume −24.1 dB`, against −91 dB for silence).

**dCARE must be closed.** An RX channel can only be opened by one process, so if dCARE is
watching that input the app cannot record it — you get a clear message in the log rather
than a silent failure.

### Keeping the store to a size

**Storage limit** on the capture deck caps how large the store may grow — 100 GB, 200 GB,
500 GB, 1 TB, 2 TB, or **No limit**, which is the default. Past the limit, the oldest
recordings are deleted to make room for the newest: a rolling window rather than a recording
that stops when the disk fills.

That matters because the alternative is worse. A recording that runs out of disk does not stop
— the encoder dies and the capture loop carries on counting frames it is not writing, which is
exactly what happened here for 38 hours before anyone noticed.

This is the only thing in Emerald that deletes an operator's recordings, so what it will not do
is as deliberate as what it will:

| | |
|---|---|
| **Never without a limit** | Off by default. Nothing is deleted until a limit is chosen. |
| **Oldest first** | Always, and both halves of a recording go together — a proxy without its master cannot be edited, and a master without its proxy is not listed. |
| **Never the open segment** | Anything written in the last two minutes is left alone, so the file the recorder has open is never the one that goes. |
| **Never down to nothing** | At least three recordings survive whatever the limit says. A limit below one segment would otherwise delete each file as it was finished and leave the store permanently empty. |
| **Never quietly** | Every sweep writes what it deleted and what the store now holds to the activity record. |

The store size is shown under the field, and it is checked every ten seconds while recording
rather than on a timer of its own — the store only grows while something is writing to it.

### The master is DNxHD 145, and how to tell

Every recording is written twice from one pass over the receiver: a half-size H.264 proxy
under `low\`, and a full-raster **DNxHD 145** master under `high\`. The master used to be
ProRes 422.

**The number is the codec.** DNxHD is defined as a table of frame size, rate and bitrate
combinations rather than as a quality setting, so 145 Mbps at 8-bit 4:2:2 *is* the tier — and
anything not in the table is refused outright. Tested against this ffmpeg at 1920×1080p, 145
is accepted at 24, 25, 30, 50 and 60. No `-profile:v` is given: naming one would put the
encoder into DNxHR, which is a different codec with a different four-character code.

**It does not take every raster.** 1920×1080, 1440×1080 and 1280×720 are in the table;
standard definition is not, and a receiver on 720×576 has its recording refused. That would
take the proxy with it, since one ffmpeg writes both — so the raster is checked before the
encoder starts, and the refusal arrives as a sentence naming what is supported and what
arrived rather than as a process that dies a few frames in.

**About one and a half times the disk**: 145 Mbps against ProRes 422's measured 89 on this
plant's feed.

#### Checking whether a clip is converted

Every clip in the capture deck and in Live Edit names its **master's codec**, read from the
file itself — `ProRes 422` for anything recorded before the change, `DNxHD` for anything
after. A clip whose master is missing says so rather than guessing.

The codec is read by walking the container to the video sample description and taking the
four-character code (`apcn` for ProRes 422, `AVdn` for DNxHD), not by probing:

```
ffprobe          195 ms per file   →  over three minutes for a store of a thousand
reading the atom   8 ms per file   →  under ten seconds, and cached after the first pass
```

That difference is why the label can be on every row rather than on request. Anything needing
duration, raster or stream layout still goes through `ffprobe`, which is worth its cost once
and not a thousand times.

The Monitor also writes a tally of the whole store whenever the split changes —
`Store: 412 DNxHD, 1661 ProRes 422.` — so progress through a library is one line in the
record rather than a count done by eye.

### Every language on the wire is recorded

SDI carries four groups of four channels — sixteen channels, eight stereo pairs — and the EDL
puts a language on each. Recording took **channels 1-2 only**, so a return feed of a
four-language message came back with three of them missing.

It now records **one track per stereo pair**, in wire order, named for the channels it came
off:

```
index=1  audio  DISPOSITION:default=1  TAG:name=Original 1 (CH 1-2)
index=2  audio  DISPOSITION:default=0  TAG:name=Original 2 (CH 3-4)
index=3  audio  DISPOSITION:default=0  TAG:name=Original 3 (CH 5-6)
```

How many pairs is worked out from the wire, by reading the first slots for their audio before
a frame is recorded — the encoder's channel count is fixed once ffmpeg starts, so it has to be
settled first. A feed on channels 1-2 is encoded **exactly as it always was**: straight through
as stereo, no filter graph, no renaming. Anything more is split with `pan`, one stereo stream
per pair, duplicated for the master and the proxy.

Asking a card for groups it does not have could be refused outright rather than answered with
what it does have, so a refusal steps the request down a group at a time and remembers what
worked. Losing the audio on 1-2 because nothing was on 13-16 would be far worse than not
seeing the extra pairs.

### Adding audio to a recording

The **Added tracks** list under the meters records audio files alongside the receiver's own
sound — a language bed, a commentary, a clean mix. Each becomes a further track in the file,
after every embedded pair, and a track shorter than the recording repeats to fill it. The same
arrangement the Ingest Controller uses.

Three things about the implementation are worth recording, because each one cost real time
and would be easy to reintroduce:

- **Two ffmpeg inputs, one stdin.** Video goes in on stdin; audio goes through a Windows
  named pipe. There is no other way to get both into a single muxed file from one process.
- **ffmpeg opens its inputs in order**, and will not touch the audio pipe until stdin has
  produced enough data to identify input 0. Waiting for the pipe connection before writing
  video deadlocks on the first frame.
- **Video and audio must be written by separate threads.** A 1080p frame is far larger than
  a pipe buffer, so a video write blocks until ffmpeg drains it — and ffmpeg will not drain
  more video until it has the matching audio to interleave. Writing both from one thread
  deadlocks after about four frames, which is exactly what happened first time round. The
  slot loop now hands frames to bounded queues and drops frames rather than stalling the
  receiver if the encoder ever falls behind.
