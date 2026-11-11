# 11. Technical assessment

Line references are to commit `dd8e4a7`. No source code was changed for this assessment.

## How to read this

| Label | Meaning |
|---|---|
| **Verified** | The code path was read and the failure follows from it. **None was reproduced on hardware.** Nothing was run on a card for this review. |
| **Re-checked** | A Verified issue whose lines were then read again separately, as a second check. Every numbered issue in A, B and C went through a separate audit that tried to disprove it. None was disproved, and eight had their wording narrowed. See the [audit report](AUDIT_REPORT.md). |
| **Potential** | A real risk, but it depends on behaviour the repository cannot confirm, or was not measured. |
| **Not verified** | Could not be established. |

Effects at the card, such as what goes down the wire on an underrun, are Not verified throughout.

## Implementation status

| Area | Fully working by code review | Partial or broken |
|---|---|---|
| Capture deck | Record, stop, preview, meters, midnight rollover, storage limit while recording | Board, port, profile, output path and limit change handlers never run (A1). Clip stage is dead code. |
| Playback deck | Arm, count down, on air, multiple TX | End of drain (A2). Close prompt (B5). Clip audition was removed; only stale comments remain. |
| EDL | Compose, validate, queue, cue, play, loop, gain, solo, offset | SOM with embedded audio (A5). Multi-port path has no caller. |
| Ingest | Validate, schedule, roll, register, recover | Runs only with its window open (B7). Verification is shallow (B10). |
| Live Edit | Timeline, player, marks, placement, delete scene, undo, levels, bookmarks, export, Go Live, EDL handshake | Placements not saved. Export ignores edits and has an offset fault (B2). |
| Monitor | Log, filters, strip, transmitted view, remote page | Ingest stage never written. Live Edit absent from strip (C5). |
| Installer | Install, update, uninstall, EDL product | Version is written twice |

## A. Highest impact: Verified and Re-checked

### A1. `_loading` is never cleared, so five shell handlers never run

`src/Emerald.App/ShellWindow.xaml.cs:110` starts it true. Every other assignment sets it true or restores the saved value (`:259`, `:292`, `:306`, `:315`, `:488`, `:501`). Nothing sets it false. So these return at their first line:

| Handler | Line | Consequence |
|---|---|---|
| `BoardBox_SelectionChanged` | `:418` | Another board does not repopulate ports or move the preview. A recording then uses the new board with a port from the old list. |
| `PortBox_SelectionChanged` | `:428` | The preview stays on the old port, so what is monitored differs from what will be recorded. |
| `Profile_Changed` | `:525` | Settings not written through until Record or close, no log, no note refresh, and `recordingSegmentChosen` is never set, so a 2-minute segment resets to 30 s at the next start. |
| `OutputPath_LostFocus` | `:582` | Path not saved for Live Edit until Record. |
| `StorageLimit_Changed` | `:1854` | No log and no immediate sweep. |

`ShowModule`'s closed handler (`:2570`) has the same guard, so the deck never reloads its profile fields after a module closes.

The monitored-versus-recorded mismatch is the one that matters most on air. `StartRecording` reads the boxes, so a changed port is what gets recorded, while the preview stays on the old one. `PlaybackWindow` clears its own equivalent flag correctly.

### A2. The delay freezes about half a second after a recording stops

`src/Emerald.Video/DelayPacer.cs:90-94` applies the drift rule with no check for a sealed ring. After sealing, `written` stops while `readSeq` advances, so the lag falls. Once `lag < target - deadband`, `Decide` returns Repeat, which does not advance `readSeq`, so it never reaches `sealedAt` and Finish never comes.

- On air: a frozen, silent frame until someone stops it.
- `DrainComplete` never fires, and the rest of the delay never airs.
- This breaks the stated purpose in `src/Emerald.App/ShellWindow.xaml.cs:1165`.
- Both transmitters are affected.
- It applies to delays longer than about half a second. If lag was above target at the seal, the freeze comes up to about a second after it. If below, sooner.
- A recording stopped before a whole delay has accumulated is worse: `Decide` keeps returning Fill (`src/Emerald.Video/DelayPacer.cs:87`), so the output holds black, nothing recorded airs, and it never finishes.

The test `Stopping_the_recording_still_plays_out_everything_recorded` (`tests/Emerald.Video.Tests/DelayPacerTests.cs:127`) checks only the first and last positions, so it passes.

### A3. Live Edit cue audio was read at the wrong position (fixed)

**Fixed.** The transmitter now calls `AudioSource.ReadVideoFrame`, which takes a frame number and converts it to samples. `CueSoundTests` plays a file whose every sample carries its own position through the real decoder, a frame at a time, and checks each frame; it fails on the old code. Not yet heard at the card. What follows describes the fault as it was.

`LiveAirTransmitter.CueSound` passed `job.Taken`, a frame count, to `AudioSource.ReadFrame`, which counts **samples** (its parameter was then named `framePosition`; it is now `samplePosition`). `PlayoutService` passed samples correctly and is unchanged.

Each frame re-read almost the same 1920 samples at 25 fps, moving one sample a frame, so the cue stayed within the first moments of its audio however long it ran. With sound at the head of the media this should have been a frame-rate buzz; with a silent head, silence. What it sounded like at the card was not measured.

### A4. Multi-pair audio corrupts the delayed output

