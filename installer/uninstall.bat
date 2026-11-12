@echo off
rem ---------------------------------------------------------------------------
rem  Removes Emerald from this machine.
rem
rem    uninstall.bat      - remove the program, ask about the recordings
rem    uninstall.bat -q   - remove the program, keep everything else, no questions
rem
rem  Recordings and settings are NOT removed unless you say so. A deinstall that
rem  quietly took a plant's recordings with it would be unforgivable, and the
rem  store is usually terabytes on another drive besides.
rem ---------------------------------------------------------------------------
setlocal EnableDelayedExpansion

set "TARGET=%ProgramFiles%\Emerald"
set "MENU=%ProgramData%\Microsoft\Windows\Start Menu\Programs\Emerald"
set "UNINSTALL_KEY=HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\Emerald"

set "QUIET=0"
if /i "%~1"=="-q" set "QUIET=1"

set "INTERACTIVE=0"
echo %cmdcmdline% | "%SystemRoot%\System32\find.exe" /i "/c" >nul && set "INTERACTIVE=1"
if "%QUIET%"=="1" set "INTERACTIVE=0"

echo ===========================================================================
echo  Emerald - uninstall
echo ===========================================================================
echo.

net session >nul 2>&1
if errorlevel 1 (
    echo [ERROR] This needs to run as administrator.
    echo         Right-click uninstall.bat and choose "Run as administrator".
    goto :fail
)

tasklist /fi "imagename eq Emerald.App.exe" /nh 2>nul | "%SystemRoot%\System32\find.exe" /i "Emerald.App.exe" >nul
if not errorlevel 1 (
    echo [ERROR] Emerald is running. Close it first.
    goto :fail
)

rem What the folders are set to, so they can be named rather than guessed at.
set "MEDIA="
set "HIGH="
set "LOW="
set "DATA="
if exist "%APPDATA%\Emerald\settings.json" (
    for /f "delims=" %%F in ('powershell -NoProfile -Command "try { (Get-Content -Raw '%APPDATA%\Emerald\settings.json' | ConvertFrom-Json).captureFolder } catch { '' }"') do set "MEDIA=%%F"
    for /f "delims=" %%F in ('powershell -NoProfile -Command "try { (Get-Content -Raw '%APPDATA%\Emerald\settings.json' | ConvertFrom-Json).highResFolder } catch { '' }"') do set "HIGH=%%F"
    for /f "delims=" %%F in ('powershell -NoProfile -Command "try { (Get-Content -Raw '%APPDATA%\Emerald\settings.json' | ConvertFrom-Json).lowResFolder } catch { '' }"') do set "LOW=%%F"
    for /f "delims=" %%F in ('powershell -NoProfile -Command "try { (Get-Content -Raw '%APPDATA%\Emerald\settings.json' | ConvertFrom-Json).dataFolder } catch { '' }"') do set "DATA=%%F"
)
if not defined DATA set "DATA=%APPDATA%\Emerald"

echo Removing the program from %TARGET%
echo.

rem The uninstaller is inside the folder it is deleting, so it cannot delete itself
rem while running. Everything else goes now; the folder and this file go on the way
rem out, handed to a detached cmd that waits for this process to end first.
for /d %%D in ("%TARGET%\*") do rmdir /s /q "%%D" 2>nul
for %%F in ("%TARGET%\*") do if /i not "%%~nxF"=="uninstall.bat" del /f /q "%%F" 2>nul

if exist "%MENU%" rmdir /s /q "%MENU%" 2>nul
if exist "%PUBLIC%\Desktop\Emerald.lnk" del /f /q "%PUBLIC%\Desktop\Emerald.lnk" 2>nul

reg delete "%UNINSTALL_KEY%" /f >nul 2>&1

netsh advfirewall firewall delete rule name="Emerald monitoring 8787" >nul 2>&1

echo   [ok]   Program, shortcuts, firewall rule and Add/Remove entry removed.
echo.

if "%QUIET%"=="1" goto :keep

echo Your recordings and settings have been left alone:
echo.
if defined HIGH (echo   High-res     !HIGH!) else if defined MEDIA (echo   Recordings   !MEDIA!) else (echo   Recordings   wherever the capture deck was pointed)
if defined LOW echo   Low-res      !LOW!
echo   Settings     %APPDATA%\Emerald
echo   Database     !DATA!
echo.

rem The database first, and on its own: it can be beside the settings, and it is the
rem record of what went to air. Answering no to it keeps it whatever is said next.
if exist "!DATA!\emerald.db" (
    set "ANSWER="
    set /p "ANSWER=Delete the database in !DATA! - the log and resource history? [n] "
    if /i "!ANSWER!"=="y" (
        del /f /q "!DATA!\emerald.db" "!DATA!\emerald.db-wal" "!DATA!\emerald.db-shm" "!DATA!\ingest.db" 2>nul
        if exist "!DATA!\peaks" rmdir /s /q "!DATA!\peaks" 2>nul
        echo   [ok]   Database removed.
    ) else (
        echo   Database kept.
    )
)

rem The settings and what lives with them - never the database files, which were
rem asked about above.
set "ANSWER="
set /p "ANSWER=Delete the settings as well? (recordings are never touched) [n] "
if /i "!ANSWER!"=="y" (
    del /f /q "%APPDATA%\Emerald\settings.json" "%APPDATA%\Emerald\settings.json.tmp" 2>nul
    if exist "%APPDATA%\Emerald\logs" rmdir /s /q "%APPDATA%\Emerald\logs" 2>nul
    if exist "%APPDATA%\Emerald\bookmarks" rmdir /s /q "%APPDATA%\Emerald\bookmarks" 2>nul
    echo   [ok]   Settings removed.
) else (
    echo   Settings kept.
)

:keep

echo.
echo ---------------------------------------------------------------------------
echo  Emerald removed.
echo ---------------------------------------------------------------------------
if "%INTERACTIVE%"=="1" pause

rem Last act: a detached shell that waits a moment for this one to exit, then takes
rem the folder and this script with it.
start "" /min cmd /c "timeout /t 2 /nobreak >nul & rmdir /s /q ""%TARGET%"" 2>nul"
exit /b 0

:fail
echo.
echo [ERROR] Uninstall did not complete.
if "%INTERACTIVE%"=="1" pause
exit /b 1
