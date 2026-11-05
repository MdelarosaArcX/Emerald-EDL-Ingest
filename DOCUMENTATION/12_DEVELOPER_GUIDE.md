# 12. Developer guide

For a developer new to Emerald. Line references are to commit `dd8e4a7`.

## Understanding the application

Read these first, in order:

| # | File | Why |
|---|---|---|
| 1 | [This documentation's overview](01_PROJECT_OVERVIEW.md) and the [glossary](01_PROJECT_OVERVIEW.md#glossary) | Vocabulary |
| 2 | `src/Emerald.App/App.xaml.cs` | Why there is one process |
| 3 | `src/Emerald.Core/RecordingSequence.cs` | Short, and explains naming and the midnight count |
| 4 | `src/Emerald.Video/SdiCapture.cs` | The heart: the slot loop |
| 5 | `src/Emerald.Video/RecordingProfile.cs` | What ffmpeg is asked to do |
| 6 | `src/Emerald.Video/DelayLine.cs`, `DelayPacer.cs` | How the delay works |
| 7 | `src/Emerald.Video/DelayTransmitter.cs` | The simpler transmitter |
| 8 | `src/Emerald.Core/TidalLock.cs` | The simpler state machine |
| 9 | `src/Emerald.Core/IngestTimeline.cs`, `LiveAir.cs`, `EdlLedger.cs` | Live Edit's rules without the UI |
| 10 | `src/Emerald.Media/MediaLibrary.cs` | How files become a timeline |

Leave `LiveEditWindow.xaml.cs` and `ShellWindow.xaml.cs` until you need them. They are large, and they use all of the above.

Comments are unusually thorough, and often explain a measured failure. Treat them as history though: [some are out of date](11_TECHNICAL_ASSESSMENT.md#stale-documentation). The code is the authority.

The code graph helps with navigation. `graphify update .` refreshes it, `graphify-out/GRAPH_REPORT.md` lists the most connected classes, and `graphify query "..."` searches it. `graphify-out` is git-ignored and `graphify` is installed separately, so a fresh clone has neither. Install it and generate the graph first.

**Before you start:** install the .NET 8 SDK and ffmpeg, and get the `assets\` folder, which is not in git but which the projects include as resources. Always build the solution, since a direct project build lands in a different output folder. The default timecode URL (`http://10.0.0.31:8888/api/timecode`) won't answer off-site: real-mode ingest then refuses to schedule, and the EDL plays immediately. Settings, logs and the ingest database are described in [data and storage](08_DATA_AND_STORAGE.md). `CLAUDE.md` lists `npm` commands that don't apply here.

## Rules of the codebase

1. **x64 only.** Every project needs `<Platforms>x64</Platforms>` and `<PlatformTarget>x64</PlatformTarget>`, `<UseWPF>true</UseWPF>` if it has windows, and an entry in `Emerald.sln` mapping `Debug|Any CPU` and `Release|Any CPU` to `x64`.
2. **References run one way:** Core, Deltacast, Video, Media, then the UI projects. Never add a cycle. If Core needs to see something above it, add an interface in Core, as with `IDelayLine` and `IEdlPlayback`.
3. **Never block a real-time thread.** The capture slot loop, `DelayLine.Offer` and transmitter loops can't wait on a disk, a lock held by the UI, or a subscriber. Use a bounded queue and count what you drop.
4. **A failure of the delay must not stop a recording.**
5. **Frames are the clock.** Don't derive positions from `DateTime.Now`. Wall-clock time is for display and for mapping to a day.
6. **Know your coordinate.** Ruler, captured and recording frames are all `long`. See [the table](04_SYSTEM_WORKFLOW.md#live-edit-building-the-timeline).
7. **Marshal to the UI** with `Dispatcher.BeginInvoke` in any handler raised on an engine thread.
8. **Operator errors are sentences.** Return a reason rather than throwing. Log with an `@event` tag.
9. **Put pure rules in Core** with time passed in, and test them.
10. **Don't change an event tag casually.** `TransmissionTally` reads them.

## Add a new feature

1. Decide the layer. Rules with no hardware go in Core. Real-time loops and recording or air decoding go in Video. Store logic, including its ffmpeg and ffprobe work such as export, waveforms and probing, goes in Media.
2. Write the rule as a pure function with a test.
3. Wire it into the window last.
4. Add activity log lines with an `@event` tag.
5. If it's settings-backed, add a property to `AppSettings` with a default. Old files load, since a missing key takes the default.
6. Run `graphify update .` and confirm the report's Import Cycles section still says none.
7. Update the relevant document here.

## Modify existing functionality

Before changing a class, find its callers and read the matching section in [the assessment](11_TECHNICAL_ASSESSMENT.md).

| Change | Look at |
|---|---|
| Recording format | `RecordingProfile`, plus tests in `tests/Emerald.Video.Tests` |
| How a frame is chosen for air | `DelayPacer`, `LiveCues` |
| Timeline positions | `IngestTimeline`, `MediaLibrary.Accumulate` |
| Live Edit's edit rules | `IngestPlacement`, `EdlDiff`, `LiveAir.Submit` |
| Ingest rules | `IngestControllerService.Build`, `IngestSchedulerService.Due` |

Changing `LiveAirTransmitter`? Check whether `DelayTransmitter` needs the same change. They have similar code that has already diverged.

## Add a new API endpoint

The server is read-only by design, and any route added should stay that way unless authentication is added first.

1. Add a builder to `src/Emerald.Core/MonitorFeed.cs` returning a JSON string, serialised with the class's `Json` options as the others do. It must only read.
2. Add the route in `MonitorServer.Respond` (`src/Emerald.Core/MonitorServer.cs`). Paths are matched exactly and are case-sensitive, and query values get only `+` to space conversion.
3. Add tests to `tests/Emerald.Core.Tests/MonitorServerTests.cs`.
4. If the page should show it, edit `src/Emerald.Core/MonitorPage.cs`, which is one string constant. Escape values with `esc`, noting it doesn't cover attribute context.
5. Document it in [API and communication](07_API_AND_COMMUNICATION.md).

## Add a new frontend component

The UI is WPF XAML with code-behind.

**A control or panel.** Use styles from `src/Emerald.App/App.xaml`, such as `PrimaryButton`, `GhostButton` and the colours `Ok`, `Warn`, `Bad`, `Muted`. A style applies to its `TargetType` and types derived from it. On an unrelated type it throws at window load, not at build, so run:

```bash
bash tools/check-xaml-styles.sh
```

Run it from the repository root, in Git Bash or another shell with gawk. It is stricter than WPF, comparing type names exactly, so a legal use on a derived type is reported too. It checks `src/*/*.xaml` only, so the Ingest windows under `Views\` are not covered and need checking by hand. A pass printed from another directory means nothing.

**A timecode field.** Declare `xmlns:core="clr-namespace:Emerald.Core;assembly=Emerald.Core"`, then use `core:TimecodeMask.Enabled="True"`. Use a plain box with `FramePick.TryParse` if partial input should work. That takes the rate, day frames and current position.

**A new window or module:**

1. Put it in the appropriate project, set up as rule 1 describes.
2. Reference it from `Emerald.App`.
3. Add a field and button in `ShellWindow`, opening it through `ShowModule(ref _field, () => new MyWindow(...))` (`src/Emerald.App/ShellWindow.xaml.cs:2550`).
4. Add your field to the `Closed` handler inside `ShowModule` (`ShellWindow.xaml.cs:2563`), or the window can never be reopened.
5. Close it in `ShellWindow_Closing`.
6. For a command-line mode, add it to the mode whitelist and usage message in `App_Startup`, the switch in `App.OpenModule`, and `ShellWindow.OpenModule`. Add a run script and an installer shortcut.
7. Pass `App.Settings` rather than loading your own. `LiveEditWindow` loading its own copy is a [known fault](11_TECHNICAL_ASSESSMENT.md#b-other-verified-issues-high).
8. Unsubscribe from every shared object on close.

## Modify media processing

**Encoder arguments.** All in `RecordingProfile.EncoderArguments`. Tests in `tests/Emerald.Video.Tests` assert the argument list, so change them together. To test by hand, reproduce the [command](05_MEDIA_PROCESSING.md#ffmpeg-commands) with a file input.

**Adding an output.** Add a `RecordingOutput` to `RecordingProfile.Outputs`. `MediaLibrary`, `StorageWarden` and `IngestRecorder` assume low and high, and `IngestRecorder` maps `[0]` to proxy and `[1]` to master, so check each.

**Decoding for air.** `FrameSource` and `AudioSource`. `ReadFrame`'s position is in **samples**.

**Watch out for:**

- The segment muxer does not create folders.
- `%d` means day of month under `-strftime`.
- DNxHD 145 accepts only certain rasters. `RecordingOutput.Supports` enforces this.
- Media Foundation can't decode DNxHD, so anything played on screen needs a proxy.

**Ring format.** Changing the slot layout needs a change to `DelayLine` only, and its magic number should change too.

## Troubleshoot hardware

See [troubleshooting](10_TROUBLESHOOTING.md#hardware-checks), and add:

1. Run with no card: ingest SIMULATE works, and the rest opens with scan errors.
2. Add log lines to `SdiCapture` rather than breakpoints. A stopped thread looks like a card fault.
3. Error 18 means the channel is in use by something outside Emerald.
4. The VideoMaster headers aren't in the repository. Get them from DELTACAST to check values in `VideoMasterHD.cs`.
5. dCARE is the independent check. Close Emerald first, since a receiver allows one handle.
6. `SdiOutput.SlotCounters` reads the card's own counters but has no callers. Calling it is the quickest way to see true underruns.

## Debug common errors

| Situation | Approach |
|---|---|
| Handler doesn't run | Check for a `_loading` style guard. See [A1](11_TECHNICAL_ASSESSMENT.md#a1-_loading-is-never-cleared-so-five-shell-handlers-never-run). |
| Exception on a background thread | Nothing logs it. Run under a debugger with first-chance exceptions on. |
| Window throws on load, build was clean | Run `tools/check-xaml-styles.sh`. |
| Off-by-a-few frames on the timeline | Check the coordinate. Then check inserts in `IngestTimeline`. |
| A/V drift in a recording | Read the closing line: "in step" or "OUT OF STEP by N". |
| Tests fail on disk space | Set `TEMP` to a drive with room. |
| Tests fail, ffmpeg not found | Put it on `PATH`. |
| UI freezes | Look for a synchronous `Stop`, `Join` or ffprobe on the UI thread. |
| Log line missing | `ActivityLog` isn't flushed at exit. Also check `Transient`. |

**Attaching a debugger:** Debug build with `run.bat Debug`, then attach, or set `Emerald.App` as the startup project with arguments `--edl`. Remember a running instance captures later launches.

## Testing

```powershell
dotnet test Emerald.sln -c Release
dotnet test tests\Emerald.Core.Tests --filter "FullyQualifiedName~RecordingSequence"
```

- xUnit. Names read as sentences.
- Fakes exist for `IDelayLine`, `IEdlPlayback`, `IIngestClock`, `IIngestStore`, `IIngestMediaRegistrar` and `IIngestRecorder`. Ingest tests use the production `MockIngestHardware`. `ISdiOutput` exists as a seam, but it has no fake and the transmitters don't accept one, so transmit loops can't be tested without a card.
- No test uses a window class: they need an STA thread and the styles in `App.xaml`. Move logic down before testing it.
- Decode tests pass silently when ffmpeg is missing, so a green run with no ffmpeg proves less.
- To be sure a test bites, break the code on purpose and watch it fail, then restore.

## Build and deploy

```powershell
.\build.bat                    # development
.\publish.bat                  # dist\Emerald, self-contained
.\publish.bat -edl             # dist\Emerald-EDL
.\publish.bat -noffmpeg        # either, without bundling ffmpeg
```

Then copy the folder across and run `install.bat` as administrator. See [installation and deployment](09_INSTALLATION_AND_DEPLOYMENT.md).

Before a release:

1. Raise the version in `Directory.Build.props`, and `DisplayVersion` in `installer/install.bat` and `installer/edl/install.bat`.
2. Run all tests, with ffmpeg available and room in the temp folder.
3. Run `tools/check-xaml-styles.sh`, and check the Ingest windows by hand.
4. Test on a card: record past a segment boundary, arm tidal lock, go to air, stop, and check the drain.

## Documentation

- This folder describes commit `dd8e4a7`. Line numbers drift, so search by name when one misses.
- `PROTOCOL.md` documents the EDL record format.
- `readme.md` holds longer design notes, with [known stale parts](11_TECHNICAL_ASSESSMENT.md#stale-documentation).
