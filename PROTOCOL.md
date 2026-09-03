# EDL record format

Every command the EDL Generator issues is also composed as a JSON object, which
**Copy JSON** in the QUEUE panel puts on the clipboard. It is a record of what was played:
which board and channel, from when, for how long, and from what media.

> The generator plays out locally through VideoMaster and has **no network transport** —
> there is nothing to connect to and no server involved. This document describes the shape
> of that record, for pasting into a run log or ingesting somewhere downstream.

## Record object

```json
{
  "type": "edl.command",
  "version": 3,
  "id": "0bf97576dc984246854faec55c53a688",
  "issuedAt": "2026-08-27T21:56:00.0000000+10:00",
  "capture": {
    "board": { "index": 1, "model": "DELTA-12G-elp-h 20", "type": 15 },
    "port": "RX3",
    "index": 3
  },
  "playback": {
    "board": { "index": 0, "model": "DELTA-3G-elp-d 22", "type": 6 },
    "port": "TX1",
    "index": 1
  },
  "timing": {
    "frameRate": 25,
    "startTimecode": "21:56:00:00",
    "startFrame": 1974000,
    "onAirTimecode": "21:57:00:00",
    "onAirFrame": 1975500,
    "mediaStartTimecode": "01:00:00:00",
    "som": "00:01:00:00",
    "somFrame": 1500,
    "eom": "00:11:00:00",
    "eomFrame": 16500,
    "duration": "00:10:00:00",
    "durationFrames": 15000,
    "stopTime": "22:07:00:00",
    "stopFrame": 1990500,
    "postPlay": "blackScreen",
    "loop": true
  },
  "media": {
    "kind": "folder",
    "source": "D:\\media\\promo",
    "fileCount": 3,
    "files": ["clip_a.mxf", "clip_b.mov", "clip_c.mp4"]
  }
}
```

| Field | Type | Meaning |
|---|---|---|
| `type` | string | Always `edl.command`. |
| `version` | int | Schema version. Currently `3`. |
| `id` | string | 32-hex-char id, unique per command. |
| `issuedAt` | ISO-8601 | Local time the command was issued, with offset. |
| `capture.board.index` | int | DELTACAST board index as reported by `VHD_GetApiInfo`. |
| `capture.board.model` | string | e.g. `DELTA-12G-elp-h 20`, from `VHD_GetBoardModel`. |
| `capture.board.type` | int | `VHD_BOARDTYPE` enum value (6 = DELTA-3G, 15 = DELTA-12G). |
| `capture.port` / `.index` | string / int | Selected RX channel, e.g. `RX3` / `3`. |
| `playback.board.*` | | Same shape as `capture.board`. **May be a different board.** |
| `playback.port` / `.index` | string / int | Selected TX channel, e.g. `TX1` / `1`. |
| `timing.frameRate` | int | Nominal rate the timecodes are counted at, from the timecode API. |
| `timing.startTimecode` | `HH:MM:SS:FF` | On-air time on the **house clock**. |
| `timing.startFrame` | int | Same value as an absolute frame count at `frameRate`. Use whichever is convenient. |
| `timing.onAirTimecode` / `timing.onAirFrame` | `HH:MM:SS:FF` / int | When video actually reaches the TX: `startTimecode + som`. The output holds the post-play fill between the two. |
| `timing.mediaStartTimecode` | `HH:MM:SS:FF` | The media's own embedded start timecode, as read by ffprobe. **Reference only** — nothing is measured against it. |
| `timing.som` / `timing.somFrame` | `HH:MM:SS:FF` / int | Start of message: a **delay applied to `startTimecode`**. `onAirTimecode` = `startTimecode + som`. Not a position inside the media. |
| `timing.eom` / `timing.eomFrame` | `HH:MM:SS:FF` / int, or `null` | End of message: the matching off-air offset, so `duration = eom − som`. `null` means no fixed duration (loops until stopped). |
| `timing.duration` | `HH:MM:SS:FF` or `null` | Derived: `eom − som`. **`null` means play until stopped.** |
| `timing.durationFrames` | int or `null` | Same value in frames. |
| `timing.stopTime` | `HH:MM:SS:FF` or `null` | Derived: `onAirTimecode + duration` (equivalently `startTimecode + eom`), wrapped at 24 h. `null` when open-ended. |
| `timing.stopFrame` | int or `null` | Same value in frames. |
| `timing.postPlay` | string | `blackScreen` or `freezeLastFrame` — what the TX carries after the message until the next one cues. |
| `timing.loop` | bool | Always `true` — see below. |
| `media` | object or `null` | The video source. **`null` means no video** — the TX carries black and only the audio plays. |
| `media.kind` | string | `folder` or `file`. |
| `media.source` | string | Absolute path, as seen by the machine running the generator. |
| `media.fileCount` | int | Number of entries in `files`. |
| `media.files` | string[] | File **names** (not full paths), ordered case-insensitively. For `kind: "file"` this is the single filename. |

### Capture and playback are independent

Each direction carries its own board. The RX may be on one card and the TX on another —
for example capture on `DELTA-12G-elp-h 20` (8 RX, 0 TX) and playback on
`DELTA-3G-elp-d 22`. They are often the same board, but never assume it: read
`capture.board.index` and `playback.board.index` separately.

### Duration semantics

`loop` is always `true`, and duration is the authority:

- **`duration` is `null`** — play the media on repeat indefinitely, until a stop arrives
  by some other means.
- **`duration` longer than the media** — repeat the media from the top as many times as
  needed to fill the duration. A 10 s clip with a 20 s duration plays twice.
- **`duration` shorter than the media** — stop when the duration expires, mid-clip.
  A 10 s clip with a 5 s duration stops at 5 s.

The duration is wall-clock playout time starting at `startTimecode`; it is not affected by
how many files are in the folder.

### Timecode

All timecodes are **non-drop-frame**, counted at `timing.frameRate`, in the range
`00:00:00:00` to `23:59:59:(rate-1)`. The generator refuses to accept anything outside that
range, so you do not need to defend against `00:00:00:25` at 25 fps.

The rate is whatever `frameRate` the timecode API reports, rounded to the nearest integer.

### Audio tracks

`audio` is a list of language beds, or `null` when no audio was selected — in which case the
message plays out **silent**, and the video file's own audio is deliberately not used.

```json
"audio": [
  { "label": "English", "kind": "file", "source": "D:\audio\en.wav",
    "fileCount": 1, "files": ["en.wav"], "offsetMs": 100, "default": true },
  { "label": "Arabic",  "kind": "file", "source": "D:\audio\ar.wav",
    "fileCount": 1, "files": ["ar.wav"], "offsetMs": -50, "default": false }
]
```

| Field | Type | Meaning |
|---|---|---|
| `label` | string | The language name, as typed by the operator. |
| `kind` / `source` / `fileCount` / `files` | | Same shape as `media`. Each track loops its own files to fill the duration. |
| `offsetMs` | int | This language's trim. Positive delays audio behind picture, negative advances it. Independent per track and adjustable live. |
| `default` | bool | The track on air when the message starts. Exactly one is true. |

**One track is embedded at a time**, on group 1, channels 1-2. The operator can switch which
one mid-message; `default` records only where it started.
