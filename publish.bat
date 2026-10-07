@echo off
rem ---------------------------------------------------------------------------
rem  Builds an installable Emerald into dist\.
rem    publish.bat            - the whole plant, into dist\Emerald
rem    publish.bat -edl       - the EDL Generator alone, into dist\Emerald-EDL
rem    publish.bat -noffmpeg  - either of those, without bundling ffmpeg
rem
rem  What comes out is a folder you copy anywhere - a USB stick, a network share -
rem  and install on a fresh Windows machine by running install.bat inside it.
rem
rem  It is published SELF-CONTAINED, which means the .NET runtime is inside the
rem  folder. That is the whole point: a fresh Windows box needs no .NET installed,
rem  no SDK, and no internet. It costs about 170 MB, which is the right trade for
rem  never having to install a runtime on a broadcast machine.
rem
rem  x64 only, and not by preference: VideoMasterHD.dll in System32 is 64-bit, so
rem  every assembly in the process has to be.
rem
rem  -------------------------------------------------------------------------
rem  WHY -edl IS NOT A SMALLER BUILD
rem
rem  It is the same binaries. One executable holds every deck, and which one opens
rem  is a command-line argument - the EDL shortcut passes --edl. The EDL also uses
rem  the same board, delay, media and timecode code as the rest, and the window
rem  styles every deck draws with live in Emerald.App itself.
rem
rem  So what -edl changes is the PRODUCT, not the payload: it installs into its own
rem  folder, lists itself separately in Add/Remove Programs, puts one shortcut on
rem  the machine instead of four, and leaves out the monitoring firewall rule that
rem  an EDL machine has no use for. An operator on that machine sees an EDL and
rem  nothing else, which is the point of asking for it.
rem
rem  Splitting the binaries for real would mean a second entry project with its own
rem  copy of those 80 window styles. Two copies of a stylesheet drift, and the
rem  saving would be a few megabytes against a 170 MB runtime that both need.
rem  -------------------------------------------------------------------------
rem ---------------------------------------------------------------------------
setlocal EnableDelayedExpansion

set "ROOT=%~dp0"
set "PROJECT=%ROOT%src\Emerald.App\Emerald.App.csproj"

rem ---------------------------------------------------------------- the flavour
rem
rem Scanned rather than positional, so -edl and -noffmpeg can be given in either
rem order. The old form - publish.bat -noffmpeg - still means what it did.
set "FLAVOUR=full"
set "WANTFFMPEG=1"

:args
if "%~1"=="" goto :argsdone
if /i "%~1"=="-edl" set "FLAVOUR=edl"
if /i "%~1"=="-noffmpeg" set "WANTFFMPEG=0"
shift
goto :args
:argsdone

if /i "%FLAVOUR%"=="edl" (
    set "PRODUCT=Emerald EDL"
    set "DIST=%ROOT%dist\Emerald-EDL"
    set "INSTALLSRC=%ROOT%installer\edl"
) else (
    set "PRODUCT=Emerald"
    set "DIST=%ROOT%dist\Emerald"
    set "INSTALLSRC=%ROOT%installer"
)

rem See build.bat for why find.exe is fully qualified.
set "INTERACTIVE=0"
echo %cmdcmdline% | "%SystemRoot%\System32\find.exe" /i "/c" >nul && set "INTERACTIVE=1"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] 'dotnet' is not on PATH. Install the .NET 8 SDK from
    echo         https://dotnet.microsoft.com/download/dotnet/8.0
    goto :fail
)

rem A running Emerald holds its own files open, and the publish has to rebuild them -
rem so it would fail half way with an MSBuild error about a locked file.
rem
rem Tested by asking whether the file can actually be written rather than by looking
rem for a process by name. That is the thing that matters, and it gets the distinction
rem right for free: an INSTALLED copy running in Program Files does not lock anything
rem here, and refusing to publish because of it would be refusing the ordinary case of
rem building a new version on the machine running the old one.
rem
rem The redirection opens the file for append and closes it without writing a byte.
set "BUILT=%ROOT%src\Emerald.App\bin\x64\Release\net8.0-windows\win-x64\Emerald.App.exe"

