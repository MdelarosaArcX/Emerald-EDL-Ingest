@echo off
rem ---------------------------------------------------------------------------
rem  Builds Emerald and launches the capture deck.
rem    run.bat              - Release build, then run
rem    run.bat Debug        - Debug build, then run
rem    run.bat Release -n   - skip the build, just run what is already built
rem
rem  Emerald runs once. If it is already up - started by run-edl.bat or
rem  run-ingest.bat, say - this brings the deck forward in that instance rather
rem  than starting a second copy, and the build is skipped because the running
rem  instance holds its own exe. To rebuild, close Emerald first.
rem
rem  One process is deliberate: the receiver allows a single open handle, and the
rem  preview, the EDL recorder and an ingest arbitrate for it through RxLease,
rem  which only works inside one process.
rem ---------------------------------------------------------------------------
setlocal

set "ROOT=%~dp0"

set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Release"

rem Building the solution sets Platform=x64 explicitly, and MSBuild folds that into the
rem output path. A direct "dotnet build" on the project alone would land in bin\%CONFIG%.
set "EXE=%ROOT%src\Emerald.App\bin\x64\%CONFIG%\net8.0-windows\win-x64\Emerald.App.exe"

rem See build.bat for why find.exe is fully qualified.
set "INTERACTIVE=0"
echo %cmdcmdline% | "%SystemRoot%\System32\find.exe" /i "/c" >nul && set "INTERACTIVE=1"

rem A running instance holds a lock on its own exe, so a rebuild would fail. It is also the
rem instance that will bring the deck forward, so there is nothing to rebuild for.
set "RUNNING=0"
tasklist /fi "imagename eq Emerald.App.exe" /nh 2>nul | "%SystemRoot%\System32\find.exe" /i "Emerald.App.exe" >nul && set "RUNNING=1"

if "%RUNNING%"=="1" (
    echo Emerald is already running - bringing the capture deck forward.
) else if /i "%~2"=="-n" (
    echo Skipping build.
) else (
    rem Scoped to this setlocal, so it never leaks into the caller's environment.
    set "EMERALD_NOPAUSE=1"
    call "%ROOT%build.bat" %CONFIG%
    if errorlevel 1 goto :fail
)

if not exist "%EXE%" (
    echo [ERROR] Executable not found: %EXE%
    echo         Run build.bat first, or check the configuration name.
    goto :fail
)

echo Launching Emerald ...
start "" "%EXE%"
exit /b 0

:fail
echo.
echo [ERROR] Could not start Emerald.
if "%INTERACTIVE%"=="1" pause
exit /b 1
