# 5. Video and media processing

Line references are to commit `dd8e4a7`. Unless stated, nothing here was measured on hardware: figures are read or calculated from the code.

## Inputs and outputs

| Direction | Source or destination | Format |
|---|---|---|
| In | SDI receiver | UYVY 8-bit 4:2:2 plus embedded audio |
| In | Media files, through ffmpeg | Anything ffmpeg reads, conformed to the output format |
| In | Audio bed files, through ffmpeg | Playout and cues: 48 kHz stereo after conversion. Recording: encoded at the file's own channel count, and its own rate unless a non-48 kHz output rate is chosen. |
| Out | SDI transmitter | 1080p UYVY plus embedded audio |
| Out | `low\` | H.264 and AAC in MP4 |
| Out | `high\` | DNxHD 145 and AAC in MOV |
| Out | Speakers | 48 kHz stereo, monitoring only |

**There is no network streaming.** No RTMP, SRT, HLS, NDI or similar code exists. "Streaming" in this project means SDI stream handles on the card. The only network output is the [read-only monitoring page](07_API_AND_COMMUNICATION.md), which carries status text and no media.

## Recording flowchart

```mermaid
flowchart TD
    A["SdiCapture.Start"] --> B["Create the recording's low and high folders"]
    B --> C["RxLease.Acquire: displaces preview"]
    C -- "RxBusyException" --> E0["Fail: receiver busy, or cannot open"]
    C --> D["VHD_OpenBoardHandle, VHD_OpenStreamHandle JOINED"]
    D -- "SDK error" --> E0
    D --> E{"Signal within about 2 s?"}
    E -- "No" --> E1["Fail: no signal"]
    E -- "Yes" --> F["CaptureFormat.FromStandard"]
    F --> G{"Output supports raster?"}
    G -- "No" --> E2["Fail: raster refused"]
    G -- "Yes" --> H["Raise FormatDetected"]
    H -- "Handler throws" --> E0
    H --> I["Set properties, VHD_StartStream"]
    I --> J["DetectPairs: read and discard 2 s of slots"]
    J --> K["Encoder.Open: start ffmpeg"]
    K --> S{"Stop or frame limit?"}
    S -- "No" --> Q{"Midnight frame?"}
    Q -- "Yes" --> R["Swap to next day's encoder, if it opened"]
    Q -- "No" --> L["Lock slot"]
    R --> L
    L -- "Lock fails" --> S
    L --> M["Copy video, OfferVideo"]
    M --> N["Read audio, interleave, OfferAudio"]
    N --> O["Raise Frame for the ring"]
    O --> P["Unlock slot"]
    P --> X{"Once a second: encoder exited?"}
    X -- "No" --> S
    X -- "Yes" --> T["Drain, Finish: close ffmpeg"]
    S -- "Yes" --> T
    T --> U["Close stream and board, dispose lease"]
    E0 --> U
    E1 --> U
    E2 --> U
    U --> V["Raise Finished"]
