# 1. Project overview

Line references in these documents are to commit `dd8e4a7`.

## What Emerald is

Emerald is a Windows desktop application for broadcast plants. It records SDI video, plays SDI video out, delays a live feed before it goes to air, and lets an operator edit that delayed feed while it is still in the delay.

It is written in C# on .NET 8 with WPF. It drives DELTACAST SDI cards through their VideoMaster SDK. It uses ffmpeg child processes for all encoding and for all decoding on the way to SDI. On-screen playback in Live Edit decodes through WPF `MediaElement` (Media Foundation).

SDI (Serial Digital Interface) is the coaxial video cable standard used in TV facilities. An **RX** port receives a feed and a **TX** port transmits one.

The name of the product and project is taken from `Emerald.sln` and `Directory.Build.props`, which sets version `1.0.0`. The installer's publisher field is "Peregrine Broadcast" (`installer/install.bat`).

## What problem it solves

A channel that airs a live programme often wants a safety delay. Recording, delaying, editing and playing out are usually separate systems. Emerald puts them in one process on one machine:

1. The feed comes in on an SDI receiver and is recorded to disk in two qualities.
2. The same frames go into a delay buffer on disk.
3. An operator watches the recording on a timeline and can replace, insert or black out material before it reaches air.
4. The delayed, edited picture goes out on an SDI transmitter.

It also schedules timed recordings, and plays out scheduled messages cued to the house clock.

## The six ways in

All of them are windows in **one process**. The reason is hardware: a DELTACAST receiver allows exactly one open handle, so the preview, recorder and ingest have to negotiate for it, and that negotiation (`RxLease`) only works inside one process.

| Module | Window class | Opened by | What it does |
|---|---|---|---|
| Capture deck (the shell) | `ShellWindow` | default, no argument | Live confidence monitor, audio meters, record button, delay-ring management. Opens every other module. |
| Playback deck | `PlaybackWindow` | shell button only | Tidal lock: transmits the receiver a fixed time later. |
| EDL Generator | `EdlWindow` | `--edl` | Composes a message (video, audio beds, start time, in and out points), queues it, plays it out on the house clock. |
| Ingest Controller | `IngestControllerWindow` | `--ingest` | Schedules recordings off an RX port, by timecode, with a SQLite-backed queue. Has a simulate mode. |
| Live Edit | `LiveEditWindow` | shell button only | Timeline over the recordings. Marks, ingest placement, deleted scenes, audio levels, export, and its own delayed transmission ("Go Live"). |
| Monitor | `MonitorWindow` | `--monitor` | Whole-plant status strip and activity log. Can serve a read-only web page to other machines. |