`SdiCapture` interleaves `pairs × 2` channels and passes the chunk to the ring unchanged. `DelayLine` allows for stereo. `DelayTransmitter.PushWithAudio` (`src/Emerald.Video/DelayTransmitter.cs:469`) and its copy in `LiveAirTransmitter` treat it as 4 bytes per sample.

With 2 pairs, left alternates channels 1 and 3 sample by sample, at twice the normal count. Above 4 pairs the chunk is cut off. Since pairs are always auto-detected, a multi-language feed triggers this. The recording itself is correct. Only the air path is affected.

### A5. EDL: SOM moves the picture but not embedded audio

`src/Emerald.EDL/PlayoutService.cs:1089` opens `AudioSource` without the `seek` that `FrameSource` gets. With SOM at one minute on a clip using its own audio, sound starts at the head and picture one minute in.

### A6. Encoder errors carry over between recordings

`_encoderErrors` (`src/Emerald.Video/SdiCapture.cs:741`) is only added to and read, and `Start` does not clear it. The capture deck's and the EDL's `SdiCapture` are long-lived, so one ffmpeg error line is replayed through `Fail` at the end of every later recording on that instance. The deck's closing line then says failed, though the files are unaffected. Ingest builds a new `SdiCapture` per job, so carry-over does not reach it, but any error line in that job's own run still fails the job.

### A7. A deleted scene can reach air through a Repeat

`src/Emerald.Video/LiveAirTransmitter.cs:341` repeats `onAir?.Video`, and `onAir = into` stores the frame even when black went out. Any Repeat during a deleted scene airs the deleted picture, muted. A2 makes that a frozen frame if a recording stops during one.

This undercuts the core promise of Delete Scene, that the range is black and silent. Repeat needs the same `BlankAt` check.

### A8. Frame 0 is at least 2 seconds after the folder stamp

The folder is stamped before capture starts. `DetectPairs` (`src/Emerald.Video/SdiCapture.cs:995`) then reads and discards slots for a full 2 seconds, and its comment "well under a second" is wrong. Placement is folder start plus frames.

Consequences:

- The timeline sits at least 2 seconds behind the station clock.
- A scheduled ingest begins at least 2 seconds after its timecode and is stamped with the planned start.
- The midnight switch happens at least 2 seconds after real midnight. The folder is named `00-00-00`, but that is not when its first frame was captured.

## B. Other Verified issues: high

