# 10. Troubleshooting

Messages are quoted from the code, except the ffmpeg errors, which are ffmpeg's own output. "Known issue" entries are described in the [assessment](11_TECHNICAL_ASSESSMENT.md). They are faults in the code, not setup mistakes.

## First steps for any problem

1. **Open the Monitor** (`--monitor`, or the deck's button). It shows every stage, the machine's CPU, memory, graphics and disks, and the whole log. Filter by area (choose **Errors** for every warning and error), day and level. Double-click a line to filter by its correlation ID. An earlier day is read back from the database, as far back as `logRetentionDays` (14).
2. **Read the database** if the Monitor is not enough. Everything is in `emerald.db` in the data folder (`dataFolder`, `%APPDATA%\Emerald` by default). Open it read-only with any SQLite tool. `log_error` holds every warning and error with the area it came from; `log_edl` holds each EDL entry whole, as JSON in `detail`. **Export** in the Monitor saves the rows shown, and **Open folder** opens the database folder.
3. **Search by event tag** (below). Tags are more reliable than message text.

```sql
select at, area, source, event, message from log_error order by at_ms desc limit 200;
```

## Log events worth knowing

| Event | Meaning |
|---|---|
| `app.crashed` | UI thread exception. The log is flushed to the database straight after it. |
| `app.databasefailed` | `emerald.db` could not be opened in the data folder, and maybe not in `%APPDATA%\Emerald` either. If neither opened, `app.started` says the record is in memory only. |
| `log.dropped` | Lines the database could not take. Reported once it keeps up again. |
| `delay.samedisk` | The ring folder was on a recording drive, so the default `%LOCALAPPDATA%\Emerald\delay` is used instead |
| `capture.problem` | A problem shown on the deck |
| `capture.dropped` | Encoder could not keep up |
| `capture.newday` | Recording moved to the next day's folder |
| `capture.rollfailed` | Next day's folder could not start. The recording stays in the old folder. |
| `delay.trouble` | Tidal lock repeated or skipped frames |
| `delay.dropped` | Ring could not keep up |
| `tidallock.failed` | Tidal lock failed |
| `liveair.failed` | Live Edit's transmission failed |
| `rx.stuck`, `rx.taken` | Receiver handover |
| `media.pruned`, `media.limitfailed` | Storage limit activity |
| `playout.audiofailed` | An audio track could not be decoded, or decoded to nothing, and is silent |
| `edl.operationFailed` | EDL step failed. The whole entry is in `log_edl`, as JSON in `detail`. |

## Startup

| Symptom | Cause | Fix |
|---|---|---|
| "VideoMasterHD.dll was not found" | Driver not installed | Install DELTACAST's installer. The app still opens. |
| "...could not be loaded. The application must run as 64-bit." | A project was added without `<Platforms>x64</Platforms>` | Fix the project |
| "No DELTACAST board detected" | Driver present, no card | Check the card. Ingest has SIMULATE. |
| "Board i (model) could not be opened" | Another application holds it | Close dCARE and others. The board stays listed with no ports, possibly under the wrong type name. |
| Nothing happens on launch | An instance is already running and took the request | Look for the window. Check Task Manager. |
| Message box about an unknown option | Argument other than `--edl`, `--ingest`, `--monitor` | Fix the shortcut |
| Blank or corrupt rendering | GPU or remote session | Close Emerald, set `softwareRendering` in `settings.json` or `EMERALD_SOFTWARE_RENDER=1`, then start it. |
| Build fails on a locked file | Emerald is running | Close it |
| All settings went back to defaults | `settings.json` was corrupt, which is silent | Restore from a copy. There is no automatic backup. |
| The Monitor shows no earlier days, and the footer says "not being written to disk" | `emerald.db` could not be opened, so the record is memory only. See `app.databasefailed`. | Check the data folder exists and is writable, or clear `dataFolder` in `settings.json` (Emerald closed) to use `%APPDATA%\Emerald`. |

## Clock

| Symptom | Cause | Fix |
|---|---|---|
| Clock reads `--:--:--:--`, status red | Timecode service unreachable | Check the URL and network. Open the URL in a browser. |
| Ingest cannot be scheduled | No clock | As above |
| An EDL message played immediately | No clock at dequeue, or a start more than 12 hours ahead | Known issue. Check the clock first. Avoid far-future starts. |
| A timecode was read wrong at 29.97 | Drop-frame is not implemented, so it's treated as 30 | Known limitation |

## Recording

| Symptom | Cause | Fix |
|---|---|---|
| "RXn is already open in another application" | dCARE or another app holds the receiver | Close it |
| "RXn on board b is in use by owner" | Another Emerald module holds it | Stop that module |
| "no signal on RXn after 2s" | No feed, or wrong port | Check the source and cable |
| Recording refused for this raster | PAL and NTSC can't go to DNxHD | Not supported |
| "Capture: the encoder stopped at frame N" | ffmpeg exited. The reason follows in the log. | See the next section |
| Dropped frames | Disk too slow, or CPU too busy | Faster disk, and check `capture.dropped` |
| After changing board or port, the preview stays on the old receiver and the port list does not update | Fixed in this build (`_loading` is now cleared after start-up). On an older build, the next recording uses what the boxes show, not what the preview shows. | On an older build, press Refresh after changing. |
| Storage limits not saved | Known issue, same cause | Start a recording after choosing them, then close the deck normally. Starting the recording copies both limits into the in-memory settings, and closing the deck saves them. A crash before that loses them. The limits are enforced during recording either way. |
| 2-minute segment length resets to 30 s after a restart | Known issue, same cause | Choose it again after starting. |
| A high-res or low-res path typed on the deck is not seen by Live Edit | Known issue, same cause: leaving the box does nothing | Use the folder button beside the box, which saves at once, or press Record, which saves both. |
| High-res and low-res paths greyed out | A recording is running. The folders are locked until it stops, because the recorder, the segment watch and the storage limit are told them once. | Stop the recording first. |
| Signal lost mid-recording, no warning | Known issue: not reported once frames have arrived | Watch the deck's frame count. |
| A good recording is logged "stopped early" | Known issue: every deck recording | Ignore this line |
| A later recording shows an old encoder error | Known issue: `_encoderErrors` is never cleared | Restart Emerald to clear it |

### ffmpeg errors

| Message | Likely cause |
|---|---|
| ffmpeg's own text, shown after `Capture encoder:`. For example, "Error submitting a packet to the muxer: Invalid argument" or "return code -22". | ffmpeg's muxer rejected a packet, and the encoder stopped. The deck now stops within about a second instead of running on unnoticed. The root cause is not established: try another output folder or drive and check its file system, permissions and free space. |
| "ffmpeg was not found" | Not installed or not in the search path. Set `ffmpegPath`. |
| No frames encoded, but the deck ran | Look for `capture.problem` lines and "Capture encoder:" text |

## Delay and tidal lock

| Symptom | Cause | Fix |
|---|---|---|
| Lock fails with a sentence about disk space | Ring needs its size plus 2 GB free | Free space, or close Emerald and set `tidalLockRingFolder` to a bigger drive in `settings.json`. |
| Format refused for delay | Only progressive 1080 at 24, 25, 30, 50, 60 can be delayed | Not supported |
| Picture freezes shortly after stopping a recording | Known issue: `DelayPacer` Repeat when sealed | Keep recording until the programme has aired, or stop manually and accept the loss |
| Output stays black after a recording stopped during the countdown, and the next recording does not go to air | Known issue: the transmitter keeps filling and blocks the next start | Disarm, which stops the feed, then arm again |
| Picture freezes when the source stops, and never fails | Known issue: at delays over 0.5 s the 10-second failure is not reached | Check the source. Stop the feed by hand if it does not come back. |
| Feed drops, deck still shows ON AIR | Known issue: some TX failure paths report nothing | Check the card and restart the feed |
| Port marks stay lit after stopping | Known issue | Cosmetic. Close and reopen the deck. |
| Audio garbled on a multi-language feed | Known issue: multi-pair audio is treated as stereo | Use a single-pair feed in the delay path until fixed |
| "The receiver has stopped delivering frames. Cutting to black." | No frames for 10 s, at a delay of 0.5 s or less | Check the source |
| Black frame during the feed | Ring lapped, so resync | Check `delay.dropped` and disk speed |

## EDL playout

| Symptom | Cause | Fix |
|---|---|---|
| SEND is disabled | The first problem shows under the button | Fix that field |
| "No capture port available." on a TX-only machine | Known issue: required even with recording off | Known limitation |
| Audio is ahead of picture | Known issue: SOM isn't applied to embedded audio | Avoid SOM on clips using their own audio |
| Only the first file of a folder played | Known issue: EOM is seeded from the first file | Set EOM equal to SOM, or set a longer duration |
| Next message never starts | An open-ended message blocks the queue, and STOP clears the queue too | Set a duration |
| Queue shows QUEUED but nothing runs | Known issue: engine exits after an output failure | Close the EDL window and reopen it, which discards the queue, then SEND again. Do not SEND first: the old entries run first. "Clear finished" does not remove QUEUED entries, and STOP is hidden after the failure. |
| Changing the form affects what's on air | Known issue: live audio state belongs to the form | Don't edit audio tracks while on air |
| Copy JSON shows the wrong ID | Known issue: it copies the next pending command | Copy before SEND |

## Ingest

| Symptom | Cause | Fix |
|---|---|---|
| Job booked for tomorrow | NOW, then START, puts the time just in the past | Add time before START. Known issue. |
| Second back-to-back job fails, "already recording" | Known issue: the first runs past its end by at least 2 s | Leave a gap of several seconds |
| Pending job failed on reopening | Known issue: scheduler runs only while the window is open | Keep the window open |
| Clip starts 2 s late | Known issue: audio pair detection | Start the job earlier |
| Job length is half or double | Known issue: clock rate and wire rate differ | Match the rates |
| Job failed "No file was produced" | Real cause is in the log | Read the Ingest lines before it |
| Complete file marked Failed | Known issue: any ffmpeg error line marks it failed | Check the file itself |
| Progress does not move | Known issue | Cosmetic |

## Live Edit

| Symptom | Cause | Fix |
|---|---|---|
| "NO EDIT ZONE" | The change is within 10 s of air | Work further ahead |
| Header shows `NOT ACKNOWLEDGED` | Transmitter did not read 3 attempts | Ctrl+Shift+E, and check the transmitter's state |
| All edits refused after one | Known issue: a TooLate change blocks later versions | Undo that change. If undo also refuses, it stays until off air. |
| Last segment stays "still being recorded" | Known issue: fingerprint excludes the newest file | Press Refresh or Sync. |
| Timeline does not change day | The above, or something is playing by hand, or you chose a different day | See [the rules](05_MEDIA_PROCESSING.md#midnight-rollover) |
| Cue audio buzzes | Fixed (A3): the sound of a placed item was read at the wrong position. A build from before the fix still does it. | Update to a build with the fix. If it still buzzes, check the file's own audio. |
| Deleted picture briefly on air | Known issue: Repeat during a deleted scene | Check `delay.trouble` |
| Export is off by the length of an insert | Known issue: ruler and captured frames mixed | Export before placing inserts |
| Placements gone after closing | Not saved, by design | Finish before closing |
| Clip will not play | Master can't play, as Media Foundation has no DNxHD decoder. Proxy might be open. | Proxy is used automatically. Check the low-res folder. |
| "Cannot go live: TXn (owner) already in use" | Another module holds it | Stop that module |

## Monitor and remote page

| Symptom | Cause | Fix |
|---|---|---|
| Page can't be reached | Monitor window closed, Remote off, or firewall | Open it and switch on. Add the firewall rule it shows. |
| "Pick another" port error | Port is taken | Close Emerald, edit `monitorApiPort` in `settings.json`, start Emerald, switch REMOTE on, and run the firewall command the Monitor shows, because the installed rule covers 8787 only. |
| Page shows few old lines | Known issue: `since` jumps to newest | Keep the page open from the start |
| Page stalls after a restart | Known issue: `Seq` restarts | Reload the page |
| Red BEHIND count on the page | Known issue: false count when armed mid-recording | Trust the deck |
| INGEST stage is always idle | Known issue: nothing writes it | Look at the Ingest window |
| Live Edit's air isn't on the strip | Known issue | Look at Live Edit's header |
| No GPU tile | The machine has no graphics engine counters | Nothing to fix. CPU, RAM and disks still show. |
| An earlier day shows fewer lines than happened | More than the limit: the window holds 20,000 rows, the remote page the newest 5000 of a day | Narrow by area, or read `emerald.db` directly |

## Hardware checks

1. Confirm `C:\Windows\System32\VideoMasterHD.dll` exists.
2. Close dCARE and anything else using the card.
3. Rescan in the deck. Read `board.scanned` and `board.scanfailed`.
4. Check each port's cable by previewing it.
5. Try another port to tell a cable from a card.
6. For TX, check `SdiPorts` ownership shown in the playback deck.

`SdiOutput.SlotCounters` can read the card's own TX slot and dropped-slot counters, but nothing calls it, so frame counts in the UI are application counts. Use dCARE for card-level checks, with Emerald closed.

## Disk problems

| Symptom | Check |
|---|---|
| Tests or the ring refuse, "The delay line needs X on D:\ for ... and only Y is free." (X and Y in MB or GB) | Free space on the ring's drive |
| Unexpected disk growth | The exports folder (exports and snapshots), thumbnails and `ingest.db` aren't limited by any rule. `emerald.db` keeps `logRetentionDays` of rows but does not shrink on disk. With the folders chosen apart, exports sit inside the high-res folder but are not counted towards its limit. |
| Very large files in `%LOCALAPPDATA%\Emerald\delay` | Rings. They delete on release or at the next start. Close Emerald first. |
| Old recordings vanished | `StorageWarden`. Look for `media.pruned`; each line says high-res or low-res. |
| Proxies gone but masters still there | The low-res limit. It deletes only proxies; their masters stay until the high-res limit takes them. |
| "only N GB is free where this recording is going" | Both recording drives are checked; the folder named is the one with less room |

### A disk shows 100% active

The Monitor's tile for a drive, for example `Disk 1 (E:)`, shows its active time as Task Manager does (100 minus idle time). 100% means the disk is busy all the time, not that it is full.

**What was measured on the plant machine.** E: is one Seagate ST3000VN007 (IronWolf, 5900 rpm SATA hard disk). The recorder writes about 15 to 16 MB/s there (DNxHD at 25p is CID 1238, about 121 Mb/s) in IOs of about 850 KB, and in steady recording E: was 85 to 90% idle, with no spike when a master closed. So the writer alone does not fill the disk. 100% comes from **readers making the head seek** on the drive being written to.

Readers on the recording drive, and what was done about each:

| Reader | Now |
|---|---|
| Live Edit waveforms, which decode a whole file's sound | Read once per segment and kept in `<data folder>\peaks`. One ffmpeg at a time, at very low disk priority. An overlay's sound comes from its small `.preview.mp4`. |
| Live Edit's store index opening new segments | 2 files at a time, not 6. Each finished file's length is read once for the whole app (`MediaLengths`), shared with the storage limit. |
| Storage limit measuring the folders | Sizes from the directory listing, without opening files. Shown from the sweep's own measurement. |
| Exports and their previews | Below-normal CPU and very low disk priority, so the recorder's writes go first |
| Proxies read by Live Edit, on the same drive as the masters | Choose a low-res folder on another drive (capture deck, **Low-res path**) |
| A delay ring on the recording drive | Refused: the default `%LOCALAPPDATA%\Emerald\delay` is used instead (`delay.samedisk`) |

What to check:

1. In the Monitor, compare the drive's **R** and **W** figures. High reads with a steady ~15 MB/s write means readers.
2. Is the low-res folder on the same drive as the high-res one? Move it to another drive if one is available.
3. Exclude the high-res, low-res and database folders from Windows Search indexing and from Defender real-time scanning. Both read new files as they are written. This is outside Emerald.
4. Is Live Edit open on a day it has not shown before? The first view of each segment reads its sound once; after that it comes from the cache.
5. Is an export running? It reads the masters, at low priority.

**RAM rising while recording and on air.** This was the delay ring, not the segment length or Live Edit: on air with a 120 s delay, Emerald's working set reached 14.3 GB (745 MB of it its own memory) and Windows held 28 GB of the ring as modified pages. Fixed: the ring keeps about 4 s in memory and writes the rest ahead to its file. If RAM still climbs, look at the Memory gauge "ring not yet on disk": a growing figure means the ring's drive cannot keep up with about 104 MB/s; put `tidalLockRingFolder` on a faster drive that is not a recording drive.

**Not changed: the delay ring's write load.** A 420 s ready line at 1080p25 is about 44 GB on C:, and while held its write-back was measured at roughly 80 to 100 MB/s to C:. That is inherent to holding that much picture, and it loads C:, not the recording drive. Only a shorter delay and headroom, or **Delay ready** Off, reduce it. An earlier suspicion that creating the ring zero-fills 44 GB at once was tested and not confirmed: creation took milliseconds, so the ring code was left as it was.
