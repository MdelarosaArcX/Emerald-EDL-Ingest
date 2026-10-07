@echo off
rem ---------------------------------------------------------------------------
rem  Installs the Emerald EDL Generator on this machine, and nothing else.
rem
rem    install.bat                       - install, asking where its media lives
rem    install.bat D:\Emerald\media      - install, media there, no questions
rem    install.bat D:\Emerald\media -q   - the same, and no pauses (for a rollout)
rem
rem  Run it as administrator: it writes to Program Files, the Start Menu and the
rem  registry. It will say so and stop if it is not elevated.
rem
rem  This is a playout machine's install. It puts one deck on the box - the EDL -
rem  with one shortcut, its own entry in Add/Remove Programs, and no monitoring
rem  port opened, because an EDL machine serves no monitoring page.
rem
rem  Nothing needs to be installed first. The .NET runtime is inside this folder,
rem  and so is ffmpeg if it was bundled. What it cannot bring with it is the
rem  DELTACAST driver - that is a kernel driver and only DELTACAST's own installer
rem  can put it on. This checks for it and says so rather than letting you find out
rem  when a list will not play.
rem ---------------------------------------------------------------------------
setlocal EnableDelayedExpansion

set "HERE=%~dp0"
set "TARGET=%ProgramFiles%\Emerald EDL"
set "MENU=%ProgramData%\Microsoft\Windows\Start Menu\Programs\Emerald EDL"
set "EXE=%TARGET%\Emerald.App.exe"
set "SETTINGS=%APPDATA%\Emerald\settings.json"
set "UNINSTALL_KEY=HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\EmeraldEDL"

rem The full Emerald, if it is also on this machine. Not a conflict - they install
rem to different folders and list separately - but they do read one settings file,
rem and an operator should be told that rather than discovering it when a board
rem index changes under them.
set "FULL=%ProgramFiles%\Emerald\Emerald.App.exe"

set "MEDIA=%~1"
set "QUIET=0"
if /i "%~2"=="-q" set "QUIET=1"
if /i "%~1"=="-q" set "QUIET=1" & set "MEDIA="

rem See build.bat for why find.exe is fully qualified.
set "INTERACTIVE=0"
echo %cmdcmdline% | "%SystemRoot%\System32\find.exe" /i "/c" >nul && set "INTERACTIVE=1"
if "%QUIET%"=="1" set "INTERACTIVE=0"

rem An existing install is an update, not a fresh one: the program is replaced, the
rem settings and the media are left exactly as they are.
set "MODE=install"
if exist "%EXE%" set "MODE=update"

echo ===========================================================================
if "%MODE%"=="update" (echo  Emerald EDL - update) else (echo  Emerald EDL - install)
echo ===========================================================================
echo.

rem ------------------------------------------------------------------ elevation
rem
rem "net session" fails for a standard user and succeeds for an administrator,
rem which is the cheapest reliable check that does not need a helper.
net session >nul 2>&1
if errorlevel 1 (
    echo [ERROR] This needs to run as administrator.
    echo.
    echo         Right-click install.bat and choose "Run as administrator".
    goto :fail
)

if not exist "%HERE%Emerald.App.exe" (
    echo [ERROR] Emerald.App.exe is not in this folder.
    echo         Run install.bat from inside the published Emerald-EDL folder.
    goto :fail
)

rem ------------------------------------------------------------ already running
rem
rem Checked by name, because the EDL and the full Emerald are the same executable
rem under different folders: either one running holds a copy of these files open.
tasklist /fi "imagename eq Emerald.App.exe" /nh 2>nul | "%SystemRoot%\System32\find.exe" /i "Emerald.App.exe" >nul
if not errorlevel 1 (
    echo [ERROR] Emerald is running. Close it first - its files cannot be replaced
    echo         while it holds them open. That includes a full Emerald, which is
    echo         the same executable.
    goto :fail
)

rem ------------------------------------------------------------- where media is
rem
rem The EDL plays files off a disk, so it needs to be pointed at where they are.
rem Left alone it would look beside its own executable, which after an install is
rem inside Program Files - the wrong place, and not writable by a standard user.
rem So it is settled here and written into the settings.