| ID | Issue | Location |
|---|---|---|
| B1 | **A TooLate refusal blocks everything after it.** Revisions are whole, so once the timeline holds a change at or before the committed frame, every later revision is refused too, until the change is undone. A long cue on air makes this reachable. | `src/Emerald.Core/LiveAir.cs:285` |
| B2 | **Export uses ruler frames as captured frames.** With an insert before the marks, the wrong material is exported by the insert's length. | `src/Emerald.LiveEdit/LiveEditWindow.xaml.cs:4552` |
| B3 | **Card failure on Fill, Resync or Repeat ends the transmitter with no status.** The deck keeps showing the last phase. | `src/Emerald.Video/DelayTransmitter.cs:206`, `:229`, `:241`, `src/Emerald.Video/LiveAirTransmitter.cs:312`, `:331`, `:341` |
| B4 | **Capture does not report signal loss once frames have arrived.** The `continue` also skips the encoder-exit check. | `src/Emerald.Video/SdiCapture.cs:444` |
| B5 | **Playback deck's "live" prompt can't fire.** It tests `_playout.Current`, while the real output is `_delayTx`. | `src/Emerald.App/PlaybackWindow.xaml.cs:146` |
| B6 | **Closing the shell mid-recording gives no warning and leaves rings on disk until the next launch:** about 44 GB for the ready ring at default settings at 25 fps, double at 50 fps, up to about 250 GB per ring with the largest Go Live choices at 25 fps, and possibly more than one ring. | `src/Emerald.App/ShellWindow.xaml.cs:2583` |
| B7 | **The ingest scheduler lives only while its window is open.** Closing it gives no warning about pending jobs, and they do not run while it is closed. On reopening, a job whose start has passed is marked Failed, and one still ahead is re-armed. | `src/Emerald.Ingest/Views/IngestControllerWindow.xaml.cs:181` |
| B8 | **Ingest frame limit mixes rates.** It counts receiver frames using the clock's rate. A 25 fps clock with a 1080p50 feed stops a 15-minute job at about 7.5 minutes and reports Completed, assuming ffmpeg encodes that feed, which was not tested. | `src/Emerald.Ingest/Services/IngestRecorder.cs:77` |
| B9 | **NOW then START books about 24 hours later,** with no warning. | `src/Emerald.Ingest/Views/IngestControllerWindow.xaml.cs:536` |
| B10 | **"Verified" does not verify contents.** A failed probe, missing proxy, dropped frames or a short signal gap all pass. | `src/Emerald.Ingest/Services/IngestMediaRegistrar.cs:81` |
| B11 | **Back-to-back ingests fail.** Validation allows touching windows, but A8 and finalisation mean the first is still running. No retry. | `src/Emerald.Ingest/Services/IngestControllerService.cs:440` |
| B12 | **EDL: a missing clock airs immediately,** and so does a start over 12 hours ahead. One failed poll clears the clock. | `src/Emerald.EDL/PlayoutService.cs:967`, `:989` |
| B13 | **EDL queue strands after an output failure.** Entries stay QUEUED with no worker. The next SEND starts a new one, which takes the stranded entries first: any whose start has passed air immediately, and the rest wait for their start. | `src/Emerald.EDL/PlayoutService.cs:402` |
| B14 | **EDL: live audio settings belong to the form.** Editing rows while on air changes the on-air message. Queued messages use whatever the form holds at air time. | `src/Emerald.EDL/EdlWindow.xaml.cs:1223` |
| B15 | **Fixed.** A wrong master could pair with a proxy when a master was missing, because every file in a folder had the same stamp. Masters are now placed on the clock by their own lengths (`StoreIndex`), not paired by number or stamp. | `src/Emerald.Media/MediaLibrary.cs:387` |
| B16 | **Segments slide on a gap.** A missing or unreadable middle segment moves all later ones earlier by its length. | `src/Emerald.Media/MediaLibrary.cs:293` |
| B17 | **Go Live overwrites settings with a stale copy.** `LiveEditWindow` holds its own `AppSettings`, so saves in each direction undo the other. | `src/Emerald.LiveEdit/GoLiveDialog.xaml.cs:197`, `src/Emerald.LiveEdit/LiveEditWindow.xaml.cs:66` |
| B18 | **The last segment stays "still being recorded"** after a stop, since the fingerprint excludes the newest file. It also blocks day rollover until a relist. | `src/Emerald.LiveEdit/LiveEditWindow.xaml.cs:655` |
| B19 | **Crash reporting gaps.** No `AppDomain.UnhandledException` or `TaskScheduler.UnobservedTaskException` handler exists, and the log is never flushed at exit, so `app.crashed` can be lost. | `src/Emerald.App/App.xaml.cs:134` |
| B20 | **Inserts placed before Go Live skip the capacity check,** which can cause repeated Resync and black. | `src/Emerald.LiveEdit/LiveEditWindow.Live.cs:116`, `:852` |
| B21 | **The storage warden follows the text box.** Typing another folder during a recording applies deletion there, and stops limiting the real store. | `src/Emerald.App/ShellWindow.xaml.cs:1899` |
| B22 | **The warden can delete ingest clips** when the ingest directory is the store, which is the default path when blank. Their database rows remain. | `src/Emerald.Media/StorageWarden.cs:151` |
| B23 | **Corrupt settings are silently replaced.** No log and no backup. | `src/Emerald.Core/AppSettings.cs:278` |
| B24 | **Signal loss never fails a delayed feed at delays over half a second.** The same rule as A2 returns Repeat before the reader reaches the end, so the "Cutting to black" failure is not reached and the feed holds a frozen, silent frame until stopped. No test exercises this sequence. | `src/Emerald.Video/DelayPacer.cs:79` |
| B25 | **Stopping a recording during the countdown leaves the transmitter running.** Tidal lock returns to Armed, the playback deck does not stop the transmitter, and it holds black. The next recording's `StartDelayTransmitter` returns early, so the lock sits in CountingDown with the old transmitter. | `src/Emerald.App/PlaybackWindow.xaml.cs:658` |
| B26 | **A ring write fault holds the feed without failing the lock.** `Write` sets `Fault` and seals, with `sealedAt` above `written`. The pacer then repeats, and `Fail` is not called. The only reader of `Fault` is a status line at release. | `src/Emerald.Video/DelayLine.cs:432` |
| B27 | **Audio after a silent pair is not recorded.** `PairsPresent` stops at the first silent pair, so audio on pairs 1 and 3 with pair 2 empty records one pair, and pair 3 is lost. | `src/Emerald.Video/SdiAudioReader.cs:263` |
| B28 | **Installers misread the existing store on an update.** A quoting fault in the PowerShell fragment leaves `MEDIA` as `'`, so the report is wrong and a folder named `'` is created. `settings.json` and the store are not changed. The fragment was reproduced in isolation; the full script was read, not run. | `installer/install.bat:83`, `installer/edl/install.bat:96` |

## C. Other Verified issues: medium and low

