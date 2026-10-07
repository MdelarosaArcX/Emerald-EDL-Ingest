@echo off
rem ---------------------------------------------------------------------------
rem  Updates an installed Emerald EDL from a folder or a network share.
rem
rem    update.bat                                      - update from this folder
rem    update.bat \\master\emerald\dist\Emerald-EDL    - update from a share
rem    update.bat \\master\emerald\dist\Emerald-EDL -q - the same, no questions
rem
rem  Run it as administrator. Put it anywhere - the Desktop, a scheduled task -
rem  and point it at wherever publish-edl.bat's output is shared from.
rem
rem  What it does NOT touch: the media, and the settings. The program folder is
rem  replaced wholesale, which is what makes an update an update rather than a
rem  pile of old files with new ones on top. Everything the operator has set up
rem  lives in %APPDATA%\Emerald and is left exactly as it is.
rem
rem  It updates the EDL install only. A full Emerald on the same machine has its
rem  own folder and its own update.bat.
rem
rem  It checks the version before copying, so pointing it at the build you are
rem  already running costs a few seconds and changes nothing.
rem ---------------------------------------------------------------------------
setlocal EnableDelayedExpansion

rem No argument means "the folder I am sitting in", so a copied folder can simply have
rem update.bat double-clicked in it. A path is for updating from a share without
rem copying anything.
set "SOURCE=%~1"
if "%SOURCE%"=="" if exist "%~dp0Emerald.App.exe" set "SOURCE=%~dp0"

rem Normalised to end in exactly one backslash, so "%SOURCE%." below is the folder
rem itself. Without this, a path given with a trailing slash becomes "...\\.." - the
rem PARENT - and robocopy would purge the install and fill it with the wrong folder.
rem
rem Delayed expansion, not %SOURCE:~-1%, and that is not a style choice. cmd parses
rem the whole line before deciding the "if defined" is false, and a substring of an
rem UNDEFINED variable is left as literal text - which leaves the quotes unbalanced
rem and kills the script with "The syntax of the command is incorrect" before it can
rem print the help below. Measured: with no argument, exit 255 and no output.
if defined SOURCE if not "!SOURCE:~-1!"=="\" set "SOURCE=!SOURCE!\"
set "TARGET=%ProgramFiles%\Emerald EDL"
set "QUIET=0"
if /i "%~2"=="-q" set "QUIET=1"

set "INTERACTIVE=0"
echo %cmdcmdline% | "%SystemRoot%\System32\find.exe" /i "/c" >nul && set "INTERACTIVE=1"
if "%QUIET%"=="1" set "INTERACTIVE=0"

echo ===========================================================================
echo  Emerald EDL - update
echo ===========================================================================
echo.

if "%SOURCE%"=="" (
    echo [ERROR] Say where the new build is, or put this file inside it.
    echo.
    echo         From a share, nothing copied:
    echo           update.bat \\master\emerald\dist\Emerald-EDL
    echo.
    echo         Or copy the published folder to this machine and double-click
    echo         update.bat inside it.
    goto :fail
)

net session >nul 2>&1
if errorlevel 1 (
    echo [ERROR] This needs to run as administrator.
    echo         Right-click update.bat and choose "Run as administrator".
    goto :fail
)

if not exist "%SOURCE%Emerald.App.exe" (
    echo [ERROR] No Emerald.App.exe in:
    echo           %SOURCE%
    echo.
    echo         That should be the folder publish-edl.bat produced -
    echo         dist\Emerald-EDL.
    goto :fail
)

if not exist "%TARGET%\Emerald.App.exe" (
    echo [ERROR] The Emerald EDL is not installed here yet.
    echo         Run install.bat from the new folder instead; it does both.
    echo.
    echo         If you meant to update a FULL Emerald, that lives in
    echo         "%ProgramFiles%\Emerald" and has its own update.bat.
    goto :fail
)

rem ------------------------------------------------------------------ is it newer
rem
rem By the executable's write time, which is what a rebuild moves. Not a version
rem string: nothing bumps one on this project, and a check that silently passes
rem because two numbers are equal is worse than no check.

for /f "delims=" %%T in ('powershell -NoProfile -Command "(Get-Item '%SOURCE%Emerald.App.exe').LastWriteTimeUtc.Ticks"') do set "NEWTICKS=%%T"
for /f "delims=" %%T in ('powershell -NoProfile -Command "(Get-Item '%TARGET%\Emerald.App.exe').LastWriteTimeUtc.Ticks"') do set "OLDTICKS=%%T"

for /f "delims=" %%D in ('powershell -NoProfile -Command "(Get-Item '%SOURCE%Emerald.App.exe').LastWriteTime.ToString('yyyy-MM-dd HH:mm')"') do set "NEWWHEN=%%D"
for /f "delims=" %%D in ('powershell -NoProfile -Command "(Get-Item '%TARGET%\Emerald.App.exe').LastWriteTime.ToString('yyyy-MM-dd HH:mm')"') do set "OLDWHEN=%%D"

echo   installed   %OLDWHEN%
echo   available   %NEWWHEN%
echo.

if "%NEWTICKS%"=="%OLDTICKS%" (
    echo Already running this build. Nothing to do.
    goto :done
)

rem -------------------------------------------------------------- is it in the way
rem
rem By name, because the EDL and a full Emerald are the same executable: either one
rem running holds these files open.
tasklist /fi "imagename eq Emerald.App.exe" /nh 2>nul | "%SystemRoot%\System32\find.exe" /i "Emerald.App.exe" >nul
if not errorlevel 1 (
    echo [ERROR] Emerald is running, and its own files cannot be replaced while it
    echo         holds them open. That includes a full Emerald on this machine.
    echo.
    echo         Close it and run this again. If something is on air, let it finish
    echo         first - closing the EDL mid-playout takes the transmitter with it.
    goto :fail
)

if "%QUIET%"=="1" goto :copy

set "ANSWER="
set /p "ANSWER=Replace the installed program with the build above? [y] "
if /i "!ANSWER!"=="n" (
    echo Left alone.
    goto :done
)

:copy

echo.
echo Replacing %TARGET% ...

rem /purge so a file that is no longer shipped goes, rather than being left behind to
rem be loaded by a version that knows nothing about it. The installer scripts are not
rem copied in: they belong to the folder they came from.
robocopy "%SOURCE%." "%TARGET%" /e /purge /xf install.bat uninstall.bat update.bat READ-ME-FIRST.txt /njh /njs /ndl /nc /ns /nfl >nul
if errorlevel 8 (
    echo [ERROR] Copy failed. The install may be half replaced - run it again, or
    echo         run install.bat from the new folder.
    goto :fail
)

echo.
echo ---------------------------------------------------------------------------
echo  Updated to the build of %NEWWHEN%.
echo.
echo  Media and settings untouched. Start Menu ^> Emerald EDL.
echo ---------------------------------------------------------------------------

:done
echo.
if "%INTERACTIVE%"=="1" pause
exit /b 0

:fail
echo.
echo [ERROR] Update did not complete.
if "%INTERACTIVE%"=="1" pause
exit /b 1
