# 10. Troubleshooting

Messages are quoted from the code, except the ffmpeg errors, which are ffmpeg's own output. "Known issue" entries are described in the [assessment](11_TECHNICAL_ASSESSMENT.md). They are faults in the code, not setup mistakes.

## First steps for any problem

1. **Open the Monitor** (`--monitor`, or the deck's button). It shows every stage and the whole log. Filter by source and level. Double-click a line to filter by its correlation ID.
2. **Read the log files** in `%APPDATA%\Emerald\logs`, or `logFolder` if set. Each day has one `emerald-yyyyMMdd.jsonl`, plus `-001` and onward past 32 MiB, and those are swept at startup after `logRetentionDays` (14). `edl-yyyyMMdd.jsonl` is kept indefinitely.
3. **Search by event tag** (below). Tags are more reliable than message text.

```powershell
Select-String -Path "$env:APPDATA\Emerald\logs\emerald-*.jsonl" -Pattern '"Level":"Error"'
```

## Log events worth knowing

| Event | Meaning |
|---|---|
| `app.crashed` | UI thread exception. The line can be lost, since the log isn't flushed. |
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
| `edl.operationFailed` | EDL step failed. Look in `edl-yyyyMMdd.jsonl`. |

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
| After changing board or port, the preview stays on the old receiver and the port list does not update | Known issue: `_loading` is never cleared. The next recording uses what the boxes show, not what the preview shows. | Press Refresh after changing. That saves the choice, refills the ports and moves the preview. |
| Storage limit not saved | Known issue, same cause | Start a recording after choosing it, then close the deck normally. Starting the recording copies the limit into the in-memory settings, and closing the deck saves them. A crash before that loses it. The limit is enforced during recording either way. |
| 2-minute segment length resets to 30 s after a restart | Known issue, same cause | Choose it again after starting. |
| Output folder not seen by Live Edit | Known issue, same cause | It takes effect on Record |
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
| Clip will not play | Master can't play, as Media Foundation has no DNxHD decoder. Proxy might be open. | Proxy is used automatically. Check `low\`. |
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
| Unexpected disk growth | `edl-*.jsonl`, `ingest\`, snapshots, thumbnails and `ingest.db` aren't limited by any rule |
| Very large files in `%LOCALAPPDATA%\Emerald\delay` | Rings. They delete on release or at the next start. Close Emerald first. |
| Old recordings vanished | `StorageWarden`. Look for `media.pruned`. |