| ID | Issue | Location |
|---|---|---|
| C1 | Tidal lock can sit in CountingDown with no transmitter if the TX ports are switched off after arming and before the recording rolls. A warning is logged, but the lock is not failed. | `src/Emerald.App/PlaybackWindow.xaml.cs:666` |
| C2 | Record during "Stopping..." re-enters a stop, with unlocked `_cts` and `_worker` | `src/Emerald.App/ShellWindow.xaml.cs:987`, `src/Emerald.Video/SdiCapture.cs:172` |
| C3 | **Fixed.** Cue decoders could leak on timeout, or when a cue is passed but stays listed: 2 ffmpeg processes and about 112 MB each | `src/Emerald.Video/LiveAirTransmitter.cs:560` |
| C4 | TX thread blocks on media preparation (up to 3 s + 3 s) and on inline disposal | `src/Emerald.Video/LiveAirTransmitter.cs:293`, `:560` |
| C5 | `PipelineState.Ingest` has no writer, and Live Edit's transmission is not in the strip or `/api/state` | `src/Emerald.Core/PipelineState.cs`, `src/Emerald.Core/MonitorFeed.cs:28` |
| C6 | Remote page skips log lines on catch-up: `since` jumps to `lastSeq`, the newest in the ring, not the last received | `src/Emerald.Core/MonitorPage.cs:257` |
| C7 | Remote page stalls after a master restart, duplicates rows on overlapping polls, and shows a false BEHIND count | `src/Emerald.Core/MonitorPage.cs:194`, `:275` |
| C8 | `dropped` counter resets within about 500 ms, and a failed log folder loses lines uncounted | `src/Emerald.Core/ActivityLog.cs:539`, `:573` |
| C9 | `edl-*.jsonl` is never pruned, and retention runs only at startup | `src/Emerald.Core/ActivityLog.cs:489` |
| C10 | HEAD responses carry a body | `src/Emerald.Core/MonitorServer.cs:338` |
| C11 | A rejected conform's status is not retried by a resend, contrary to its comment. The edit stays Rejected even if a later revision containing it is accepted. | `src/Emerald.Core/EdlLedger.cs:287` |
| C12 | The ledger isn't told about day changes: stale conform state, and `EdlLedger.Clear` has no caller. Air is unaffected. | `src/Emerald.LiveEdit/LiveEditWindow.xaml.cs:892` |
| C13 | `CommittedThrough` overstates during an insert, refusing edits air has not reached | `src/Emerald.Core/LiveAir.cs:328` |
| C14 | On-air mark is wrong during Fit to fill, moving at half speed at 2x and then jumping | `src/Emerald.LiveEdit/LiveEditWindow.Live.cs:549` |
| C15 | Deleted scene isn't black on held frames in preview. Air is unaffected. | `src/Emerald.LiveEdit/LiveEditWindow.Held.cs:142` |
| C16 | Export button re-enables during an export | `src/Emerald.LiveEdit/LiveEditWindow.xaml.cs:3829` |
| C17 | **Fixed.** A held ring claim outlived its use, so a stopped recording's ring could stay mapped and on disk until Live Edit closed. The claim is now dropped once the line is sealed and not being shown. | `src/Emerald.LiveEdit/LiveEditWindow.Held.cs:60` |
| C18 | Bookmarks past midnight disappear after rollover | `src/Emerald.LiveEdit/LiveEditWindow.xaml.cs:903` |
| C19 | Each encoder start repeats audio while paying back the frames sent before ffmpeg opened its audio pipe: the first file of a recording and the first file after midnight, not every segment | `src/Emerald.Video/SdiCapture.cs:893` |
| C20 | `Finished` can fire while the old day's encoder is still closing | `src/Emerald.Video/SdiCapture.cs:428` |
| C21 | Every deck recording logs "stopped early" | `src/Emerald.App/ShellWindow.xaml.cs:1559` |
| C22 | Title sanitised for one name but not the other, so `SegmentWatch` can miss segments. The box is read-only, so only a hand-edited setting is exposed. | `src/Emerald.App/ShellWindow.xaml.cs:1034` |
| C23 | Probe and thumbnail "timeouts" do not bound anything: `ReadToEnd` blocks first. The thumbnail path is currently dead code. | `src/Emerald.Media/MediaProbe.cs:179`, `src/Emerald.App/ClipThumbnails.cs:82` |
| C24 | A failed waveform is cached as silence | `src/Emerald.Media/Waveform.cs:167` |
| C25 | Failed export leaves partial output and possibly the list file. A second preview encode is wasted for `.mp4`. | `src/Emerald.Media/RangeExport.cs:190`, `:214` |
| C26 | Stack allocation in a loop (build warning CA2014). Harmless on Emerald's own files. | `src/Emerald.Media/MediaCodec.cs:341` |
| C27 | Modules opened before the shell get duplicated | `src/Emerald.App/App.xaml.cs:161` |
| C28 | Port ownership checks the saved board, not the selected one | `src/Emerald.App/PlaybackWindow.xaml.cs:313` |
| C29 | ON AIR marks stay lit after a stop | `src/Emerald.App/PlaybackWindow.xaml.cs:688` |
| C30 | The ready ring's faults aren't surfaced, and drop reporting holds one delegate | `src/Emerald.Video/SdiCapture.cs:140` |
| C31 | Pipe listener can spin on a constructor exception and hide failures from `OpenModule` | `src/Emerald.App/App.xaml.cs:250` |
| C32 | Monitor rebuilds its transmitted grid on any line, and can double lines | `src/Emerald.App/MonitorWindow.xaml.cs:180`, `:381` |
| C33 | EDL: hold during a cue uses the next message's post play | `src/Emerald.EDL/PlayoutService.cs:481` |
| C34 | EDL: a file that decodes to nothing respawns ffmpeg as soon as the previous one exits, at most once a frame, each `AudioSource.Open` running on the play loop | `src/Emerald.EDL/PlayoutService.cs:1143` |
| C35 | EDL: a folder is marked by its first file only, so the rest doesn't play by default | `src/Emerald.EDL/EdlWindow.xaml.cs:524` |
| C36 | EDL: Copy JSON cannot return the sent command | `src/Emerald.EDL/EdlWindow.xaml.cs:906` |
| C37 | EDL: capture port required on a TX-only system | `src/Emerald.EDL/EdlWindow.xaml.cs:679` |
| C38 | Ingest: closing during a recording leaves no result row | `src/Emerald.Ingest/Services/IngestSchedulerService.cs:518` |
| C39 | Ingest: real failure reason overwritten by "No file was produced" | `src/Emerald.Ingest/Services/IngestMediaRegistrar.cs:92` |
| C40 | Ingest: queue progress never moves on screen | `src/Emerald.Ingest/Services/IngestSchedulerService.cs:382` |
| C41 | Ingest: disk estimate formula is for ProRes and half raster, neither of which applies | `src/Emerald.Ingest/Services/DiskSpace.cs:73` |
| C42 | Ingest: simulate state isn't reconciled on recovery, so a real job can roll as simulated | `src/Emerald.Ingest/Services/IngestControllerService.cs:128` |
| C43 | Ingest: a Cancel and roll race can leave a recording under a Cancelled job | `src/Emerald.Ingest/Services/IngestSchedulerService.cs:368` |
| C44 | Ingest: default directory ignores the configured store | `src/Emerald.Ingest/Views/IngestControllerWindow.xaml.cs:111` |
| C45 | Board property errors are ignored, and an unopenable board shows as `DELTA-hd` | `src/Emerald.Deltacast/BoardService.cs:62` |
| C46 | SDK return values unchecked: embed audio, RX stream properties | `src/Emerald.Deltacast/SdiOutput.cs:190`, `src/Emerald.Video/SdiCapture.cs:298` |
| C47 | A late-starting signal permanently marks a good recording failed | `src/Emerald.Video/SdiCapture.cs:451` |
| C48 | Playout reuses an open output when only the rate changed | `src/Emerald.EDL/PlayoutService.cs:366` |
| C49 | **Fixed.** Preview placeholder text read "about every 0 min": integer division with a 30 s default. It now uses the offered label ("30 s"), as does the EDL deck's folder hint. | `src/Emerald.LiveEdit/LiveEditWindow.xaml.cs:1157` |
| C50 | The installers use the elevated account's `%APPDATA%`, so when the administrator is not the operator, the store setting goes to the wrong profile | `installer/install.bat:24` |
| C51 | `update.bat -q` with no folder takes `-q` as the source path | `installer/update.bat:25` |
| C52 | Uninstall's prompt says "settings and logs" but deleting removes all of `%APPDATA%\Emerald`, including `ingest.db` and bookmarks | `installer/uninstall.bat:78` |
| C53 | A process started with `--monitor` alone never connects the clock | `src/Emerald.App/MonitorWindow.xaml.cs:80` |
| C54 | `SdiCapture` logs the proxy as 960x540 when it is full raster, and the deck's profile note says half-size | `src/Emerald.Video/SdiCapture.cs:306`, `src/Emerald.App/ShellWindow.xaml.cs:561` |

