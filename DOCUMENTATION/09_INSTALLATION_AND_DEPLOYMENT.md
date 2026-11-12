# 9. Installation and deployment

Commands are from the project's own scripts.

## Requirements

| Software | Needed for | Source |
|---|---|---|
| Windows 10/11 x64 | Everything. `VideoMasterHD.dll` is 64-bit. | |
| .NET 8 SDK | Building. The scripts name 8.0. | <https://dotnet.microsoft.com/download/dotnet/8.0> |
| .NET 8 Desktop Runtime | Running a build that isn't self-contained. `publish.bat` output needs nothing. | Same page |
| DELTACAST VideoMaster driver | Any SDI work. Must come from DELTACAST's own installer, because it is a kernel driver. | DELTACAST |
| ffmpeg and ffprobe | Recording, playout, export, clip lengths. Can be bundled. | <https://ffmpeg.org> |
| git | Cloning | |

**Hardware.** A DELTACAST card with RX and TX ports for the work planned. No minimum CPU, RAM or disk figures are stated in the project. What the code implies:

| Item | Figure | Source |
|---|---|---|
| Ring disk | About 44 GB for the default 7-minute ring at 1080p25, plus 2 GB free-space margin. About 105 MB/s sustained. | Calculated from `src/Emerald.Video/DelayLine.cs` |
| Master recording | DNxHD at 145 Mb/s, about 65 GB an hour before audio | `src/Emerald.Video/RecordingProfile.cs` |
| Proxy recording | Depends on the bitrate setting | |

**Environment variables.**

| Name | Effect |
|---|---|
| `EMERALD_SOFTWARE_RENDER=1` | WPF software rendering |
| `EMERALD_NOPAUSE` | Set by the run scripts so a build does not pause |

**Ports.**

| Port | Direction | Purpose |
|---|---|---|
| TCP 8787 | Inbound | Optional monitoring page. `install.bat` opens it in the firewall, for the full product. |
| TCP 8888 | Outbound | Default port in the timecode URL default. The site's real service: Not verified. |

**External services.** Only the timecode service, which is not in this repository.

## Development

```powershell
git clone https://github.com/MdelarosaArcX/Emerald-EDL-Ingest.git Emerald
cd Emerald
.\build.bat
```

| Command | Effect |
|---|---|
| `run.bat` | Build Release, open the capture deck |
| `run-edl.bat` | Build, open the EDL Generator |
| `run-ingest.bat` | Build, open the Ingest Controller |
| `run-monitor.bat` | Build, open the Monitor |
| `run.bat Debug` | Debug build, then run |
| `run.bat Release -n` | Skip the build |
| `build.bat` | Release build only |
| `build.bat Debug` | Debug build only |
| `build.bat Release -r` | Restore packages first |

By hand:

```powershell
dotnet build Emerald.sln -c Release
dotnet test  Emerald.sln -c Release
.\src\Emerald.App\bin\x64\Release\net8.0-windows\win-x64\Emerald.App.exe
.\src\Emerald.App\bin\x64\Release\net8.0-windows\win-x64\Emerald.App.exe --ingest
```