```

## Capture in order

`SdiCapture.Run`, `src/Emerald.Video/SdiCapture.cs:194`:

1. **Folders** (`:215`). Created by hand, because ffmpeg's segment muxer does not create directories.
2. **Lease** (`:231`). `RxLease.Acquire(board, rx, "recording")`.
3. **Open** (`:239`). Board and stream with `VHD_SDI_STPROC_JOINED`, so slots carry audio. Error 18 becomes "already open in another application - close dCARE".
4. **Detect** (`:262`). Up to 20 tries, 100 ms apart.
5. **Format** (`:277`, table at `:1098`). Unknown standards are assumed to be 1920x1080.
6. **Raster check** (`:284`). DNxHD accepts 1920x1080, 1440x1080 and 1280x720, so PAL and NTSC are refused.
7. **`FormatDetected`** (`:295`), before any frame.
8. **Properties** (`:298`). YUV422_8, slaved transfer, queue depth 4, 2000 ms IO timeout.
9. **Audio pairs** (`:321`). `DetectPairs` reads slots for a full 2 seconds and discards them. The count fixes the encoder's channel layout.
10. **Encoder** (`:345`).
11. **Slot loop** (`:392`):
    - Lock a slot. On failure, count it and `continue`. If no frame has arrived more than 3 seconds after the loop began, `Fail` is called once ("no slots arriving"). This sets `LastError`, so the recording reports as failed even if frames arrive later. After the first frame, lock failures are silent.
    - Copy the video into a new array (`:471`), and call `encoder.OfferVideo`. A refusal is a dropped frame.
    - Convert a preview if due, and raise `PreviewFrame`.
    - If the pipe is attached or anything subscribes to `Frame` or `Audio`: read and interleave audio, raise `Audio` with the reader, then substitute silence if the slot was empty. `OfferAudio` runs only once ffmpeg has attached the pipe. Slots before that are paid back later, up to 3 a slot, by repeating the current chunk.
    - Raise `Frame` with video, audio and frame number.
    - Unlock.
    - Once a second: update `PipelineState` and check `Ffmpeg.HasExited`.
    - At most once per 5 s: log drops and add a `DropMark`. Every 30 s: log a `REC` line.
12. **Finish** (`:646`). Drain, report the closing lip-sync figure, then `finally` closes everything and raises `Finished` last.

### Encoder pipes

`SdiCapture.Encoder` (`:780`) is one ffmpeg with two inputs:

| Input | Transport | Writer thread | Queue |
|---|---|---|---|
| Video | ffmpeg stdin, raw UYVY | `capture video writer` | 8 frames |
| Audio | Named pipe `\\.\pipe\edlcap_<pid>_<guid>`, raw PCM | `capture audio writer` | 200 chunks |

**Why two threads and two transports:**

- A process has one stdin.
- ffmpeg opens input 1 only after identifying input 0, so waiting for the pipe connection first would deadlock.
- ffmpeg will not drain more video until it has matching audio, so one writer thread would also deadlock.

**Lip sync.** The pipes carry no timestamps. Alignment is only "how many of each were written". `CaptureSync.AudioChunksDue` (`src/Emerald.Core/CaptureSync.cs`) allows audio only up to the video count, catching up by at most 3 chunks a slot. So a dropped picture drops its sound.

### Audio capture

`SdiAudioReader` asks the card for up to 16 mono channels, at 48 kHz in 16-bit samples, and interleaves them. A failed read steps the request down from 16 to 12, 8, 4, then 2. The encoder gets `pairs × 2` channels, with each pair split into its own AAC track by the filter graph when there is more than one. See [hardware](06_HARDWARE_INTEGRATION.md#audio-on-the-wire).

## ffmpeg commands

All paths go through `ProcessStartInfo.ArgumentList`, so there is no shell quoting. `Ffmpeg.Locate` (`src/Emerald.Video/Ffmpeg.cs:16`) searches the configured path, then beside the executable, then `PATH`, then `C:\ffmpeg\bin`, then `C:\Program Files\ffmpeg\bin`.

### Recording

Built by `RecordingProfile.EncoderArguments`, `src/Emerald.Video/RecordingProfile.cs:274`.

Inputs:

```
-hide_banner -loglevel error -nostdin
-f rawvideo -pix_fmt uyvy422 -s {W}x{H} -r {FrameRate} -i pipe:0
-f s16le -ar {inputSampleRate} -ac {pairs*2} -i \\.\pipe\{pipeName}
-stream_loop -1 -i {bedPath}          (once per added bed)
```

Filter graph, only if needed (`:515`):

```
-filter_complex {chains joined with ;}
[1:a]pan=stereo|c0=c{p*2}|c1=c{p*2+1},asplit=2[p{p}o0][p{p}o1]     (each pair, when pairs > 1)
[{i+2}:a]{bedFilters},asplit=2[b{i}o0][b{i}o1]                      (each adjusted bed)
```

Bed filters (`:555`): `adelay={ms}:all=1` for a positive offset, `atrim=start={s},asetpts=N/SR/TB` for a negative one, `volume={gain}dB`, or `anull`.

Each output, proxy first:

```
-map 0:v:0
-map 1:a:0  or  -map [p{p}o{out}]
-map {i+2}:a:0  or  -map [b{i}o{out}]
-c:v {libx264|dnxhd}
-b:v 145M                                   (master)
-preset veryfast -profile:v high -bf 0 -g {max(1,rate/2)} -sc_threshold 0     (libx264)
-b:v {k}k -maxrate {k}k -bufsize {k*2}k     (libx264, only if a bitrate is chosen)
-pix_fmt {yuv420p|yuv422p}
-c:a aac -b:a {AudioBitrateKbps}k
-ar {AudioSampleRate}                       (only if not 48000)
-metadata:s:a:{p} title=...   -disposition:a:{p} {default|0}          (each pair, only if pairs > 1 or beds exist)
-metadata:s:a:{pairs+i} title={label}   -disposition:a:{pairs+i} 0    (each bed)
-shortest                                   (only if beds exist)
-timecode {HH:MM:SS:FF}                     (single file with a start timecode)
```

Then one of three destinations:

```
-f segment -segment_time {s} -segment_format {mp4|mov} -reset_timestamps 1
    -segment_start_number {start} {root}\{low|high}\{folder}\{prefix}_{MMddyyHHmm}_%d.{ext}

