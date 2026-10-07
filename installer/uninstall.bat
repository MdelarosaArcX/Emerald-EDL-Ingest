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

rem What the store is set to, so it can be named rather than guessed at.
set "MEDIA="
if exist "%APPDATA%\Emerald\settings.json" (
    for /f "delims=" %%F in ('powershell -NoProfile -Command "try { (Get-Content -Raw '%APPDATA%\Emerald\settings.json' | ConvertFrom-Json).captureFolder } catch { '' }"') do set "MEDIA=%%F"
)

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
if defined MEDIA (echo   Recordings   !MEDIA!) else (echo   Recordings   wherever the capture deck was pointed)
echo   Settings     %APPDATA%\Emerald
echo   Logs         %APPDATA%\Emerald\logs
echo.

set "ANSWER="
set /p "ANSWER=Delete the settings and logs as well? (recordings are never touched) [n] "
if /i "!ANSWER!"=="y" (
    rmdir /s /q "%APPDATA%\Emerald" 2>nul
    echo   [ok]   Settings and logs removed.
) else (
    echo   Settings and logs kept.
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
