@echo off
rem ---------------------------------------------------------------------------
rem  Installs Emerald on this machine.
rem
rem    install.bat                                - install, asking where things go
rem    install.bat D:\Emerald\media               - recordings in its high\ and low\,
rem                                                 the database where it always was
rem    install.bat D:\Masters E:\Proxies D:\Data   - high-res, low-res and database folders
rem    ... -q                                     - the same, and no pauses (for a rollout)
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

set "MEDIA="
set "HIGH="
set "LOW="
set "DATA="
set "QUIET=0"
for %%A in (%*) do if /i "%%~A"=="-q" set "QUIET=1"

rem Three folders given: high-res, low-res and the database. One: the store as it always
rem was, masters in its high\ and proxies in its low\.
if not "%~3"=="" if /i not "%~3"=="-q" (
    set "HIGH=%~1"
    set "LOW=%~2"
    set "DATA=%~3"
)
if not defined HIGH if not "%~1"=="" if /i not "%~1"=="-q" set "MEDIA=%~1"

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

rem -------------------------------------------------------------- where things go
rem
rem This matters more than it looks. Left alone, Emerald puts its store beside its
rem own executable, which after an install is inside Program Files - not writable
rem by a standard user, and the wrong place for terabytes of video besides. So the
rem folders are settled here, at install time, and written into the settings.
rem
rem Three of them. The high-res masters (DNxHD, about 15 MB/s while recording) and
rem the low-res proxies Live Edit plays can go on different drives: on one spinning
rem disk, every read of a proxy takes the head away from the masters being written.
rem And the database - the log of everything, the recordings' resource samples, the
rem ingest schedule - can go on any drive, ideally not the one the masters go to.
rem
rem Already configured: an update asks only for what is not set yet, and never
rem moves a store that has terabytes in it.

set "SETTINGSPS=%HERE%settings.ps1"
set "HAVE_CAPTURE="
set "HAVE_HIGH="
set "HAVE_LOW="
set "HAVE_DATA="

if exist "%SETTINGS%" if exist "%SETTINGSPS%" (
    for /f "usebackq tokens=1,* delims==" %%K in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%SETTINGSPS%" -Settings "%SETTINGS%" -Read`) do (
        if /i "%%K"=="captureFolder" set "HAVE_CAPTURE=%%L"
        if /i "%%K"=="highResFolder" set "HAVE_HIGH=%%L"
        if /i "%%K"=="lowResFolder" set "HAVE_LOW=%%L"
        if /i "%%K"=="dataFolder" set "HAVE_DATA=%%L"
    )
)

rem One store given on the command line stands for both halves of it.
if defined MEDIA (
    if not defined HIGH set "HIGH=%MEDIA%\high"
    if not defined LOW set "LOW=%MEDIA%\low"
)

rem What is already set stays; a store set the old way already says where both go.
if not defined HIGH if defined HAVE_HIGH set "HIGH=!HAVE_HIGH!"
if not defined LOW if defined HAVE_LOW set "LOW=!HAVE_LOW!"
if not defined HIGH if defined HAVE_CAPTURE set "HIGH=!HAVE_CAPTURE!\high"
if not defined LOW if defined HAVE_CAPTURE set "LOW=!HAVE_CAPTURE!\low"
if not defined DATA if defined HAVE_DATA set "DATA=!HAVE_DATA!"

rem Offer the roomiest fixed drive that is not the system one, since that is almost
rem always where a plant wants its recordings.
set "BEST="
for /f "skip=1 tokens=1,2" %%D in ('powershell -NoProfile -Command "Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | Sort-Object -Property FreeSpace -Descending | Select-Object DeviceID,FreeSpace | Format-Table -HideTableHeaders"') do (
    if not defined BEST if not "%%D"=="" (
        if /i not "%%D"=="%SystemDrive%" set "BEST=%%D"
    )
)
if not defined BEST set "BEST=%SystemDrive%"