## D. Potential risks

Not demonstrated. They depend on runtime behaviour or SDK semantics.

| Risk | Why it might matter |
|---|---|
| Ring creation on the UI thread | Extending a 41 GiB file and `Flush(true)` may stall all windows. Not measured. |
| Ring on the system drive by default | Constant heavy writes to `%LOCALAPPDATA%`. |
| Mapped-view I/O errors | May raise a structured exception rather than `IOException`. Whether `Write`'s catch sees it: Not verified. |
| Per-frame large allocations | **Fixed for capture.** The capture loop reuses its frame buffers (`FramePool`). `PlayoutService` and `FrameReadahead` still allocate per message and per cue, not per frame. |
| `Stop` returning while the worker lives | 2 + 6 s join versus up to about 15 s in `Finish`. A new `Start` could collide on the handle. |
| Short RX buffer | A short slot would misalign every later raw frame. Whether the SDK ever returns one: Not verified. |
| Kill after 5 s | `Kill(true)` leaves a file without its index if closing is slow. |
| Proxy and master boundaries differ | By design now: the master is cut at 5 minutes and the proxy shorter. Each is laid out on its own measured lengths, and exports plan on the masters' offsets (`RangePlan.FromMasters`), so the proxies' keyframe-late cuts do not move a master cut. |
| Audio request ratchet | `SdiAudioReader` steps its channel count down permanently on any failed read. |
| `RxPreview` field races | `_worker`, `_cts` and `_lease` are written from several threads without a lock. |
| Thread-safety of `SegmentWatch.Poll` | Overlapping tasks could duplicate lines. |
| UI-thread I/O | `ShowStorage` walks the store every 10 s, ingest validation writes a probe file per keystroke, `Overlay_Drop` runs ffprobe synchronously. |
| Fan timing | Sequential pushes: a stuck port delays each frame by up to 2 s. The in-step claim relies on an unverified genlock assumption. |
| Interlaced recording | 1080i is encoded with no interlace flags. Field flags: Not verified. |
| `VideoMasterHD_Audio.dll` absent | Not checked by the scan or the installer. It would surface as a generic failure on the first audio call. |
| Lock contention on real-time threads | `RecordingPicture` and `DropMarks` hold locks that the UI holds during copies or scans. |
| Wall clock versus timecode drift in ingest | A 29.97 generator treated as 30 drifts 0.1%, reaching the 5 s Missed window for bookings about 83 minutes ahead. |
| Bookmarks not written atomically | A crash mid-write loses that day's notes. |

## Unused modules and dead code

Confirmed by search.

