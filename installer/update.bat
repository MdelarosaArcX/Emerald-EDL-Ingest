@echo off
rem ---------------------------------------------------------------------------
rem  Updates an installed Emerald from a folder or a network share.
rem
rem    update.bat                                   - update from this folder
rem    update.bat \\master\emerald\dist\Emerald     - update from a share
rem    update.bat \\master\emerald\dist\Emerald -q  - the same, no questions
rem
rem  Run it as administrator. Put it anywhere - the Desktop, a scheduled task -
rem  and point it at wherever publish.bat's output is shared from.
rem
rem  What it does NOT touch: the recordings, and the settings. The program folder
rem  is replaced wholesale, which is what makes an update an update rather than a
rem  pile of old files with new ones on top. Everything the operator has set up
rem  lives in %APPDATA%\Emerald and is left exactly as it is.
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
set "TARGET=%ProgramFiles%\Emerald"
set "QUIET=0"
if /i "%~2"=="-q" set "QUIET=1"

set "INTERACTIVE=0"
echo %cmdcmdline% | "%SystemRoot%\System32\find.exe" /i "/c" >nul && set "INTERACTIVE=1"
if "%QUIET%"=="1" set "INTERACTIVE=0"

echo ===========================================================================
echo  Emerald - update
echo ===========================================================================
echo.

if "%SOURCE%"=="" (
    echo [ERROR] Say where the new build is, or put this file inside it.
    echo.
    echo         From a share, nothing copied:
    echo           update.bat \\master\emerald\dist\Emerald
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
    echo         That should be the folder publish.bat produced - dist\Emerald.
    goto :fail
)

if not exist "%TARGET%\Emerald.App.exe" (
    echo [ERROR] Emerald is not installed here yet.
    echo         Run install.bat from the new folder instead; it does both.
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
tasklist /fi "imagename eq Emerald.App.exe" /nh 2>nul | "%SystemRoot%\System32\find.exe" /i "Emerald.App.exe" >nul
if not errorlevel 1 (
    echo [ERROR] Emerald is running, and its own files cannot be replaced while it
    echo         holds them open.
    echo.
    echo         Close it and run this again. If it is recording, stop the recording
    echo         first - closing Emerald mid-recording leaves the segment it was
    echo         writing without its index, and that segment will not play.
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
echo  Recordings and settings untouched. Start Menu ^> Emerald.
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
