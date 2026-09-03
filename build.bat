@echo off
rem ---------------------------------------------------------------------------
rem  Builds the whole Emerald solution.
rem    build.bat            - Release build (default)
rem    build.bat Debug      - Debug build
rem    build.bat Release -r - Release build, restoring packages from scratch
rem ---------------------------------------------------------------------------
setlocal

set "ROOT=%~dp0"
set "SOLUTION=%ROOT%Emerald.sln"

set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Release"

rem Pause at the end only when launched by double-click, so the window stays readable.
rem find.exe is fully qualified because a PATH carrying Unix tools ahead of System32
rem (an option in the Git for Windows installer) shadows it with GNU find.
set "INTERACTIVE=0"
echo %cmdcmdline% | "%SystemRoot%\System32\find.exe" /i "/c" >nul && set "INTERACTIVE=1"

rem run.bat sets this so a double-clicked run does not stop for a keypress mid-way.
if defined EMERALD_NOPAUSE set "INTERACTIVE=0"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] 'dotnet' is not on PATH. Install the .NET 8 SDK from
    echo         https://dotnet.microsoft.com/download/dotnet/8.0
    goto :fail
)

if not exist "%SOLUTION%" (
    echo [ERROR] Solution not found: %SOLUTION%
    goto :fail
)

echo Building Emerald [%CONFIG%] ...
echo.

if /i "%~2"=="-r" (
    dotnet restore "%SOLUTION%" --nologo
    if errorlevel 1 goto :fail
)

rem Every project is x64 only, so the solution maps Any CPU onto it; building the
rem solution rather than the app project keeps that mapping in play.
dotnet build "%SOLUTION%" -c %CONFIG% --nologo
if errorlevel 1 goto :fail

echo.
echo Build succeeded.  Output:
echo   %ROOT%src\Emerald.App\bin\x64\%CONFIG%\net8.0-windows\win-x64\Emerald.App.exe
if "%INTERACTIVE%"=="1" pause
exit /b 0

:fail
echo.
echo [ERROR] Build failed.
if "%INTERACTIVE%"=="1" pause
exit /b 1