The output path has `bin\x64\` because building the solution sets `Platform=x64`. Building `Emerald.App.csproj` alone lands in `bin\Release\` instead.

If Emerald is running, the run scripts skip the build, because the running process locks its own executable, and hand the request to that instance. Close Emerald to rebuild.

`CLAUDE.md` lists `npm run build`, `npm test` and `npx graphify .`. There is no `package.json` in this project, so those commands do not apply. Use the commands above. The code graph updates with `graphify update .`.

### Running tests

```powershell
dotnet test Emerald.sln -c Release
```

Some Video and Ingest tests need:

- `ffmpeg` on `PATH`.
- Free disk space in the temp folder for delay rings, around 2 GB past the ring's size. On a machine whose `C:` is nearly full, point `TEMP` and `TMP` at another drive, or those tests fail on the free-space guard.

## Production build

```powershell
publish.bat              # the whole plant, into dist\Emerald
publish.bat -edl         # EDL Generator alone, into dist\Emerald-EDL
publish-edl.bat          # the same
publish.bat -noffmpeg    # either, without bundling ffmpeg
```

Under the hood:

```
dotnet publish src\Emerald.App\Emerald.App.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o <dist> --nologo
```

- **Self-contained.** The .NET runtime is inside the folder, about 170 MB per the script. No runtime, SDK or internet is needed on the target.
- **ffmpeg** is bundled from `C:\ffmpeg\bin`, `C:\Program Files\ffmpeg\bin` or `PATH`, if found. Otherwise a warning is shown.
- **`-edl` is the same binaries.** It changes the product: its own install folder, one module's shortcuts (Emerald EDL in the Start Menu and on the Desktop), its own Add/Remove Programs entry, and no firewall rule. A real split would need a second copy of the window styles.

Check first that Emerald is not running from the source tree, or the publish refuses.

The version is set in `Directory.Build.props`. `installer\install.bat` and `installer\edl\install.bat` each write `DisplayVersion` as a literal `1.0.0`, so all three have to be changed together. `update.bat` does not write the registry, so the listed version only changes on `install.bat`.

## Installing

1. Copy the published folder to the machine.
2. Right-click `install.bat`, Run as administrator.
3. It lists the fixed drives with their free space, then asks for three folders. Accept each suggestion or type another:
   - **High-res folder**, for the DNxHD masters. Suggested: `<roomiest non-system drive>\Emerald\media\high`.
   - **Low-res folder**, for the proxies Live Edit plays. Suggested: `<same drive>\Emerald\media\low`. A different drive from the high-res one is better on a spinning disk.
   - **Database folder**, for `emerald.db`, `ingest.db` and the waveform cache. Suggested: `%APPDATA%\Emerald`. Ideally not the drive the masters go to.
4. Start Menu, Emerald.

```
install.bat                                  install, asking where things go
install.bat D:\Emerald\media                 masters in its high\, proxies in its low\
install.bat D:\Masters E:\Proxies D:\Data    high-res, low-res and database folders
... -q                                       the same, no questions and no pauses, for a rollout
```

It asks only when started from Explorer (Run as administrator) and without `-q`. Run from a command prompt, or with `-q`, it takes the suggestions for anything not given. With one folder given, the database folder is not given, so it is asked for or defaults to `%APPDATA%\Emerald`.

**An update asks only for what is not set.** The folders already in `settings.json` are read first (`highResFolder`, `lowResFolder`, `dataFolder`, and a `captureFolder` set the old way stands for its `high` and `low`). On a machine installed before this change, that means it asks only for the database folder.

`%APPDATA%` here is the elevated account's. If the administrator who runs the installer is not the operator, the folders land in the administrator's profile, and the operator's Emerald falls back to `media` beside the executable, inside Program Files, and to `%APPDATA%\Emerald` for the database. In that case set the folders on the deck as the operator.

What it does:

| Step | Detail |
|---|---|
| Checks | Elevated, `Emerald.App.exe` beside it, Emerald not running |
| Folders | Reads what is already set through `settings.ps1 -Read`, lists the drives (`settings.ps1 -Drives`), then asks for the high-res, low-res and database folders not yet set. Creates all three. |
| Copy | `robocopy` to `%ProgramFiles%\Emerald` with `/e /purge`. `settings.ps1` is not copied. |
| Settings | Merged by `settings.ps1`: it adds `highResFolder`, `lowResFolder` and `dataFolder` only where they are missing or empty, never overwrites a folder already chosen, touches no other setting, and writes through a temporary file. A `settings.json` it cannot read is left alone, with a warning. If `settings.ps1` is not beside `install.bat`, nothing is saved and it says so. |
| Shortcuts | Start Menu: Emerald, Emerald EDL, Emerald Ingest, Emerald Monitor. Plus a Desktop shortcut. |
| Registry | Add/Remove Programs entry under `HKLM` |
| Firewall | Rule `Emerald monitoring 8787`, inbound TCP. This is added even if remote monitoring is never switched on. |
| Report | Whether the driver, ffmpeg, ffprobe and the high-res, low-res and database folders are ready |

`READ-ME-FIRST.txt` travels with the folder. The EDL product has its own set in `installer\edl\`. `publish.bat` copies `installer\settings.ps1` into both published folders.

**EDL installer.** Asks for the media folder as before, then for the database folder, with the same drive list and the same rule: asked only if `dataFolder` is not set. It merges `captureFolder` and `dataFolder` through `settings.ps1`. Known issue, still open there: its fragment that reads the existing `captureFolder` has the old quoting fault (`installer/edl/install.bat:96`), so on an update the summary shows the media folder as `'`. Because the settings are now merged rather than left alone, a settings file with no `captureFolder` would get `'` written into it. Check `captureFolder` after an EDL update.

## Configuring a site

Three things are machine-specific, all set in the UI and stored in `settings.json`:

1. **Timecode service.** Set it in the deck, EDL or Ingest window and press Apply. One setting is shared by all. Until it's reachable the clock reads `--:--:--:--`, and an ingest cannot be scheduled.
2. **Board and port.** Rescan lists what is present.
3. **ffmpeg.** Found automatically, or set `ffmpegPath` in `settings.json`.

Also consider the ring folder. `tidalLockRingFolder` defaults to `%LOCALAPPDATA%`, usually the system drive, where tens of gigabytes are written constantly while recording with `keepDelayReady` on (roughly 80 to 100 MB/s while a delay is held). There is no UI field for it found in this review, so it means editing `settings.json`. A folder on the same drive as either recording folder is refused and the default used instead (`delay.samedisk`).

And the recording drives. Outside Emerald:

- Exclude the high-res, low-res and database folders from Windows Search indexing and from Defender real-time scanning. Both read files as they are written.
- Put the low-res folder on a different drive from the high-res one where you can.

Edit `settings.json` only with Emerald closed. The running process holds its own copy, does not re-read these values, and overwrites the file when it next saves.

## Updating

```
update.bat                                   update from this folder
update.bat \\master\emerald\dist\Emerald     update from a share
update.bat \\master\emerald\dist\Emerald -q  the same, no questions
```

Run as administrator, with Emerald closed. It compares the executable's write time, so pointing it at the current build changes nothing. The program folder is replaced wholesale. Recordings and `%APPDATA%\Emerald` are not touched.

`/purge` removes anything else placed in the program folder by hand. That includes `ffmpeg.exe` and `ffprobe.exe` copied there for a `-noffmpeg` build, so in that case put them on `PATH` or in `C:\ffmpeg\bin`, or bundle them.

`update.bat -q` with no folder treats `-q` as the source path, so always give the folder with `-q`.

Stop any recording before closing Emerald. A segment being written has no index until it closes.

## Uninstalling

```
uninstall.bat      remove the program, show the folders, ask about settings, then about the database
uninstall.bat -q   remove the program, keep everything else, no questions
```

Recordings are never touched. It shows the high-res, low-res, settings and database folders, then asks two questions:

1. **Delete the settings?** `y` removes the whole of `%APPDATA%\Emerald`, which includes bookmarks, and, when the database folder is the default, the database too, before the second question is asked.
2. **Delete the database?** Asked only if `emerald.db` is still in the database folder. `y` deletes `emerald.db`, `emerald.db-wal`, `emerald.db-shm`, `ingest.db` and the `peaks` folder there.

## Where things go

| Item | Location |
|---|---|
| Program | `C:\Program Files\Emerald` |
| High-res and low-res recordings | Chosen at install (`highResFolder`, `lowResFolder`); changeable on the capture deck |
| Settings, bookmarks | `%APPDATA%\Emerald` |
| Databases and waveform cache | The database folder chosen at install (`dataFolder`), `%APPDATA%\Emerald` by default: `emerald.db` (the log, kept `logRetentionDays`, 14), `ingest.db`, `peaks\` |
| Old log files | `%APPDATA%\Emerald\logs` (`logFolder`). Nothing new is written there; `.jsonl` files from earlier builds are removed at startup once older than `logRetentionDays`. |
| Delay rings | `%LOCALAPPDATA%\Emerald\delay`. Never on a recording drive: a ring folder there is passed over for this default. |

**EDL product.** Program in `C:\Program Files\Emerald EDL`, updated from `dist\Emerald-EDL` by its own `update.bat`. It removes only itself. If a full Emerald is also installed, its `uninstall.bat` does not offer to delete settings. Both products share `%APPDATA%\Emerald`, so a board or port changed in one changes in the other.

**With no card or driver,** Emerald still opens. `READ-ME-FIRST.txt` says the Monitor, Live Edit and the clip store work, and SIMULATE covers Ingest.

## Continuous integration

No CI configuration was found in the repository. The scripts exit non-zero on failure, so they would drop into one.
