@echo off
rem ---------------------------------------------------------------------------
rem  Installs Emerald on this machine.
rem
rem    install.bat                       - install, asking where recordings go
rem    install.bat D:\Emerald\media      - install, recordings there, no questions
rem    install.bat D:\Emerald\media -q   - the same, and no pauses (for a rollout)
rem
rem  Run it as administrator: it writes to Program Files, the Start Menu and the
rem  firewall. It will say so and stop if it is not elevated.
rem
rem  Nothing needs to be installed first. The .NET runtime is inside this folder,
rem  and so is ffmpeg if it was bundled. What Emerald cannot bring with it is the
rem  DELTACAST driver - that is a kernel driver and only DELTACAST's own installer
rem  can put it on. This checks for it and says so rather than letting you find out
rem  when a recording will not start.
rem ---------------------------------------------------------------------------
setlocal EnableDelayedExpansion

set "HERE=%~dp0"
set "TARGET=%ProgramFiles%\Emerald"
set "MENU=%ProgramData%\Microsoft\Windows\Start Menu\Programs\Emerald"
set "EXE=%TARGET%\Emerald.App.exe"
set "SETTINGS=%APPDATA%\Emerald\settings.json"
set "UNINSTALL_KEY=HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\Emerald"

set "MEDIA=%~1"
set "QUIET=0"
if /i "%~2"=="-q" set "QUIET=1"
if /i "%~1"=="-q" set "QUIET=1" & set "MEDIA="

rem See build.bat for why find.exe is fully qualified.
set "INTERACTIVE=0"
echo %cmdcmdline% | "%SystemRoot%\System32\find.exe" /i "/c" >nul && set "INTERACTIVE=1"
if "%QUIET%"=="1" set "INTERACTIVE=0"

rem An existing install is an update, not a fresh one: the program is replaced, the
rem settings and the recordings are left exactly as they are.
set "MODE=install"
if exist "%EXE%" set "MODE=update"

echo ===========================================================================
if "%MODE%"=="update" (echo  Emerald - update) else (echo  Emerald - install)
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
    echo         Run install.bat from inside the published Emerald folder.
    goto :fail
)

rem ------------------------------------------------------------ already running
tasklist /fi "imagename eq Emerald.App.exe" /nh 2>nul | "%SystemRoot%\System32\find.exe" /i "Emerald.App.exe" >nul
if not errorlevel 1 (
    echo [ERROR] Emerald is running. Close it first - its files cannot be replaced
    echo         while it holds them open.
    goto :fail
)

rem -------------------------------------------------------------- where media goes
rem
rem This matters more than it looks. Left alone, Emerald puts its store beside its
rem own executable, which after an install is inside Program Files - not writable
rem by a standard user, and the wrong place for terabytes of video besides. So the
rem store is settled here, at install time, and written into the settings.