-f {mp4|mov} {root}\{low|high}\{prefix}.{ext}       (single file, no -y, refuses to overwrite)

-f segment -segment_time {s} -segment_format {mp4|mov} -reset_timestamps 1
    -strftime 1 {root}\{low|high}\{prefix}_%Y-%m-%d_%H-%M-%S.{ext}     (legacy: no Names given; only tests reach it)
```

Worked example: 1080p25, one audio pair, default settings.

```
ffmpeg -hide_banner -loglevel error -nostdin
 -f rawvideo -pix_fmt uyvy422 -s 1920x1080 -r 25 -i pipe:0
 -f s16le -ar 48000 -ac 2 -i \\.\pipe\edlcap_<pid>_<guid>
 -map 0:v:0 -map 1:a:0 -c:v libx264 -preset veryfast -profile:v high -bf 0 -g 12 -sc_threshold 0
   -pix_fmt yuv420p -c:a aac -b:a 192k
   -f segment -segment_time 30 -segment_format mp4 -reset_timestamps 1 -segment_start_number 1
   <root>\low\<folder>\media_MMddyyHHmm_%d.mp4
 -map 0:v:0 -map 1:a:0 -c:v dnxhd -b:v 145M -pix_fmt yuv422p -c:a aac -b:a 192k
   -f segment -segment_time 30 -segment_format mov -reset_timestamps 1 -segment_start_number 1
   <root>\high\<folder>\media_MMddyyHHmm_%d.mov