rem Already configured: an update must not re-ask, and must not move anything.
if exist "%SETTINGS%" (
    for /f "delims=" %%F in ('powershell -NoProfile -Command "try { (Get-Content -Raw ''%SETTINGS%'' ^| ConvertFrom-Json).captureFolder } catch { '''' }"') do set "MEDIA=%%F"
)

if defined MEDIA goto :havemedia

rem Offer the roomiest fixed drive that is not the system one, since that is almost
rem always where a plant keeps its media.
set "BEST="
for /f "skip=1 tokens=1,2" %%D in ('powershell -NoProfile -Command "Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | Sort-Object -Property FreeSpace -Descending | Select-Object DeviceID,FreeSpace | Format-Table -HideTableHeaders"') do (
    if not defined BEST if not "%%D"=="" (
        if /i not "%%D"=="%SystemDrive%" set "BEST=%%D"
    )
)
if not defined BEST set "BEST=%SystemDrive%"

set "MEDIA=%BEST%\Emerald\media"

echo The EDL plays media off a disk. Point it at the folder that holds it - a
echo local drive or a share, whichever this plant uses.
echo.
set "ANSWER="
set /p "ANSWER=Where does the media live? [%MEDIA%] "
if not "!ANSWER!"=="" set "MEDIA=!ANSWER!"

:havemedia

echo.
echo   Program        %TARGET%
echo   Media          %MEDIA%
echo   Settings       %SETTINGS%
echo.

rem ------------------------------------------------------------------- the files
echo Copying files ...

if not exist "%TARGET%" mkdir "%TARGET%" 2>nul

rem /e all subdirectories, /purge remove what is no longer shipped, /njh /njs /ndl
rem /nc /ns /nfl quiet. Robocopy returns 0-7 for success; 8 and above is a real
rem failure.
robocopy "%HERE%." "%TARGET%" /e /purge /xf install.bat uninstall.bat update.bat READ-ME-FIRST.txt /njh /njs /ndl /nc /ns /nfl >nul
if errorlevel 8 (
    echo [ERROR] Copying to %TARGET% failed.
    goto :fail
)

if not exist "%MEDIA%" mkdir "%MEDIA%" 2>nul
if not exist "%MEDIA%" (
    echo [WARN] Could not create or reach %MEDIA%. Set the media folder in the EDL
    echo        after installing. A share that is not mounted yet is the usual
    echo        reason, and is not a problem.
)

rem ---------------------------------------------------------------- the settings
rem
rem Written only if there is no settings file yet, so installing the EDL onto a
rem machine that already runs a full Emerald never overwrites what the operator
rem has set up there. Just the folder: everything else is better left to Emerald's
rem own defaults, and the board and port are chosen in the EDL itself.

if exist "%SETTINGS%" (
    echo Settings already exist - left alone.
) else (
    if not exist "%APPDATA%\Emerald" mkdir "%APPDATA%\Emerald" 2>nul
    powershell -NoProfile -Command "$p='%MEDIA%'.Replace('\','\\'); Set-Content -LiteralPath '%SETTINGS%' -Encoding utf8 -Value ('{' + [char]10 + '  \"captureFolder\": \"' + $p + '\"' + [char]10 + '}')"
    if errorlevel 1 (
        echo [WARN] Could not write the settings file. Set the media folder in the
        echo        EDL after installing.
    ) else (
        echo Settings written, media set to %MEDIA%.
    )
)

rem --------------------------------------------------------------- the shortcuts
rem
rem One deck, so one shortcut. --edl is what makes this an EDL machine: the same
rem executable opens the capture deck without it.
echo Making shortcuts ...

if not exist "%MENU%" mkdir "%MENU%" 2>nul

call :shortcut "%MENU%\Emerald EDL.lnk"           "--edl" "Emerald - EDL Generator"
call :shortcut "%PUBLIC%\Desktop\Emerald EDL.lnk" "--edl" "Emerald - EDL Generator"

rem --------------------------------------------------- Add/Remove Programs entry
rem
rem Its own key, so installing or removing the EDL never touches a full Emerald's
rem entry on the same machine.
for /f %%S in ('powershell -NoProfile -Command "[math]::Round((Get-ChildItem -Recurse -File '%TARGET%' ^| Measure-Object Length -Sum).Sum / 1KB)"') do set "SIZEKB=%%S"

reg add "%UNINSTALL_KEY%" /f /v DisplayName     /t REG_SZ /d "Emerald EDL Generator" >nul
reg add "%UNINSTALL_KEY%" /f /v DisplayVersion  /t REG_SZ /d "1.0.0" >nul
reg add "%UNINSTALL_KEY%" /f /v Publisher       /t REG_SZ /d "Peregrine Broadcast" >nul
reg add "%UNINSTALL_KEY%" /f /v InstallLocation /t REG_SZ /d "%TARGET%" >nul
reg add "%UNINSTALL_KEY%" /f /v DisplayIcon     /t REG_SZ /d "%EXE%" >nul
reg add "%UNINSTALL_KEY%" /f /v UninstallString /t REG_SZ /d "\"%TARGET%\uninstall.bat\"" >nul
reg add "%UNINSTALL_KEY%" /f /v NoModify        /t REG_DWORD /d 1 >nul
reg add "%UNINSTALL_KEY%" /f /v NoRepair        /t REG_DWORD /d 1 >nul
if defined SIZEKB reg add "%UNINSTALL_KEY%" /f /v EstimatedSize /t REG_DWORD /d %SIZEKB% >nul

copy /y "%HERE%uninstall.bat" "%TARGET%\uninstall.bat" >nul

rem No firewall rule. The monitoring page is a capture-side thing; an EDL machine
rem serves nothing, so nothing is opened. If this box should also serve monitoring,
rem install the full Emerald on it instead.

rem ------------------------------------------------------------- what is missing
echo.
echo ---------------------------------------------------------------------------
echo  Installed.
echo ---------------------------------------------------------------------------
echo.

set "READY=1"

if exist "%SystemRoot%\System32\VideoMasterHD.dll" (
    echo   [ok]   DELTACAST driver found.
) else (
    set "READY=0"
    echo   [--]   DELTACAST driver NOT found ^(no VideoMasterHD.dll in System32^).
    echo          The EDL will open and lists can be built and saved, but nothing
    echo          can be played to air until the driver is installed from
    echo          DELTACAST's own installer. Emerald cannot carry it.
)

if exist "%TARGET%\ffmpeg.exe" (
    echo   [ok]   ffmpeg is in the install folder.
) else (
    where ffmpeg.exe >nul 2>&1
    if errorlevel 1 (
        set "READY=0"
        echo   [--]   ffmpeg NOT found. The EDL decodes its media with it, so
        echo          nothing will play out.
        echo          Put ffmpeg.exe and ffprobe.exe in "%TARGET%"
    ) else (
        echo   [ok]   ffmpeg found on PATH.
    )
)

if exist "%TARGET%\ffprobe.exe" (
    echo   [ok]   ffprobe is in the install folder.
) else (
    where ffprobe.exe >nul 2>&1
    if errorlevel 1 (
        echo   [--]   ffprobe NOT found. Media lengths and timecodes will read as
        echo          unknown, which an EDL needs to cue accurately.
    ) else (
        echo   [ok]   ffprobe found on PATH.
    )
)

if exist "%MEDIA%" (
    echo   [ok]   Media folder ready at %MEDIA%
) else (
    echo   [--]   Media folder %MEDIA% cannot be reached yet.
)

if exist "%FULL%" (
    echo.
    echo   [note] A full Emerald is also installed on this machine, at
    echo          "%ProgramFiles%\Emerald". They are separate programs and either
    echo          can be removed without touching the other - but they share one
    echo          settings file, so a board or port changed in one is changed for
    echo          both. That is usually what you want on a single plant machine.
)

echo.
if "%READY%"=="1" (
    echo  Ready to run. Start Menu ^> Emerald EDL, or the Desktop shortcut.
) else (
    echo  Installed but not yet ready to play out - see the [--] lines above.
)
echo.
if "%INTERACTIVE%"=="1" pause
exit /b 0

rem ---------------------------------------------------------------------------
rem  One shortcut. WScript.Shell through PowerShell, because batch cannot make a
rem  .lnk on its own and every Windows that can run Emerald has PowerShell.
rem ---------------------------------------------------------------------------
:shortcut
set "LNK=%~1"
set "ARGS=%~2"
set "DESC=%~3"

powershell -NoProfile -Command ^
    "$s=(New-Object -ComObject WScript.Shell).CreateShortcut('%LNK%');" ^
    "$s.TargetPath='%EXE%';" ^
    "$s.Arguments='%ARGS%';" ^
    "$s.WorkingDirectory='%TARGET%';" ^
    "$s.Description='%DESC%';" ^
    "$s.IconLocation='%EXE%';" ^
    "$s.Save()" >nul 2>&1

if errorlevel 1 echo [WARN] Could not create %LNK%
exit /b 0

:fail
echo.
echo [ERROR] Install did not complete.
if "%INTERACTIVE%"=="1" pause
exit /b 1
