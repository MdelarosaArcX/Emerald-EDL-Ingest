@echo off
rem ---------------------------------------------------------------------------
rem  Removes the Emerald EDL Generator from this machine.
rem
rem    uninstall.bat      - remove the program, ask about the settings
rem    uninstall.bat -q   - remove the program, keep everything else, no questions
rem
rem  Media and settings are NOT removed unless you say so, and media is never
rem  touched at all: the files an EDL plays are the plant's, not this program's.
rem
rem  This removes only the EDL install. A full Emerald in "%ProgramFiles%\Emerald"
rem  is a separate program and is left exactly as it is.
rem ---------------------------------------------------------------------------
setlocal EnableDelayedExpansion

set "TARGET=%ProgramFiles%\Emerald EDL"
set "MENU=%ProgramData%\Microsoft\Windows\Start Menu\Programs\Emerald EDL"
set "UNINSTALL_KEY=HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\EmeraldEDL"
set "FULL=%ProgramFiles%\Emerald\Emerald.App.exe"

set "QUIET=0"
if /i "%~1"=="-q" set "QUIET=1"

set "INTERACTIVE=0"
echo %cmdcmdline% | "%SystemRoot%\System32\find.exe" /i "/c" >nul && set "INTERACTIVE=1"
if "%QUIET%"=="1" set "INTERACTIVE=0"

echo ===========================================================================
echo  Emerald EDL - uninstall
echo ===========================================================================
echo.

net session >nul 2>&1
if errorlevel 1 (
    echo [ERROR] This needs to run as administrator.
    echo         Right-click uninstall.bat and choose "Run as administrator".
    goto :fail
)

if not exist "%TARGET%" (
    echo [ERROR] No Emerald EDL install found at
    echo         %TARGET%
    goto :fail
)

tasklist /fi "imagename eq Emerald.App.exe" /nh 2>nul | "%SystemRoot%\System32\find.exe" /i "Emerald.App.exe" >nul
if not errorlevel 1 (
    echo [ERROR] Emerald is running. Close it first. That includes a full Emerald,
    echo         which is the same executable.
    goto :fail
)

rem What the media folder is set to, so it can be named rather than guessed at.
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
if exist "%PUBLIC%\Desktop\Emerald EDL.lnk" del /f /q "%PUBLIC%\Desktop\Emerald EDL.lnk" 2>nul

reg delete "%UNINSTALL_KEY%" /f >nul 2>&1

rem No firewall rule to remove: the EDL install never added one. Deleting the
rem monitoring rule here would break a full Emerald on the same machine.

echo   [ok]   Program, shortcut and Add/Remove entry removed.
echo.

rem --------------------------------------------------------- the shared settings
rem
rem One settings file serves every Emerald on the machine. If a full Emerald is
rem still installed, removing that file would strip its configuration too, so it
rem is not even offered.

if "%QUIET%"=="1" goto :keep

if exist "%FULL%" (
    echo A full Emerald is still installed on this machine and shares the settings
    echo file, so the settings and logs have been left alone:
    echo.
    echo   Settings     %APPDATA%\Emerald
    goto :keep
)

echo Your media and settings have been left alone:
echo.
if defined MEDIA (echo   Media        !MEDIA!) else (echo   Media        wherever the EDL was pointed)
echo   Settings     %APPDATA%\Emerald
echo   Logs         %APPDATA%\Emerald\logs
echo.

set "ANSWER="
set /p "ANSWER=Delete the settings and logs as well? (media is never touched) [n] "
if /i "!ANSWER!"=="y" (
    rmdir /s /q "%APPDATA%\Emerald" 2>nul
    echo   [ok]   Settings and logs removed.
) else (
    echo   Settings and logs kept.
)

:keep

echo.
echo ---------------------------------------------------------------------------
echo  Emerald EDL removed.
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