```

Why these flags:

| Flag | Reason in the code |
|---|---|
| `-r` on raw input | Raw video has no timestamps |
| `-g rate/2`, `-sc_threshold 0` | A keyframe every half second so the proxy seeks. The comment records an 11-second gap before this. |
| `-bf 0` | No frame reordering, for backward stepping |
| `veryfast` | Keeps CABAC and motion search, unlike `ultrafast` |
| `pan`, not `channelsplit` | The channel layout of raw PCM is a guess |
| `asplit` | A filter output can be mapped only once |
| `-reset_timestamps 1` | Each segment starts at zero |
| No `-strftime` on segments | Under it `%d` means day of month, so every segment overwrites one file (`src/Emerald.Core/RecordingSequence.cs:27`) |

### Decoding for air

`FrameSource.Open`, `src/Emerald.Video/FrameSource.cs:67`:

```
-hide_banner -loglevel error -nostdin
[-accurate_seek -ss {seconds:F3}]
-i {mediaPath} -an
-vf [setpts=PTS/{speed},]scale={W}:{H}:force_original_aspect_ratio=decrease,pad={W}:{H}:(ow-iw)/2:(oh-ih)/2:color=black,fps={rate}
-pix_fmt uyvy422 -f rawvideo pipe:1
```

This letterboxes, conforms the rate and drops audio. There is no deinterlacer in the chain.

`AudioSource.Open`, `src/Emerald.Video/AudioSource.cs:96`:

```
-hide_banner -loglevel error -nostdin
[-ss {seconds:F3}]
-i {mediaPath}
[-af {atempo=2,...|atempo=0.5,...}atempo={rest}]          (chained above 2 or below 0.5)
[-map 0:a:{streamIndex}]
-vn -ar 48000 -ac 2 -f s16le pipe:1
```

`AudioSource` keeps a rolling 3-second buffer per channel and decodes up to 1.5 seconds ahead. `ReadFrame` takes a position **in samples** and returns silence for anything not decoded, so a live offset change needs no ffmpeg restart.

### Other commands

| Purpose | Location | Arguments |
|---|---|---|
| Probe | `src/Emerald.Media/MediaProbe.cs:170` | `ffprobe -v error -print_format json -show_format -show_streams {path}` |
| Export | `src/Emerald.Media/RangeExport.cs:165` | `ffmpeg -hide_banner -loglevel error -nostdin -y -f concat -safe 0 -i {list} -ss {from} -t {length} -c copy -avoid_negative_ts make_zero {output}` |
| Export preview | `src/Emerald.Media/RangeExport.cs:299` | `ffmpeg -hide_banner -loglevel error -nostdin -y -i {export} -c:v libx264 -preset veryfast -profile:v high -crf 20 -pix_fmt yuv420p -bf 0 -g {max(1,rate/2)} -sc_threshold 0 -c:a aac -b:a 192k {name}.preview.mp4` |
| Waveform | `src/Emerald.Media/Waveform.cs:123` | `ffmpeg -v error -nostdin -i {path} -vn -ac 1 -ar 8000 -f s16le -acodec pcm_s16le -` |
| Thumbnail | `src/Emerald.App/ClipThumbnails.cs:73` | `ffmpeg -hide_banner -loglevel error -nostdin -y -ss 1 -i {clip} -frames:v 1 -vf scale=294:-1 {cache}.png` |

## Recording profile

`src/Emerald.Video/RecordingProfile.cs:99`. One ffmpeg writes both outputs in one pass.

| | `LowRes`, proxy | `HighRes`, master |
|---|---|---|
| Folder and container | `low`, MP4 | `high`, MOV |
| Video | libx264, yuv420p, **full raster** | dnxhd, 145 Mb/s, yuv422p |
| Audio | AAC | AAC |

Defaults: automatic proxy bitrate, 192 kb/s audio, 48 kHz, 30-second segments (`:160`). Choices are limited to lists (`:166`), so an unlisted value in settings falls back to the default and never reaches ffmpeg:

| Setting | Offered values |
|---|---|
| Proxy bitrate | Auto (0), 1500, 3000, 5000, 8000 kb/s |
| Audio bitrate | 128, 192, 256, 320 kb/s |
| Sample rate | 48000, 44100, 32000 Hz |
| Segment length | 10, 15, 30, 60, 120, 300, 600 s |

A title of `capture` silently becomes `media` (`RecordingSequence.cs:65`). `SanitisePrefix` replaces invalid characters and `%`, and caps the prefix at 60 characters.

## File segmentation

- Each recording has a folder stamped `yyyy-MM-dd_HH-mm-ss` when Record was pressed.
- Files are `<prefix>_<MMddyyHHmm>_<N>`, where N starts at 1 per folder and is not padded.
- A segment's position on the timeline is the folder's start plus the length of the segments before it, worked out by `MediaLibrary.Accumulate` (`src/Emerald.Media/MediaLibrary.cs:268`).

An open segment has no index until it closes, so the player cannot read it. `HeldPicture` covers that gap from the ring.

## Midnight rollover

Segmented recordings only (`src/Emerald.Video/SdiCapture.cs:375`, `:395`).

- `RecordingSequence.FramesToNextDay` computes the frame at which folder start plus frames recorded reaches 00:00:00.
- Ten seconds ahead, `OpenForDay` starts the next encoder on a task, because starting ffmpeg on the slot loop would cost frames.
- At that frame, `encoder` swaps, `old.Finish` runs on a task, and `Rolled` is raised.
- The new folder is `yyyy-MM-dd_00-00-00` and numbering restarts at 1.
- The receiver, frame count and rings carry on.
- If `OpenForDay` fails, `capture.rollfailed` is logged and the recording stays in the old folder until the next midnight.

The count starts from the folder stamp, while frame 0 comes at least 2 seconds later because of `DetectPairs`. So the new folder's "00:00:00" picture is captured at least 2 seconds after real midnight. The timeline stays self-consistent but sits behind the station clock. This is a [verified issue](11_TECHNICAL_ASSESSMENT.md).

## Delay line

A ring of raw frames in a memory-mapped file: `src/Emerald.Video/DelayLine.cs`.

- **Location:** `tidalLockRingFolder`, default `%LOCALAPPDATA%\Emerald\delay`, named `tidal-<pid>-<guid>.ring`.
- **Layout:** 4096-byte header with magic `EMLDLY01`, then slots at `header + seq % SlotCount * stride`. Each slot has a 64-byte header, video and audio.
- **Audio capacity:** four frames of stereo.
- **Free-space guard:** requires size plus 2,000,000,000 bytes.

Sizes at 1080p25 (stride 4,182,016 bytes, 50 guard slots):

| Holds | Slots | File size |
|---|---|---|
| 60 s | 1,550 | 6.04 GiB |
| 120 s | 3,050 | 11.9 GiB |
| 300 s | 7,550 | 29.4 GiB |
| 420 s (default ready ring) | 10,550 | 41.1 GiB |

**Writing.** `Offer` runs on the capture thread and never waits: a full 8-frame queue counts as a drop. The `tidal delay writer` thread sets the slot's sequence to -1, copies the data and writes the sequence last.

**Reading.** `TryRead` (`:524`) is lock-free. It checks the sequence, copies into the caller's buffer, applies a memory barrier and re-checks. A mismatch means the frame was overwritten during the read (`Lapped`).

**Pacing.** `DelayPacer.Decide` (`src/Emerald.Video/DelayPacer.cs:65`) is a pure function evaluated in this order:

| # | Condition | Action |
|---|---|---|
| 1 | Sealed and read at or past the end | Finish |
| 2 | Writer has lapped the reader | Resync |
| 3 | Nothing to read, not sealed, stalled 10 s | Fail |
| 4 | Nothing to read otherwise | Fill if below target, else Repeat |
| 5 | Written below target | Fill |
| 6 | Lag above target plus deadband | Skip |
| 7 | Lag below target minus deadband | Repeat |
| 8 | Otherwise | Advance |

The deadband is half a second of frames, at least 1.

Rules 1, 3 and 4 are reached in steady state only when the target is at or below the deadband. With a longer delay, once `written` stops, rule 7 returns Repeat before the reader arrives at the end, so the line neither finishes nor fails. See [A2](11_TECHNICAL_ASSESSMENT.md). `StalledMs` is 0 until the first frame is written, so a line that never receives a frame does not fail either.

**Which formats:** `DelayFormats.TryMatch` allows only progressive 1080 at standards 0, 1, 8, 9 and 10, because there is no scaler in this path.

## Playback and output flowchart

```mermaid
flowchart TD
    subgraph OnScreen["On screen: Live Edit"]
        A1["Proxy file"] --> A2["MediaElement PlayerA or PlayerB"]
        A3["Open segment"] --> A4["HeldPicture reads ring"]
        A5["Live preview"] --> A6["RecordingPicture"]
    end
    subgraph Delayed["SDI output: delayed"]
        B0{"LiveAirTransmitter only: cue media playing?"}
        B0 -- "Yes" --> B7["Cue media: FrameReadahead, or ring or black picture for an audio-only cue. Pacer not consulted."]
        B0 -- "No" --> B2["DelayPacer.Decide"]
        B2 -- "Fill or Resync" --> B3["Black"]
        B2 -- "Repeat" --> B5["Last frame, muted"]
        B2 -- "Finish or Fail" --> B10["Thread ends"]
        B2 -- "Advance or Skip" --> B1["DelayLine.TryRead"]
        B1 -- "EndOfLine" --> B10
        B1 -- "Lapped or NotYetWritten: no push" --> B0
        B1 -- "Ok" --> B4{"LiveAirTransmitter only: LiveCues.Due?"}
        B4 -- "Cut: read position jumps, no push" --> B0
        B4 -- "Media opened: starts next pass, no push" --> B0
        B4 -- "None, or could not open" --> B6{"LiveAirTransmitter only: LiveCues.BlankAt?"}
        B6 -- "Yes" --> B8["Black and silence"]
        B6 -- "No" --> B9["Ring frame with audio. Level ramp in LiveAirTransmitter only."]
    end
    subgraph Playout["SDI output: EDL"]
        C1["FrameSource"] --> C2["AdvanceBeds, gain and offset"]
        C2 --> C3["PushFrame"]
    end
    B3 --> TX["SdiOutput or SdiOutputFan: VHD_LockSlotHandle paces the loop"]
    B5 --> TX
    B7 --> TX
    B8 --> TX
    B9 --> TX
    C3 --> TX
    TX --> Wire["SDI TX port"]