| Item | Note |
|---|---|
| Shell stage: `Player`, cue logic, scrub, strip, `ClipThumbnails`, `ReportCodecs` | `StageMedia` is collapsed and nothing shows it. Roughly `src/Emerald.App/ShellWindow.xaml.cs:594-981`. |
| `PlaybackWindow`'s `_playout` and clip audition | Constructed, never given an entry |
| `IngestMode.Cut`, `IngestPlacement.TryPlanCut` | Nothing creates a cut, and only tests call `TryPlanCut`. Handling remains in the transmitter and timeline. It was retired by the EDL redesign, and the source itself does not say so. |
| `SdiOutput.SlotCounters` | No callers. So SDK drop counts are never read. |
| `PlayoutRequest.TxChannels` | No caller sets it |
| `EdlLedger.Clear`, `EdlLedger.Edits` | No callers |
| `TimelineZoom.WindowStart`, `TimeOfFrame`, `IngestTimeline.AddedFrames`, `IngestTimeline.BlankAt`, `LiveCues.DelayWith` | Tests only |
| `Overlay.StartFrame`, `Overlay.EndFrame` | No logic reads them |
| `SetAudio`'s `"delete"` branch | Its one caller passes `"level"` |
| `MediaCodec.IsMaster`, `Waveform.Known`, `SegmentWatch.HighestSequence`, `ClipNames.Sanitise` | No production caller |
| `AudioTrackRow.IsOnAir`, `OnAirText`, `MuteText`, `IsDefault` | Not bound or no behaviour |
| `AudioPairRow.LeftPeak`, `RightPeak`, `AudioMonitor.Reset`, `PairLevel` | No effective use |
| `EdlLogger.Logged` | No subscriber |
| `RecordingProfile.HalfRaster` | Called from a misleading log line (`SdiCapture.cs:306`) and from a branch that never runs, since `HalfSize` is false on both outputs (`RecordingProfile.cs:341`) |

## Duplicate logic

| What | Where |
|---|---|
| Pacer switch, read switch, buffer swap, stereo split, `Announce`, `Report`, start and stop | `DelayTransmitter`, `LiveAirTransmitter`. They have already diverged. |
| Stop sequence | `src/Emerald.App/ShellWindow.xaml.cs:1487` and `:2404` |
| Open and release of the three rings | `src/Emerald.App/ShellWindow.xaml.cs:1113`, `:1257`, `:1406` |
| Frame ledger display | `PlaybackWindow.ShowAirFrames`, `MonitorWindow.DrawFrames`, and the remote page, with different conditions |
| Log panel append and level-to-brush | At least four windows |
| Timecode wrapping `Stamp` | `LiveEditWindow`, `IngestDropDialog` |
| `BlankAt` | `IngestTimeline`, `LiveCues` |
| `EarliestChange` | Plan and cue versions in `EdlDiff` |

## Performance bottlenecks

Unmeasured, from reading the code.

- Large per-frame allocation in `SdiCapture`.
- `LiveEditWindow.Draw` clears and rebuilds all visuals, up to about 12 Hz when following air, creating unfrozen brushes and fonts.
- `HoldsOnRuler` rebuilds `IngestItems()` per overlay, reached every tick.
- `FollowEdl` runs LINQ every tick while live.
- `ActivityLog.LastSeq` uses `_ring.Last()` on a queue, inside the lock real-time threads use. It's read twice per remote poll.
- `ShowHeld` reads and converts a full frame on the UI thread each tick.
- All windows share one UI thread, so one window's stall affects all of them.

## Memory and resource management