set "ASKING=0"
if not defined HIGH set "ASKING=1"
if not defined LOW set "ASKING=1"
if not defined DATA set "ASKING=1"

if "%ASKING%"=="1" if "%INTERACTIVE%"=="1" (
    echo The drives on this machine:
    echo.
    powershell -NoProfile -ExecutionPolicy Bypass -File "%SETTINGSPS%" -Drives
    echo.
    echo Recordings need folders with room for them. Emerald will not put them in
    echo Program Files. The masters and the proxies can be on different drives.
    echo.
)

if not defined HIGH (
    set "HIGH=%BEST%\Emerald\media\high"
    if "%INTERACTIVE%"=="1" (
        set "ANSWER="
        set /p "ANSWER=High-res folder, for the DNxHD masters? [!HIGH!] "
        if not "!ANSWER!"=="" set "HIGH=!ANSWER!"
    )
)

if not defined LOW (
    set "LOW=%BEST%\Emerald\media\low"
    if "%INTERACTIVE%"=="1" (
        set "ANSWER="
        set /p "ANSWER=Low-res folder, for the proxies Live Edit plays? [!LOW!] "
        if not "!ANSWER!"=="" set "LOW=!ANSWER!"
    )
)

if not defined DATA (
    set "DATA=%APPDATA%\Emerald"
    if "%INTERACTIVE%"=="1" (
        echo.
        echo The database holds the log of everything Emerald does and the machine's
        echo CPU, memory, graphics and disk figures while it records. It grows by a
        echo few hundred megabytes a week and is trimmed to the last 14 days.
        set "ANSWER="
        set /p "ANSWER=Database folder? [!DATA!] "
        if not "!ANSWER!"=="" set "DATA=!ANSWER!"
    )
)

echo.
echo   Program        %TARGET%
echo   High-res       !HIGH!
echo   Low-res        !LOW!
echo   Database       !DATA!
echo   Settings       %SETTINGS%
echo.

rem ------------------------------------------------------------------- the files
echo Copying files ...

if not exist "%TARGET%" mkdir "%TARGET%" 2>nul

rem /e all subdirectories, /purge remove what is no longer shipped, /njh /njs /ndl /nc /ns
rem quiet. Robocopy returns 0-7 for success; 8 and above is a real failure.
robocopy "%HERE%." "%TARGET%" /e /purge /xf install.bat uninstall.bat update.bat settings.ps1 READ-ME-FIRST.txt /njh /njs /ndl /nc /ns /nfl >nul
if errorlevel 8 (
    echo [ERROR] Copying to %TARGET% failed.
    goto :fail
)

for %%F in ("!HIGH!" "!LOW!" "!DATA!") do (
    if not exist "%%~F" mkdir "%%~F" 2>nul
    if not exist "%%~F" (
        echo [WARN] Could not create %%~F. Set it on the capture deck after installing.
    )
)

rem ---------------------------------------------------------------- the settings
rem
rem Merged into the settings file: a folder already chosen is never overwritten, and
rem nothing else in the file is touched, so reinstalling over a working machine keeps
rem what the operator has set up.

if exist "%SETTINGSPS%" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%SETTINGSPS%" -Settings "%SETTINGS%" -High "!HIGH!" -Low "!LOW!" -Data "!DATA!"
    if errorlevel 1 (
        echo [WARN] Could not write the settings file. Set the folders on the capture
        echo        deck after installing.
    ) else (
        echo Settings written.
    )
) else (
    echo [WARN] settings.ps1 is not beside install.bat, so the folders were not saved.
    echo        Set them on the capture deck after installing.
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

if exist "!HIGH!" (echo   [ok]   High-res folder ready at !HIGH!) else (echo   [--]   High-res folder !HIGH! does not exist.)
if exist "!LOW!" (echo   [ok]   Low-res folder ready at !LOW!) else (echo   [--]   Low-res folder !LOW! does not exist.)
if exist "!DATA!" (echo   [ok]   Database folder ready at !DATA!) else (echo   [--]   Database folder !DATA! does not exist.)

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