```

The transmitters use no software timer. `PushFrame` blocks until the card frees a slot, and that is what paces the loop.

### DelayTransmitter

`src/Emerald.Video/DelayTransmitter.cs`. Startup sets `readSeq = max(0, written - target)`, so a ring already holding the delay goes straight to air. Each pass acts on the pacer's decision, as in the flowchart. Each time another second's worth of frames has gone out, it publishes frame counts and, at most once per 5 s, logs `delay.trouble` and adds a `DropMark` for repeats or skips since the last line. Nothing is published while it is sending only fill or repeats.

### LiveAirTransmitter

`src/Emerald.Video/LiveAirTransmitter.cs`. It has the same pacing loop. It does not publish to `PipelineState`, log `delay.trouble`, add a `DropMark` or announce segments, so its repeats and skips reach only `Counters`. It also refuses to start on a failed port hold, and adds:

- **A moving target.** Inserts add to it, cuts subtract.
- **The EDL.** `CuesForAir` is read at the top of every pass, even while filling. That read is the acknowledgement.
- **Cues.** `LiveCues.Due` chooses. Replace, overlay and fit-to-fill consume the ring underneath, while insert does not, so the recording waits.
- **Preparation.** `Prepare` opens decoders 6 seconds ahead on tasks. `PreRolled` waits for half a second of decoded frames.
- **Blanks.** `LiveCues.BlankAt` is checked on every frame against the newest list, so a changed end takes effect on the next frame.
- **Levels.** A per-frame linear gain ramp from `AudioEdits`.

### PlayoutService

See [the EDL workflow](04_SYSTEM_WORKFLOW.md#edl-generator-composing-and-playing-out).

## Media processing workflow

```mermaid
flowchart LR
    Rec["Recorder writes segments"] --> Watch["SegmentWatch polls: mvhd present means closed"]
    Watch --> LogSeg["capture.segment log line"]
    Rec --> Warden["StorageWarden: oldest first past the limit"]
    Warden --> Ledger["PruneLedger records what went"]
    Rec --> List["MediaLibrary.List"]
    Ledger --> List
    List --> Probe["MediaProbe with ffprobe, 6 at a time, cached"]
    List --> Pair["MasterIndex pairs proxy and master"]
    Probe --> Starts["Accumulate works out segment starts"]
    Pair --> Starts
    Starts --> Place["Live Edit Place: timeline"]
    Place --> Wave["Waveform: ffmpeg at 8 kHz mono, cached in memory"]
    Place --> Export["RangeExport: concat, stream copy"]
    Export --> Sidecar["name.json and name.preview.mp4"]
    Ingest["Ingest job: single file"] --> Reg["MediaLibraryRegistrar: verifies the files"]
    Reg --> Row["IngestSchedulerService: SaveRecording, SQLite row"]
