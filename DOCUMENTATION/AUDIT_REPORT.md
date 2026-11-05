# Documentation audit report

This is the second-pass audit of the documents in this folder, against commit `dd8e4a7`. Only documentation changed. No application source code was modified.

## Result

The first version of these documents was mostly accurate on structure and names, but it was not safe to rely on for behaviour. Some flowcharts had the wrong order, and some instructions would not have worked. Those are now corrected.

The documents can now explain the system to a new developer without the original author, with one firm limit: **nothing here has been observed running on a card.** Every statement about behaviour comes from reading the code. [Unverified workflows](#unverified-workflows) lists what needs a card or a running app to confirm.

| Measure | Result |
|---|---|
| Documents audited | 13 |
| Findings raised | 179 |
| Corrections applied | 180 edits across 13 documents |
| Findings rejected after checking the code | 2 |
| Numbered faults tested in the assessment | 80 |
| Faults disproved | 0 |
| Faults narrowed in wording | 8 |
| New faults found and added | 10 |

## How it was done

Six independent reviewers each took a group of documents and the source, told to assume the documents contained errors. For every numbered fault in the assessment, the reviewer tried to disprove it by looking for a guard or compensating code. Each claim was compared with the code, and each finding had to cite the code.

Findings with the largest consequences were then re-read by hand before being accepted, and that is how the two rejections below arose. The remaining findings were accepted on their code citations without a second reading.

| Check | Method | Result |
|---|---|---|
| File and line references | Script, across all documents | All resolve |
| Class, method and module names | Script, against all source and scripts | All found, apart from framework, database index and example names |
| Links and anchors between documents | Script | All valid |
| Mermaid syntax | Parsed with Mermaid 11, and rendered for the Word versions | All parse and render |
| ffmpeg commands | Argument by argument, plus the worked example followed through `EncoderArguments` by hand | Correct after the fixes below |
| Delay ring sizes | Recomputed from the code's formula | All correct |
| Installer quoting | The fragment was run in isolation against a scratch settings file | Fault reproduced |
| Test suite | Run at this commit | 777 of 777 pass. None uses a card. |

## Documentation sections verified

| Document | Verified | Result |
|---|---|---|
| 1. Overview | Identity, modules, technology table, dependencies, feature status, glossary | Corrected: feature table, ffmpeg claim, clock failure behaviour |
| 2. Structure | All 33 Core, 17 Video, 9 Media, 7 Deltacast, 17 Ingest and 4 EDL files present. Dependency diagram: 22 of 22 edges match the project files. | Corrected: several "depended on by" cells, test coverage, diagram direction |
| 3. Architecture | Every diagram edge by edge, shared state, threads, timers, state machines | Corrected: 2 state diagrams, data flow, error flowchart. Overall diagram rewritten. |
| 4. Workflows | Each workflow traced from its entry method | Corrected: tidal lock end, ingest states, Go Live conditions, levels, placement modes |
| 5. Media | All ffmpeg arguments, capture order, ring layout, pacer, buffers, recovery | Corrected: recording and output flowcharts, underrun behaviour |
| 6. Hardware | All 17 P/Invokes, 22 constants, stream, standards and format tables, `VHD_AUDIOINFO` offsets | Tables correct. Corrected: receive flowchart, pair detection, signal-loss behaviour |
| 7. API | All routes, payload fields, timecode client, events, log tags, handshake | Corrected: log example, event table, tag list |
| 8. Storage | All 43 database columns, all 45 settings, naming, inventory, warden | Corrected: WAL mode, defaults, migration rule |
| 9. Install | Every command and script behaviour | Corrected: uninstall prompt, update behaviour, EDL product |
| 10. Troubleshooting | Quoted messages, event tags, workarounds | Corrected: 3 workarounds that did not hold or were incomplete, 1 misquoted message |
| 11. Assessment | 80 numbered faults, dead code, duplicates, stale documentation, security | 72 confirmed unchanged, 8 narrowed, 10 added |
| 12. Developer guide | Every instruction | Corrected: 4 instructions that were wrong or would have failed |
| README | Every statement | Corrected: 4 statements and its diagram |

## Corrections made

These mattered most. A reader acting on the old text would have been misled.

### Behaviour that was wrong

| Document | Old statement | Reality |
|---|---|---|
| 3, 5, 6 | A delayed feed fails after 10 seconds with no frames | True only at delays of 0.5 s or less. At any longer delay the feed holds a frozen, silent frame until stopped. Added as B24. |
| 3 | A ring write fault fails the lock | It seals the ring and calls no `Fail`. Added as B26. |
| 3 | `FormatDetected` is not protected from a throwing handler | It is caught, and it fails the recording on purpose. `Message` is the unprotected one. |
| 4 | A stopped recording drains and returns to Armed | Not reached at delays over half a second. A stop during the countdown leaves the transmitter holding black. Added as B25. |
| 5 | Recording flowchart: Midnight, then Stop, then Lock | The order is Stop, then Midnight, then Lock. |
| 5 | Output flowchart: read the ring, then ask the pacer | The pacer decides first. A cue already playing skips the pacer entirely. |
| 8 | The database has no WAL mode | EF Core sets WAL when it creates the file, so companion files exist. Moving a machine needs them too. |
| 11 | Encoder errors carry over into ingest jobs | Ingest builds a new `SdiCapture` per job. Only the deck and the EDL carry over. |
| 11 | Pending ingest jobs fail on reopening | Only those whose start has passed. The rest are re-armed. |

### Instructions that would not have worked

| Document | Old instruction | Problem and fix |
|---|---|---|
| 10 | After an output failure, clear the queue | No control can clear QUEUED entries. Close and reopen the EDL window. |
| 10 | Start a recording to save the storage limit | That only reaches memory. The deck must also close normally. |
| 9 | `uninstall.bat` asks about recordings | It asks about settings and logs, and answering yes deletes the database and bookmarks too. |
| 9 | `install.bat <path>` sets the store | Only when no settings file exists. |
| 12 | A new endpoint builder returns an object | It must return a JSON string, or it won't compile. |
| 12 | The steps for adding a window | A required step was missing: without it the window can never be reopened. |
| 12 | `build.bat` in PowerShell | PowerShell needs `.\build.bat`. |
| 12 | Fakes exist for `ISdiOutput` and `IIngestHardware` | Neither has one, so transmit loops can't be tested without a card. |

### Diagrams

| Diagram | Change |
|---|---|
| Overall architecture | Rewritten. It was too dense to read and missed real paths. It now shows layers, with a table for who drives what. |
| Project dependencies | Direction fixed, so Core sits at the bottom |
| Data flow | Cues now go to `LiveAirTransmitter` only. Preview comes from the slot, not the copy. |
| Tidal lock, Live Air states | Added the transitions the classes allow |
| Ingest states | Removed a transition nothing reaches, and added the recovery transitions |
| Tidal lock, Go Live sequences | Added the conditions that were hidden |
| Recording, output, receive flowcharts | Rebuilt to match the actual order |
| Error flowchart | Separated ring create from ring write, and corrected signal loss |
| API sequence | Added the transmitted poll and the output pipes |

### Faults added to the assessment

B24 signal loss never fails a long delay. B25 a stop during the countdown leaves the transmitter running. B26 a ring write fault holds the feed. B27 audio after a silent pair is lost. B28 the installers misread the store on an update. C50 to C54 cover the installers' account handling, `update.bat -q`, the uninstall prompt, `--monitor` with no clock, and the misleading proxy size. The assessment's A2 was also extended: a recording stopped before a whole delay has accumulated holds black forever.

### Findings rejected

The code contradicted these two, so they were not applied.

| Finding | Why it was rejected |
|---|---|
| `Profile_Changed` contains no log call | It logs `capture.profile`. The original text was right. |
| Ring tests don't evidently need free disk space | During development, 19 delay-ring tests failed on the free-space guard when the temp drive was full. |

## Requested checks

| # | Check | Result |
|---|---|---|
| 1 | Documented functions, classes, modules, APIs and services exist | Yes. Two types were attributed to the wrong file, and are now fixed. |
| 2 | Workflows follow the real sequence | Yes, after correction. 24 findings on document 4. |
| 3 | Diagrams represent the real implementation | Yes, after correction. See the table above. |
| 4 | Frontend, backend and hardware paths | Yes, after correction. There is no network boundary between the UI and engines. |
| 5 | Recording, playback, streaming and media details | Yes, after correction. There is no network streaming, as the documents say. |
| 6 | Configuration values and startup commands | Yes. All 45 settings and all commands checked. Script behaviour corrected. |
| 7 | Planned features described as implemented | None found. Unreachable features are labelled as such: the capture deck's clip stage, `IngestMode.Cut` and multi-port EDL playout. |
| 8 | Secrets or credentials exposed | None. See below. |

### Secrets

No passwords, keys, tokens, email addresses, user paths or real machine names appear. Three items were reviewed and left in, because they are configuration or public facts already in the repository. You may want to remove them if these documents go outside the company:

| Item | Where | Note |
|---|---|---|
| `http://10.0.0.31:8888/api/timecode` | Documents 7, 8, 12 | A private site address and the source's default |
| GitHub owner in the clone URL | Document 9 | A personal account name, also in `readme.md` |
| Publisher "Peregrine Broadcast" | Document 1 | A company name, from `installer/install.bat` |

## Missing information

Not in the documents, because it cannot be established from this repository:

| Item | Who can supply it |
|---|---|
| VideoMaster SDK semantics: `allowSignalDetection`, `TX_OUTPUT=0`, slaved transfer, relays, genlock, 3G-B | DELTACAST headers and documentation |
| Install location of `VideoMasterHD_Audio.dll` | DELTACAST installer |
| RX count of the DELTA-12G-elp-h 20: the readme says 8, the simulator says 4 | A real board |
| The real timecode service's response, and its rate and type values | The service, or its owner |
| Measured latencies: ffmpeg start, ring creation, cue accuracy | A test run |
| Minimum CPU, RAM and disk specification | Plant testing |
| Whether `assets\` is available to a new developer, since it isn't in git and the projects need it | Original developer |
| Whether retiring `IngestMode.Cut` is permanent | Project owner |

## Unverified workflows

These read correctly in the code, but only a running system can confirm them.

**Needs a card:**

- The drain freeze, the frozen frame on signal loss, and black after a stop during the countdown (A2, B24, B25).
- Cue audio as heard on air (A3), and over-long audio buffers at the card (A4).
- A deleted scene's picture during a Repeat (A7).
- Slot pacing, the 2-second IO timeout, and whether lock failures wait for it.
- Handover from preview to recorder, and the preview's return.
- Midnight rollover with a live recording and delay.
- Frame accuracy of ingest rolls and EDL cues.
- Multi-port fan-out timing.
- DNxHD 145 at 1280x720, 1440x1080 and 1080p50.

**Needs a running app:**

- Seamless `MediaElement` swaps, chase behaviour and held-picture playback.
- Whether `MessageBox.Show` really throws when the shell closes Live Edit.
- Export accuracy after an insert (B2).
- Single-instance handoff across real processes.
- Remote monitoring over a real network.
- WAL mode on a real database, confirmed so far only from the EF Core provider's code.
- The full install script on an elevated machine. Only the failing fragment was run.

## Potential architectural issues

Details are in the [assessment](11_TECHNICAL_ASSESSMENT.md).

| Issue | Why it matters |
|---|---|
| **Delay pacing has no concept of a sealed or dead source** | One rule causes the drain freeze, the unreachable failure and the endless hold. It decides what happens at the end of every delayed feed. |
| **Two transmitters with copied logic** | They have already diverged: Live Edit publishes no drop counts, marks or pipeline state. Fixes must be made twice. |
| **No test seam for the output** | The transmitters open `SdiOutput` directly, so what actually goes to air can't be tested without a card. Three of the top five faults are in this code. |
| **God classes** | `LiveEditWindow` is about 7,750 lines and `ShellWindow` 2,731. No test reaches them, and the `_loading` fault survived there. |
| **Three frame coordinates as plain `long`s** | Ruler, captured and recording frames can be mixed without complaint. That caused the export fault. |
| **Windows own engine lifetimes** | Closing the ingest window stops the scheduler. Closing the Monitor stops the server. Closing Live Edit ends the transmission. |
| **Live Edit's state lives only in memory** | A crash during a live session loses every placement. |
| **Settings copies** | Several `AppSettings` instances each save the whole file, so saves undo each other. |
| **Log event tags as an interface** | `TransmissionTally` builds a feature from tags, so renaming one can silently break it. |
| **No unhandled-exception handling off the UI thread** | A real-time thread failure ends the process with no record. |
| **Monitoring server has no authentication** | It's read-only and off by default, but it exposes paths and exception text on the network when on. |

## Recommended next documentation improvements

In priority order:

1. **Run the [unverified list](#unverified-workflows) on a card,** and record the results in these documents. This is the biggest gap.
2. **Fix the stale comments in the source.** The [stale table](11_TECHNICAL_ASSESSMENT.md#stale-documentation) lists them. One misled this documentation's first version, and they will mislead the next reader.
3. **Update `readme.md`, `PROTOCOL.md` and `CLAUDE.md`,** or replace their overlapping sections with links here. `CLAUDE.md`'s `npm` commands don't apply.
4. **Add an operator manual.** These documents are for developers. Nothing explains the decks to the people who use them.
5. **Add a runbook for going live,** with what to check beforehand and what to do when something fails on air.
6. **Add a sample `settings.json` and a sample timecode response** taken from a real site, with private values removed.
7. **Add test-plan notes** listing what each fault needs in order to be tested.
8. **Automate the checks.** The reference, link and diagram checks could run in CI, so these documents can't drift unnoticed. Line numbers will drift with every commit, so consider citing method names instead.