- **Leaks found and fixed:** cue decoders (C3, both paths), the held ring claim (C17), and `MediaCodec.Cache`, which had no cap and kept an entry for every look at a file still being written. It is now capped at 20,000 and keeps answers only.
- **Bounded caches:** `MediaProbe` clears at 20,000, `Waveform` at 5,000.
- **SDK handles:** no leaks were found. All open paths close in `finally` or `Dispose`, stream before board. There is no finalizer.
- **Encoder threads:** ffmpeg's thread counts are stated on every recording, because its defaults follow the CPU count and held 3.7 GB on this machine. See [the flags](05_MEDIA_PROCESSING.md).
- **Rings** rely on reference counts, and on the startup sweep after a crash. A ring is memory-mapped, and it used to show as working set up to its full size: 41 GB for the default ready line at 1080p25 (120 s delay, 300 s headroom). `DelayLine.TrimBehind` now gives back pages once a second with `VirtualUnlock`, keeping the newest 10 s, or the farthest reader's distance plus 2 s if that is more (about 13 GB at a 120 s delay). The cost is that trimmed pages get written to the ring file, up to about 100 MB/s, on the system drive unless `tidalLockRingFolder` says otherwise; before trimming, a recording measured no writes to C:. Trimmed pages normally wait in the standby list, where a read or an overwrite takes a soft fault (about 2 ms a frame, measured on a test mapping). Under memory pressure they can be evicted, and then either one reads from disk. Lower headroom makes the ring smaller.
- **Ring size and release:** the ready ring holds about 100 MB/s of 1080p25 until it is full, then stops growing: about 29 GB at its 300 s minimum, 41 GB by default. **Delay ready** on the Capture deck turns it off. A ring is deleted without flushing it first. Flushing took 37 s and wrote about 5.9 GB to C: for a 4 GB ring; without it, the same release took about 1 s and wrote nothing.
- **Frame buffers are reused.** The capture loop used to allocate a 4 MB array for every frame, about 99 MB/s on the large object heap. `FramePool` now hands the same buffers round, with a count of holders on each (`FrameLease`): the loop, the encoder's queue and each ring's queue take a claim and give it up once they have copied the frame. A buffer goes back to the pool only when the count reaches nothing. One pool per recording; at most 16 idle buffers (66 MB) are kept.
- **Measured, recorder path without the card** (real ffmpeg command, real ring, 8-frame queues, 25 fps, 5 minutes each, quiet machine): per-frame allocation ran 294 full collections and 182 ms of GC pause, with the frame copy averaging 1.08 ms and 361 copies over 5 ms. Reused buffers ran 0 full collections, the copy averaged 0.69 ms and 0 copies were over 5 ms. No frames were dropped either way. A 30-minute run of the reused path: 0 full collections, 0 drops, managed heap 54 to 59 MB, ffmpeg 356 to 369 MB, one buffer reused for all 45,000 frames.
- **Measured on the running application** (recording, Live Edit open, 6 minutes, before these changes): private memory 506 to 516 MB and flat, ffmpeg 362 to 364 MB, handles and threads flat. Working set rose to 42.2 GB as the ready ring filled and then held.
- **Live Edit over a long run.** Live Edit read the whole store again every time a segment closed: a recursive file walk every 2 s, then a listing and probe of every file of every day, then a layout over all of them on the UI thread. Over a day that was O(n²) in the number of files, and past 20,000 files the probe caches emptied themselves partway through every listing. `StoreIndex` keeps what it has read: a refresh walks only folders that can have changed and reads only new or just-closed files, with a full walk every 60 s for deletions.
- **Live Edit per frame.** The timeline's edit list was sorted again on every lookup, several times a frame. It is now handed over in order and walked as it is. Finding the segment under the playhead is a binary search; drawing finds the visible run of segments by halving and draws sub-pixel segments and drop marks as one block per run or pixel. The scrollbar is set only when a value changes.
- **Live Edit over days.** Placed items, deleted scenes and sound edits that ended before yesterday are let go of at each day change (they used to be shifted back a day for ever). The player held ready behind the big screen and the small monitor close after 5 minutes idle. Waveforms are kept for an hour either side of the view. An export is called off when the window closes, and background work finishing after close leaves the window alone.
- **Other limits added:** `SegmentWatch` forgets segments the storage limit has deleted; `EdlLogger` allows 2,000 appends to wait on the disk and counts the rest; Live Edit keeps waveforms only for files on the timeline; ffprobe is killed after 15 s; playout copies its last frame into one buffer instead of cloning it per file.
- **Not tested on the card.** The pool, the ring release and the cue fixes are unit tested and were run in the harness, not through `SdiCapture` with a receiver.
- **Still open:** `TransmissionTally` grows while the Monitor window stays open; the `MediaProbe` and `Waveform` caches clear all at once at their caps (Live Edit no longer re-reads through them, but other callers do); the big screen's player keeps its file open while idle, because it shows the frame left on; `LOAD` is logged for every segment played through.
- **Window event subscriptions** to shared objects are removed on close. `_capture` and `_preview` subscriptions share the window's lifetime.

## Security

| Concern | Status |
|---|---|
| Authentication on the monitoring server | **None.** Verified. Anyone on the network can read the log when it is on. |
| Encryption | None. Plain HTTP. |
| CORS | `*`. Any page in a browser on the network can read it. |
| Firewall | `installer/install.bat` opens TCP 8787 **even if the feature is never used**. Nothing listens until switched on. Once switched on, the setting is saved, and it starts again whenever the Monitor window opens. |
| Connection limits | None, and no write timeout. A LAN client can hold tasks and sockets. |
| Page escaping | `esc` covers `&`, `<` and `>` but is used inside `title="..."`. Exception text with a double quote could inject attributes. Low severity. |
| Command injection | Low risk. ffmpeg arguments use `ArgumentList`. |
| `%` in ffmpeg paths | Neither the root folder nor the deck's segment-name prefix is sanitised. `SanitisePrefix` covers ingest names and `NamePrefix`, but the `RecordingNames` stem bypasses it. The title box is read-only, so only a hand-edited setting can carry a `%`. |
| Secrets in the repository | None found. The default timecode URL holds a private IP address. |
| State-changing routes | None. The server is read-only. |
| Installer | Requires elevation and writes to `HKLM` and the firewall |

## Scalability

Emerald is designed for one machine.

- **One process, one UI thread.** All windows share a dispatcher.
- **Per-recording cost:** about 105 MB/s to each ring and one ffmpeg per encoder. Each ingest job has its own `SdiCapture`, with no concurrency cap and no estimate of CPU capacity.
- **No sharing between machines** except the read-only page and copying folders.
- **Store listing** walks the file system, cached by probe. A store with thousands of files is the use case the managed atom reader exists for.
- **Timeline shows one recording folder** per day (`src/Emerald.LiveEdit/LiveEditWindow.xaml.cs:807`), despite comments suggesting otherwise.
- **Frame rate** is fixed per Live Edit window, read once.

## Missing error handling