if exist "%BUILT%" (
    2>nul ( >>"%BUILT%" call ) || (
        echo [ERROR] Emerald is running from this source tree and is holding its own
        echo         files open:
        echo           %BUILT%
        echo.
        echo         Close it and run publish.bat again. An installed copy running
        echo         from Program Files is not a problem.
        goto :fail
    )
)

echo Publishing %PRODUCT%, self-contained, x64 ...
echo.

if exist "%DIST%" rmdir /s /q "%DIST%"
mkdir "%DIST%" 2>nul

dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true -p:Platform=x64 ^
    -o "%DIST%" --nologo
if errorlevel 1 goto :fail

rem -------------------------------------------------------------------- ffmpeg
rem
rem Emerald looks for ffmpeg.exe beside its own executable BEFORE anything on PATH
rem (see Ffmpeg.Locate), so dropping it in here is all the configuration a fresh
rem machine needs. ffprobe comes too: the store's clip list and the EDL's timecode
rem reading both use it.

if "%WANTFFMPEG%"=="0" (
    echo.
    echo Skipping ffmpeg. The install will report it missing until you put
    echo ffmpeg.exe and ffprobe.exe in the install folder or on PATH.
    goto :extras
)

set "FFMPEG="
for %%D in ("C:\ffmpeg\bin" "C:\Program Files\ffmpeg\bin") do (
    if exist "%%~D\ffmpeg.exe" if not defined FFMPEG set "FFMPEG=%%~D"
)

if not defined FFMPEG (
    for /f "delims=" %%P in ('where ffmpeg.exe 2^>nul') do if not defined FFMPEG set "FFMPEG=%%~dpP"
)

if not defined FFMPEG (
    echo.
    echo [WARN] ffmpeg was not found on this machine, so it has not been bundled.
    echo        Emerald cannot record or play out without it. Put ffmpeg.exe and
    echo        ffprobe.exe into "%DIST%" before installing, or install ffmpeg on
    echo        the target machine.
    goto :extras
)

echo.
echo Bundling ffmpeg from %FFMPEG%
copy /y "%FFMPEG%\ffmpeg.exe" "%DIST%\ffmpeg.exe" >nul
if errorlevel 1 goto :fail

if exist "%FFMPEG%\ffprobe.exe" (
    copy /y "%FFMPEG%\ffprobe.exe" "%DIST%\ffprobe.exe" >nul
) else (
    echo [WARN] ffprobe.exe was not beside ffmpeg.exe. Clip durations and codecs
    echo        will read as unknown until it is there.
)

:extras

rem The installer and its notes travel inside the folder they install. Which set of
rem them depends on the flavour, and that is the only difference in what ships.
if not exist "%INSTALLSRC%\install.bat" (
    echo [ERROR] No installer scripts in %INSTALLSRC%
    goto :fail
)

copy /y "%INSTALLSRC%\install.bat" "%DIST%\install.bat" >nul
copy /y "%INSTALLSRC%\uninstall.bat" "%DIST%\uninstall.bat" >nul
copy /y "%INSTALLSRC%\update.bat" "%DIST%\update.bat" >nul
copy /y "%INSTALLSRC%\READ-ME-FIRST.txt" "%DIST%\READ-ME-FIRST.txt" >nul

echo.
echo ---------------------------------------------------------------------------
echo  Ready:  %DIST%
echo.
for /f %%S in ('powershell -NoProfile -Command "[math]::Round((Get-ChildItem -Recurse -File '%DIST%' ^| Measure-Object Length -Sum).Sum / 1GB, 2)"') do echo  Size:   %%S GB
echo.
echo  Copy that folder to the new machine and run install.bat as administrator.
echo  It installs "%PRODUCT%".
echo ---------------------------------------------------------------------------
if "%INTERACTIVE%"=="1" pause
exit /b 0

:fail
echo.
echo [ERROR] Publish failed.
if "%INTERACTIVE%"=="1" pause
exit /b 1
