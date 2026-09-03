# Emerald

A Windows desktop application (C# / WPF, .NET 8) for SDI playout, recording and review on
DELTACAST hardware. It is self-contained: no server, no network transport, nothing to
connect to.

Emerald opens on a **shell** with a live confidence monitor on an SDI receiver and two ways
in:

- **EDL Generator** — composes an EDL playout command and plays it out of a DELTACAST SDI
  output, cued to house timecode, recording the return feed while it is on air.
- **Live Edit** — the editing workspace over what has been captured. Layout only for now;
  trimming and rendering are not built.

Both open as windows in the same process, which is what lets them share the hardware (see
*One receiver, two claimants* below).

## The projects

```
Emerald.sln
├─ src\Emerald.App          the shell: RX preview, and the way in to the modules   [WinExe]
├─ src\Emerald.EDL          the EDL generator window and the playout engine
├─ src\Emerald.LiveEdit     the editing workspace (layout only so far)
├─ src\Emerald.Media        media management: scan, probe, and the capture store
├─ src\Emerald.Video        ffmpeg: decode, audio beds, and RX recording
├─ src\Emerald.Deltacast    VideoMaster interop, boards, TX output, RX arbitration
├─ src\Emerald.Core         timecode, the timecode API client, settings
└─ tests\Emerald.Core.Tests
```

References run one way only, `Core ← Deltacast ← Video ← Media`, with the three UI projects
on top and `Emerald.App` referencing everything. Nothing below `Emerald.App` references WPF
except the two module windows, so the whole engine can be exercised without a UI.

Two placements are worth explaining, because the obvious reading is the wrong one:

- **`VideoFormat` is in Deltacast, not Video.** It maps a frame rate to an SDI standard,
  interface and frame size. That is an SDI table which ffmpeg happens to consume.
- **`SdiCapture` is in Video, not Deltacast.** It is the recording engine; it reads a
  receiver through Deltacast, which is precisely why `Video → Deltacast` exists.

Every project is **x64 only** — `VideoMasterHD.dll` in `System32` is 64-bit, so every
assembly loaded into the process must match. The solution maps `Any CPU → x64` for each
project; a new project added without `<Platforms>x64</Platforms>` will fail the build.

## Build and run

Double-click **`run.bat`**, or from a terminal:

```
run.bat                 build (Release) and launch
run.bat Debug           build Debug and launch
run.bat Release -n      skip the build, just launch what is already built

build.bat               build only (Release)
build.bat Debug         build Debug
build.bat Release -r    force a package restore first
```

`run.bat` closes any running instance before rebuilding — the app holds a lock on its own
executable, and on the receiver, so a rebuild would otherwise fail. Both scripts exit
non-zero on failure, so they drop straight into CI or a scheduled task, and they pause
before closing only when double-clicked, so the output stays readable.

The equivalent by hand:

```powershell
dotnet build Emerald.sln -c Release
dotnet test  Emerald.sln -c Release
.\src\Emerald.App\bin\x64\Release\net8.0-windows\win-x64\Emerald.App.exe
```

Building the solution sets `Platform=x64` explicitly, which MSBuild folds into the output
path — hence the `bin\x64\` segment. Building `Emerald.App.csproj` on its own lands in
`bin\Release\` instead.

To produce a redistributable folder:

```powershell
dotnet publish src\Emerald.App\Emerald.App.csproj -c Release -r win-x64 --self-contained false
```

Requirements: .NET 8 desktop runtime, and the DELTACAST VideoMaster driver (the app loads
`VideoMasterHD.dll` from `System32`). The process is pinned to **x64** because that DLL is
64-bit only.

## One receiver, two claimants

A DELTACAST receiver allows **exactly one open handle**. That is already why dCARE must be
closed before Emerald can record; inside Emerald it means the shell's preview and the EDL's
recorder cannot both hold the same input.

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
than executables of their own.

## The capture store

`Emerald.Media/MediaLibrary.cs` owns where recordings live — `media\` beside the solution by
default, overridable in the EDL's **Recording folder**. Recordings land there full
resolution with their audio, exactly as they came off the wire, and Live Edit lists that same
folder. Nothing is copied or transcoded on the way in.

Settings live in `%APPDATA%\Emerald\settings.json`. On first run Emerald reads
`%APPDATA%\EdlGenerator\settings.json` if its own is absent, so board, port, media source and
audio tracks carry across from the pre-Emerald app.

## Fields

| Field | Notes |
|---|---|
| **Capture Board** / **Playback Board** | Chosen **independently** — the RX can be on board 0 and the TX on board 1, or the other way round. Rendered as `0. DELTA-3G-elp-d 22`, matching the DELTACAST index/model convention. **Rescan** re-enumerates without restarting. |
| **Capture Port** | `RX0`…`RXn` on the capture board, where *n* comes from `VHD_CORE_BP_NB_RXCHANNELS`. |
| **Playback Port** | `TX0`…`TXn` on the playback board, from `VHD_CORE_BP_NB_TXCHANNELS`. A board with no TX channels (e.g. `DELTA-12G-elp-h 20`, which is 8 RX / 0 TX) shows an amber warning and blocks Send. On startup each side defaults to the first board that can actually do that job. |
| **Timecode API** | Default `http://10.0.0.31:8888/api/timecode`. **Apply** switches source at runtime. |
| **Start Timecode** | On-air time on the house clock. `HH:MM:SS:FF`, masked (see below) and validated against the live frame rate. **Now** stamps the current realtime timecode. |
| **SOM** (start of message) | A **delay on the start timecode**, masked. Start `06:20:00:00` with SOM `00:01:00:00` puts video on TX at `06:21:00:00`; until then the output holds the post-play fill. It is **not** a position inside the media file — nothing seeks, and it is never checked against the media's length or embedded timecode. |
| **EOM** (end of message) | The matching off-air offset. `EOM − SOM` is the duration. Empty means no fixed duration: the media loops until stopped. Must be later than SOM. |
| **Duration** | **Read-only** — `EOM − SOM`. Shows `open-ended` when EOM is empty. |
| **Stop Time** | **Read-only** — on-air time + duration (i.e. `Start + EOM`), wrapped at 24 h. Start `06:20:00:00`, SOM `00:01:00:00`, EOM `00:11:00:00` gives on air `06:21:00:00` and stop `06:31:00:00`. |
| **Audio Tracks** | A list of language beds, each with its own source. Only **one is on air at a time**, on channels 1-2. The **default** radio picks which starts; **take** switches live mid-message. Each has its own **+ / −** trim at **10 ms per tick** (±500 ms) and a **0** reset — independent per language, so switching preserves each one's delay. Up to 8 tracks. |
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
| **CUED** | Next up and holding — the countdown to its start timecode is shown. |
| **PLAYING** | On air, with elapsed time counting up. |
| **DONE** / **STOPPED** / **FAILED** | Finished. **Clear finished** removes them. |

Each row carries start, duration, stop time, TX channel and post-play setting.

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
- **SOM/EOM.** SOM delays the on-air moment relative to the Start Timecode; EOM is the
  matching off-air offset, so the duration is `EOM − SOM`. The output opens at the start
  timecode and holds the post-play fill through the SOM delay, then cuts to the media,
  which plays from its top and loops to fill the duration. Neither mark seeks into the
  media, and neither is checked against the media's length or its embedded timecode — set
  them freely. When media is selected, ffprobe still reports its length, stream layout and
  embedded timecode under the drop zone
  (`length 00:04:28:06, has audio, media TC starts 01:00:00:00`), for information only.
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
| Video + one track | Video with that track |
| Video + several tracks | Video with **one language at a time** on channels 1-2 |

At least one of the two is required; either alone is a valid message.

### Multiple languages

Every track is decoded and advanced **every frame**, whether or not it is the one on air.
That is what makes **take** instant: switching is a choice of buffer, not a restart. A track
left un-advanced would stall its decoder against its ring buffer and sit at the wrong
position, so switching to it would be neither immediate nor sample-accurate.

The cost is one ffmpeg process and about 576 KB of ring buffer per language, which is
nothing for the eight tracks SDI can carry.

Each track loops its own files to fill the duration, so a short bed under a long message
keeps sound on air throughout.

### Adjusting on air

Each language has **its own** offset, adjustable **while a message is playing**, at **10 ms
per tick** up to ±500 ms. Picture is never touched, and switching languages preserves
whatever each was trimmed to.

The offset is never passed to ffmpeg. Audio is always decoded from the natural start of the
file into a rolling three-second buffer, and the offset simply moves **where the play loop
reads from** — re-pointing an index, not restarting a process. The loop re-reads the offset
every frame, so a nudge lands on the next frame:

- **positive** reads back in time, so picture leads (audio delayed);
- **negative** reads ahead, so sound leads (audio advanced).

The buffer carries both history and lookahead, so neither direction needs a re-seek and
there is no gap or click on adjustment. Verified on the card: 90 offset changes during a
20-second message, and separately 6 language switches during another, both still outputting
exactly 500 frames with no interruption to picture.

- Audio is decoded to 48 kHz 16-bit stereo, de-interleaved, and embedded into the SDI as
  **group 1, channels 1–2**. Every frame rate the app outputs divides 48000 exactly, so a
  frame is a whole number of samples and audio cannot drift against picture.
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

## Typing a timecode

Both timecode fields are masked — type digits only and the colons appear as you go:

```
1 0 2 0 3 0 0 4   ->  10:20:30:04
0 0 0 0 1 5       ->  00:00:15     ->  00:00:15:00 on leaving the field
```

- Anything that is not a digit is ignored, so `10:20:30:04` typed or pasted in full still
  lands correctly — the colons you type are simply dropped and re-inserted by the mask.
- Eight digits is the ceiling; further keystrokes are ignored rather than shifting the value.
- With nothing selected, typing **overwrites** from the caret rather than inserting. Click
  into a filled field, type eight digits, and you have replaced it.
- Leaving a partially typed field pads it with zeros, so `10` becomes `10:00:00:00`.
  **This pads to the right** — `20` in the duration field means twenty *hours*, not twenty
  seconds. Type `00002000` for twenty seconds.
- Clearing the duration field completely leaves it empty, which still means "loop
  indefinitely" — blur padding deliberately skips an empty field.

The mask only enforces shape. Range checking stays with the validator, so `10:20:30:78`
is accepted by the mask and then rejected as invalid at 25 fps.

## The activity log

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
  AppSettings.cs            %APPDATA%\Emerald\settings.json, migrated from EdlGenerator

src/Emerald.Deltacast/
  VideoMasterHD.cs          P/Invoke into VideoMasterHD.dll
  BoardInfo.cs              board + RX/TX channel descriptors
  BoardService.cs           board scan; never throws, reports SDK errors inline
  SdiOutput.cs              a configured, running TX channel; PushFrame paces playout
  VideoFormat.cs            frame rate -> SDI standard, interface and frame size
  RxLease.cs                who owns a receiver: preview yields, recording does not

src/Emerald.Video/
  Ffmpeg.cs                 one answer per run for where ffmpeg lives
  FrameSource.cs            ffmpeg -> raw UYVY frames
  AudioSource.cs            ffmpeg -> a ring of 16-bit stereo samples, per language
  SdiCapture.cs             RX -> segmented MP4, video on stdin and audio on a named pipe

src/Emerald.Media/
  MediaScanner.cs           folder/file -> ordered playlist
  MediaProbe.cs             ffprobe: duration, start timecode, stream layout
  MediaLibrary.cs           the capture store and what is in it

src/Emerald.EDL/
  PlayoutService.cs         cue on timecode, loop the playlist, honour the duration
  EdlCommand.cs             the EDL record, rendered as JSON in the UI
  AudioTrackRow.cs          one language row in the Audio Tracks panel
  TimecodeMask.cs           digits-only HH:MM:SS:FF input mask
  EdlWindow.xaml(.cs)       the EDL UI

src/Emerald.LiveEdit/
  LiveEditWindow.xaml(.cs)  viewer, transport and timeline over the capture store

src/Emerald.App/
  App.xaml(.cs)             the application, and the whole theme
  ShellWindow.xaml(.cs)     preview, board picker, and the module buttons
  RxPreview.cs              RX -> a decimated BGRA thumbnail, ~12 fps

build.bat / run.bat         build, and build-then-launch
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