rem Already configured: an update must not re-ask where recordings go, and must not
rem move a store that has terabytes in it.
if exist "%SETTINGS%" (
    for /f "delims=" %%F in ('powershell -NoProfile -Command "try { (Get-Content -Raw ''%SETTINGS%'' ^| ConvertFrom-Json).captureFolder } catch { '''' }"') do set "MEDIA=%%F"
)

if defined MEDIA goto :havemedia

rem Offer the roomiest fixed drive that is not the system one, since that is almost
rem always where a plant wants its recordings.
set "BEST="
set "BESTFREE=0"
for /f "skip=1 tokens=1,2" %%D in ('powershell -NoProfile -Command "Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | Sort-Object -Property FreeSpace -Descending | Select-Object DeviceID,FreeSpace | Format-Table -HideTableHeaders"') do (
    if not defined BEST if not "%%D"=="" (
        if /i not "%%D"=="%SystemDrive%" set "BEST=%%D"
    )
)
if not defined BEST set "BEST=%SystemDrive%"

set "MEDIA=%BEST%\Emerald\media"

echo Recordings need a folder with room for them. Emerald will not put them in
echo Program Files.
echo.
set "ANSWER="
set /p "ANSWER=Where should recordings go? [%MEDIA%] "
if not "!ANSWER!"=="" set "MEDIA=!ANSWER!"

:havemedia

echo.
echo   Program        %TARGET%
echo   Recordings     %MEDIA%
echo   Settings       %SETTINGS%
echo.

rem ------------------------------------------------------------------- the files
echo Copying files ...

if not exist "%TARGET%" mkdir "%TARGET%" 2>nul

rem /e all subdirectories, /purge remove what is no longer shipped, /njh /njs /ndl /nc /ns
rem quiet. Robocopy returns 0-7 for success; 8 and above is a real failure.
robocopy "%HERE%." "%TARGET%" /e /purge /xf install.bat uninstall.bat update.bat READ-ME-FIRST.txt /njh /njs /ndl /nc /ns /nfl >nul
if errorlevel 8 (
    echo [ERROR] Copying to %TARGET% failed.
    goto :fail
)

if not exist "%MEDIA%" mkdir "%MEDIA%" 2>nul
if not exist "%MEDIA%" (
    echo [WARN] Could not create %MEDIA%. Set the store folder on the capture deck
    echo        after installing.
)

rem ---------------------------------------------------------------- the settings
rem
rem Written only if there is no settings file yet, so reinstalling over a working
rem machine never overwrites what the operator has set up. Just the store folder:
rem everything else is better left to Emerald's own defaults.

if exist "%SETTINGS%" (
    echo Settings already exist - left alone.
) else (
    if not exist "%APPDATA%\Emerald" mkdir "%APPDATA%\Emerald" 2>nul
    powershell -NoProfile -Command "$p='%MEDIA%'.Replace('\','\\'); Set-Content -LiteralPath '%SETTINGS%' -Encoding utf8 -Value ('{' + [char]10 + '  \"captureFolder\": \"' + $p + '\"' + [char]10 + '}')"
    if errorlevel 1 (
        echo [WARN] Could not write the settings file. Set the store folder on the
        echo        capture deck after installing.
    ) else (
        echo Settings written, store set to %MEDIA%.
    )
)

rem --------------------------------------------------------------- the shortcuts
echo Making shortcuts ...

if not exist "%MENU%" mkdir "%MENU%" 2>nul

call :shortcut "%MENU%\Emerald.lnk"           ""          "Emerald - the capture deck"
call :shortcut "%MENU%\Emerald EDL.lnk"       "--edl"     "Emerald - EDL Generator"
call :shortcut "%MENU%\Emerald Ingest.lnk"    "--ingest"  "Emerald - Ingest Controller"
call :shortcut "%MENU%\Emerald Monitor.lnk"   "--monitor" "Emerald - Monitor, capture to on air"
call :shortcut "%PUBLIC%\Desktop\Emerald.lnk" ""          "Emerald - the capture deck"

rem --------------------------------------------------- Add/Remove Programs entry
for /f %%S in ('powershell -NoProfile -Command "[math]::Round((Get-ChildItem -Recurse -File '%TARGET%' ^| Measure-Object Length -Sum).Sum / 1KB)"') do set "SIZEKB=%%S"

reg add "%UNINSTALL_KEY%" /f /v DisplayName     /t REG_SZ /d "Emerald" >nul
reg add "%UNINSTALL_KEY%" /f /v DisplayVersion  /t REG_SZ /d "1.0.0" >nul
reg add "%UNINSTALL_KEY%" /f /v Publisher       /t REG_SZ /d "Peregrine Broadcast" >nul
reg add "%UNINSTALL_KEY%" /f /v InstallLocation /t REG_SZ /d "%TARGET%" >nul
reg add "%UNINSTALL_KEY%" /f /v DisplayIcon     /t REG_SZ /d "%EXE%" >nul
reg add "%UNINSTALL_KEY%" /f /v UninstallString /t REG_SZ /d "\"%TARGET%\uninstall.bat\"" >nul
reg add "%UNINSTALL_KEY%" /f /v NoModify        /t REG_DWORD /d 1 >nul
reg add "%UNINSTALL_KEY%" /f /v NoRepair        /t REG_DWORD /d 1 >nul
if defined SIZEKB reg add "%UNINSTALL_KEY%" /f /v EstimatedSize /t REG_DWORD /d %SIZEKB% >nul

copy /y "%HERE%uninstall.bat" "%TARGET%\uninstall.bat" >nul

rem ------------------------------------------------------------ remote monitoring
rem
rem Windows blocks inbound connections, so the monitoring page would be served
rem perfectly well and reach nobody. The rule is added now, while we are elevated,
rem rather than leaving it as a thing to discover later. It opens one TCP port for
rem a read-only page; nothing served on it can change anything.

netsh advfirewall firewall delete rule name="Emerald monitoring 8787" >nul 2>&1
netsh advfirewall firewall add rule name="Emerald monitoring 8787" dir=in action=allow protocol=TCP localport=8787 >nul
if errorlevel 1 (
    echo [WARN] Could not add the firewall rule for remote monitoring. Other
    echo        machines will not reach the monitoring page until it is added.
) else (
    echo Firewall opened for remote monitoring on TCP 8787 ^(read-only^).
)

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
    echo          Emerald will open, and the Monitor and Live Edit will work, but
    echo          nothing can be captured or transmitted until the driver is
    echo          installed from DELTACAST's own installer. Emerald cannot carry it.
)

if exist "%TARGET%\ffmpeg.exe" (
    echo   [ok]   ffmpeg is in the install folder.
) else (
    where ffmpeg.exe >nul 2>&1
    if errorlevel 1 (
        set "READY=0"
        echo   [--]   ffmpeg NOT found. Recording, ingest and playout all need it.
        echo          Put ffmpeg.exe and ffprobe.exe in %TARGET%
    ) else (
        echo   [ok]   ffmpeg found on PATH.
    )
)

if exist "%TARGET%\ffprobe.exe" (
    echo   [ok]   ffprobe is in the install folder.
) else (
    where ffprobe.exe >nul 2>&1
    if errorlevel 1 (
        echo   [--]   ffprobe NOT found. Clip lengths and codecs will read as unknown.
    ) else (
        echo   [ok]   ffprobe found on PATH.
    )
)

if exist "%MEDIA%" (
    echo   [ok]   Store ready at %MEDIA%
) else (
    echo   [--]   Store folder %MEDIA% does not exist.
)

echo.
if "%READY%"=="1" (
    echo  Ready to run. Start Menu ^> Emerald, or the Desktop shortcut.
) else (
    echo  Emerald is installed but not yet ready to record - see the [--] lines above.
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