```

Export uses masters only if every segment in the range has one, otherwise proxies. DNxHD is all-intra, so a master export is frame-exact. A proxy export starts at a keyframe.

## Timecode handling

- **Station clock:** `TimecodeService` polls every 500 ms and free-wheels on a `Stopwatch` between polls. Small errors correct at most 1 frame per poll. Larger ones snap.
- **No timecode travels with frames.** Ring slots carry a frame number. Segments have no timecode track; they are placed from the folder name.
- **Single-file ingest** writes `-timecode` from the job's SOM.
- **Drop-frame is not implemented.** 29.97 is treated as 30.

## Buffering

At 1080p25:

| Stage | Depth | Reference |
|---|---|---|
| RX card slots | 4 | `src/Emerald.Video/SdiCapture.cs:303` |
| Encoder video queue | 8 frames, 320 ms | `src/Emerald.Video/SdiCapture.cs:28` |
| Encoder audio queue | 200 chunks, 8 s | `src/Emerald.Video/SdiCapture.cs:29` |
| Ring hand-off queue | 8 frames | `src/Emerald.Video/DelayLine.cs:205` |
| Ring | Delay + headroom + guard | `src/Emerald.Video/DelayLine.cs:120` |
| Cue video read-ahead | 1 s plus 2 buffers | `src/Emerald.Video/LiveAirTransmitter.cs:612` |
| Cue and bed audio | Up to 1.5 s ahead in 3 s | `src/Emerald.Video/AudioSource.cs:29` |
| TX card slots | 4 | `src/Emerald.Deltacast/SdiOutput.cs:12` |
| Speakers | 2 queued, of 8 buffers | `src/Emerald.Video/SpeakerOutput.cs:30` |

## Synchronisation

| Where | How audio and video stay together |
|---|---|
| Recording | Equal counts of frames and audio chunks per encoder |
| Ring | A slot holds both, so sync is structural. A skip loses both and a repeat is mute. |
| Cues | Separate decoders. The position passed to `ReadFrame` has a [verified fault](11_TECHNICAL_ASSESSMENT.md). |
| Playout | Picture and each bed advance frame by frame on one thread |
| Speakers | Queue depth is capped at 80 ms, dropping when the clocks drift |

## Error recovery

| Stage | Failure | Behaviour |
|---|---|---|
| Setup | Bad folder, ffmpeg or bed | Sentence, nothing opened |
| RX open | Lease, handle, no signal, raster | `Fail` and return |
| Slot lock | Timeout | Retry until stopped. Before the first frame and after 3 s, one `Fail`, which marks the result failed. After the first frame, silent. |
| Encoder slow | Queue full | Frame and its audio dropped, logged, recording continues |
| Encoder dead | Process exit | Detected within about a second of frames, recording stops, no restart |
| Midnight encoder | Open fails | Stays in old folder, tries again next midnight |
| Ring create | No space or IO | `DelayLineException`, lock fails, recording continues |
| Ring write | Exception | `Fault` set, ring seals, recording continues |
| TX underrun | Receiver stops | Delay of 0.5 s or less: the reader catches the writer, mute repeat, Fail after 10 s stalled. Longer delay: plays on until lag falls half a second below target, then mute repeat indefinitely. Fail is not reached. |
| TX lapped | Overwritten | Resync, one black frame |
| TX card | Push fails | Thread ends, no restart |
| Cue open | Throw or null | Logged, the recording carries on |
| Cue decode slow | | Hold last frame, muted, insert target grows by one |
| Bed file bad, playout or cue | | Silence, `playout.audiofailed` |
| Bed file missing, recording | | Sentence at setup, nothing opened |
| Bed file present but unreadable, recording | | ffmpeg exits: as "Encoder dead" |
