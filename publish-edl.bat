@echo off
rem ---------------------------------------------------------------------------
rem  Builds the EDL-only installer into dist\Emerald-EDL.
rem    publish-edl.bat            - build it
rem    publish-edl.bat -noffmpeg  - build it without bundling ffmpeg
rem
rem  This is a one-line wrapper on publish.bat so that the EDL build has an
rem  obvious name of its own. The work, and the explanation of what an EDL-only
rem  build actually differs in, are both in publish.bat.
rem ---------------------------------------------------------------------------
call "%~dp0publish.bat" -edl %*
exit /b %errorlevel%