- Unhandled exceptions on non-UI threads (B19).
- `async void` handlers with no try/catch: `ShellWindow_Loaded`, `Refresh_Click`, `StopRecording`, `Rescan_Click`, `Arm_Click`, `StopOutput_Click`, `PlaybackWindow.Window_Loaded`, `EdlWindow_Loaded`, `RefreshBoards_Click`, `IngestControllerWindow.Window_Loaded` and its `Rescan_Click`.
- `_ = Task.Run(line.Release)` without a catch (`src/Emerald.App/ShellWindow.xaml.cs:1192`, `:1463`).
- `PreviewFrame.Invoke` unguarded, and reads the field twice (`src/Emerald.Video/SdiCapture.cs:489`, `:495`).
- `RxLease.Freed` invoked unguarded inside capture's `finally`.
- `WaitForExit()` with no timeout in export (`src/Emerald.Media/RangeExport.cs:186`).

## Technical debt

| Item | Detail |
|---|---|
| God classes | `LiveEditWindow` about 7,750 lines, `ShellWindow` 2,731, `EdlWindow` 1,925. No view models, so behaviour in them can only be tested through the window. |
| Test gaps | No test references a window. The play loop and ingest scheduler loop are untested. |
| Undeclared dependency | Live Edit on Video and Deltacast |
| Settings copies | Several `AppSettings` instances |
| Event tags as an interface | `TransmissionTally` derives features from log event tags |
| Manual marshalling | Each handler must remember |
| Three frame coordinates | Plain `long`s with no type distinction, the source of B2 |
| Placements unsaved | A crash during a live session loses every edit on the timeline, while `LiveAir` also holds them only in memory |

## Stale documentation

Do not copy these without checking.

| Statement | Reality |
|---|---|
| `readme.md`: Live Edit is "layout only" | It is a full editor |
| `readme.md`: "no server, no network transport" | `MonitorServer` and `TimecodeService` exist |
| `readme.md`: everything reaching the transmitter goes through `PlayoutService` | Only the EDL uses it |
| `readme.md`, `AppSettings.cs:47`: 2-minute segments | Default is 30 s |
| `readme.md`: `capture_...` file names | `media_MMddyyHHmm_N` |
| `readme.md`, `.gitignore`, comments: ProRes | DNxHD 145 |
| `readme.md`, deck profile note (`ShellWindow.xaml.cs:561`): half-size H.264 proxy | Full raster |
| `readme.md`: "segmented MP4" | MP4 and MOV |
| Ingest "Exactly this", "verified and registered" | See A8, B10 |
| `PROTOCOL.md` | [Differences listed here](07_API_AND_COMMUNICATION.md#edl-record-format) |
| `CLAUDE.md`: `npm run build`, `npm test`, `npx graphify .` | No `package.json` exists |
| `RxPreview.cs:16`, `SdiCapture.cs:59`: 4:1, twelve fps | 2:1, every frame |
| `SdiCapture.cs:992`: "well under a second" | 2 seconds |
| `LiveEditWindow.xaml.cs:750`, `:801`: everything laid out, "not a filter" | Filters to one folder |
| `LiveAirTransmitter.cs:9`: deleted scene is skipped | It is blacked out |
| `StorageWarden.cs:132`: pairs by the timestamp in the name | By write time |
| `VideoMasterHD.cs:6`: "limited to enumerating boards" | Full surface |
| `uninstall.bat:5`: asks about the recordings | Asks about settings and logs |
| `SdiAudioReader.cs:255`: a silent pair is recorded to hold the position | Counting stops at the first silent pair |
| `IngestSchedulerService.cs:498`: `Move` is "the only way" a status changes | `Recover` writes it directly |

## Priorities

If fixing in order of on-air risk:

1. **A2, B24:** every drain and every source loss at a delay over half a second.
2. **A3:** every placed item with sound. Fixed; listen to one at the card.
3. **A7:** deleted picture reaching air.
4. **A1:** monitoring one port while recording another.
5. **A4:** any multi-language feed on the air path.
6. **B3, B4:** faults that look like everything is fine.
7. **B1:** the session-long block.
8. **A5, A6, A8.**

Each should get a failing test first where possible. A2 can be tested now, because `DelayPacer.Decide` is pure. A3 is tested through the decoder (`CueSoundTests`). A4 and A7 can be tested without a card only after their logic is moved out of the transmitters or an `ISdiOutput` is injected.

## What could not be fully analysed

| Area | Limit |
|---|---|
| Runtime behaviour | Nothing was run on a card. Timings, stall lengths and card output on underrun are unmeasured. |
| VideoMaster SDK | Headers aren't in the repository, so `allowSignalDetection`, `TX_OUTPUT=0`, slaved transfer, relay and genlock defaults, 3G-B and fractional rates are Not verified. Numeric values are as written in the code. |
| Timecode service | Only the client's contract is known. |
| Window XAML | Except for `LiveEditWindow.xaml`, reviewed for named controls, handlers and bindings rather than visual layout. |
| `App.xaml` | Summarised: palette and styles. |
| DNxHD 145 at 720p and 1440x1080 | `Supports` accepts them, and the code comment records testing only at 1920x1080. |
| Delayed-output audio at the card | What the SDK does with an over-long channel buffer (A4) is Not verified. |
| `VideoMasterHD_Audio.dll` | Install location Not verified. |