`readme.md` still calls Live Edit "layout only". That is out of date: see [the assessment](11_TECHNICAL_ASSESSMENT.md#stale-documentation).

## Architecture in brief

Emerald is a **single-process, layered desktop application**. There is no separate backend. "Backend" here means the service classes in the lower-layer projects, called directly by the windows.

```
Windows (UI)   Emerald.App, Emerald.EDL, Emerald.Ingest, Emerald.LiveEdit
                     |
Media          Emerald.Media      store listing, probing, export, pruning
                     |
Video          Emerald.Video      recording, delay ring, transmitters, ffmpeg
                     |
Hardware       Emerald.Deltacast  VideoMaster SDK interop, boards, ports
                     |
Core           Emerald.Core       timecode, settings, log, shared state, pure rules
```

Three design decisions explain most of the code.

- **One process.** Hardware arbitration and shared state are plain objects. There is no message bus.
- **Frames as the clock.** The card paces transmission and capture. Positions are counted in frames, not read from the wall clock, because the two drift apart.
- **Rules without hardware, mostly in Core.** The decisions with the most consequences are free of cards and windows and take time as a parameter, so they can be tested with no card. `LiveCues`, `IngestTimeline`, `IngestPlacement` and `AudioEdits` are plain rules. `EdlLedger` is a stateful object with no clock or UI that logs through `EdlLogger`. `DelayPacer` is the same kind of rule, in Video.

See [the system architecture](03_SYSTEM_ARCHITECTURE.md) for diagrams.

## Technologies

| Area | Technology | Evidence |
|---|---|---|
| Language | C# with nullable and implicit usings | `src/Emerald.App/Emerald.App.csproj` |
| Runtime | .NET 8, `net8.0-windows`, x64 only | every `.csproj` |
| UI | WPF (XAML plus code-behind, no MVVM framework) | `src/Emerald.App/App.xaml` |
| Video hardware | DELTACAST VideoMaster, `VideoMasterHD.dll` and `VideoMasterHD_Audio.dll` | `src/Emerald.Deltacast/VideoMasterHD.cs:13`, `:166` |
| Encode and decode for SDI | ffmpeg child process | `src/Emerald.Video/RecordingProfile.cs`, `FrameSource.cs`, `AudioSource.cs` |
| Probing | ffprobe child process, plus a managed MOV/MP4 atom reader | `src/Emerald.Media/MediaProbe.cs`, `MediaCodec.cs` |
| On-screen playback | WPF `MediaElement` (Media Foundation) | `src/Emerald.LiveEdit/LiveEditWindow.xaml:138` |
| Speaker monitoring | `winmm.dll` `waveOut*` P/Invoke | `src/Emerald.Video/SpeakerOutput.cs` |
| Database | SQLite through `Microsoft.EntityFrameworkCore.Sqlite` 8.0.11, ingest only | `src/Emerald.Ingest/Emerald.Ingest.csproj` |
| Other persistence | JSON and JSONL files | `src/Emerald.Core/AppSettings.cs`, `ActivityLog.cs` |
| Tests | xUnit 2.5.3 | `tests/*/*.csproj` |

That SQLite package is the only NuGet package outside the test projects.

## External dependencies and services

| Dependency | Needed for | If missing |
|---|---|---|
| DELTACAST card and driver | All capture and transmission | App opens; board scan reports the error. Monitor, Live Edit and the clip store still work. |
| ffmpeg | Recording, playout, export, waveforms | Those functions are unavailable. |
| ffprobe | Clip lengths and stream layout | Must sit in the same folder as the ffmpeg in use, because `PATH` is not searched for it. Otherwise durations read as unknown and clips look unfinished. Master codec names still work, because `MediaCodec` reads them from the file. See [assessment](11_TECHNICAL_ASSESSMENT.md). |
| Timecode HTTP service | The station clock | Clock shows `--:--:--:--`, including after a lost connection. Ingest cannot be scheduled unless SIMULATE is on, which uses the PC clock. An EDL message is not refused: with no clock its cue counts as zero and it plays immediately. |

The timecode service is external and is not part of this repository. Only the client's expectations are documented: see [API and communication](07_API_AND_COMMUNICATION.md#timecode-client). No cloud services, licensing servers or telemetry were found in the code.

## Hardware integration

- Boards named in the code: DELTA-3G and DELTA-12G families, with a wider type table in `VideoMasterHD.BoardTypeName`.
- SDK: VideoMaster. 17 P/Invoke imports.
- Pixel format: 8-bit UYVY 4:2:2 on both RX and TX.
- Audio: 48 kHz embedded, up to 16 channels (8 stereo pairs).

See [hardware integration](06_HARDWARE_INTEGRATION.md).

## Modules and responsibilities

| Project | Responsibility | References |
|---|---|---|
| `Emerald.Core` | Timecode, station clock, settings, activity log, monitor server, shared state (`TidalLock`, `LiveAir`, `PipelineState`), pure edit rules | none |
| `Emerald.Deltacast` | SDK interop, board scan, RX arbitration, TX output | Core |
| `Emerald.Video` | SDI recording, delay ring, delayed transmitters, ffmpeg decode sources, meters, speakers | Core, Deltacast |
| `Emerald.Media` | Media store listing, probing, range export, segment watching, storage limit, waveforms | Core, Video |
| `Emerald.EDL` | EDL window and playout engine | Core, Deltacast, Video, Media |
| `Emerald.Ingest` | Scheduler, queue, job store, ingest window | Core, Deltacast, Video, Media |
| `Emerald.LiveEdit` | Timeline editor and Go Live | Core, Media (Video and Deltacast arrive transitively) |
| `Emerald.App` | Entry point, capture and playback decks, Monitor window, shared styles | all of the above |

## Feature status

| Feature | Status | Notes |
|---|---|---|
| Recording to two qualities, segmented | Implemented | H.264 proxy and DNxHD 145 master from one ffmpeg |
| Per-day folder across midnight | Implemented | Frame-counted, see [media processing](05_MEDIA_PROCESSING.md#midnight-rollover) |
| Confidence preview and audio meters | Implemented | |
| Tidal lock delay | Implemented | The end-of-drain behaviour has a verified fault |
| EDL playout | Implemented | Single TX from the UI; multi-port path exists but no caller |
| Ingest scheduling | Implemented | Scheduler runs only while its window is open |
| Live Edit timeline and Go Live | Implemented | Placements are not saved to disk |
| Delete Scene (black and silence) | Implemented | Replaces the former cut, which remains in the model but is unreachable |
| Range export | Implemented | Exports the recording as captured, without edits |
| Bookmarks | Implemented | One note per frame per day, saved as JSON per day |
| Undo and redo in Live Edit | Implemented | 50 levels. Refused for anything inside the no edit zone. Not saved to disk. |
| Storage limit | Implemented | Deletes the oldest segments past the limit. Runs only from the capture deck while it records. |
| Several TX ports | Implemented | Tidal lock and Go Live only |
| Activity log and EDL log | Implemented | JSONL files. The retention sweep covers `emerald-*.jsonl` only, so `edl-*.jsonl` files are never removed. |
| On-air recording from the EDL | Implemented | Optional, through `SdiCapture` |
| Remote monitoring page | Implemented | Read-only (GET and HEAD), no authentication, off by default. Runs only while the Monitor window is open. The full installer opens the firewall for TCP 8787 regardless, and a different port needs its own rule. |
| Installer and update scripts | Implemented | Batch files, no MSI |
| Simulated hardware | Partial | Ingest Controller only |
| Clip audition on the capture deck | Not reachable | The code is present, but `StageMedia` is collapsed and never shown, and `RefreshClips` returns early. |
| Clip audition on the playback deck | Removed | Only stale comments in `PlaybackWindow.xaml.cs` mention it. |

The full analysis, including faults found, is in [the technical assessment](11_TECHNICAL_ASSESSMENT.md).

## Glossary

| Term | Meaning here |
|---|---|
| Air, on air | What is leaving the TX port right now. |
| ANC | Ancillary data in the SDI signal. Embedded audio travels here. |
| Bed | An audio file carried alongside the picture: in EDL playout on its own SDI channel pair, and in a recording as a further audio stream in the file. |
| Blank | Placement mode that puts black and silence on air and leaves the delay unchanged. This is Delete Scene. |
| Conform | Sending the whole EDL when an edit's first frame reaches the far edge of the no edit zone. |
| Cue | One instruction for the Live Edit transmitter, in recording frames. |
| Delay line, ring | A memory-mapped file holding raw frames between the recorder and the transmitter. |
| DNxHD | Intra-frame broadcast codec used for the master files. |
| EDL | Edit Decision List. In the EDL Generator it means one playout command. In Live Edit it means the current full set of edits. |
| Fan-out | One transmitter driving several TX ports with the same picture. |
| Master, high | The DNxHD `.mov` file. |
| Mark in, mark out | The operator's selected range on the timeline. |
| No edit zone | The next 10 seconds of air, which cannot be changed. |
| Proxy, low | The H.264 `.mp4` file, used for viewing because Media Foundation cannot decode DNxHD. |
| Recording frame | Frames since the recording rolled. Compare *ruler frame*. |
| Ruler frame | Frames from midnight of the day the timeline shows, including inserts. |
| Segment | One file of a recording split by time. |
| Slot | The card's buffer for one frame. Software locks a slot, uses it and unlocks it. |
| SOM, EOM | Start and end of message. |
| Station clock, house clock | Time read from the external timecode service. |
| Tidal lock | Emerald's name for the fixed-delay feed from the playback deck. |
| UYVY | The raw 8-bit 4:2:2 pixel layout, 2 bytes per pixel. |
