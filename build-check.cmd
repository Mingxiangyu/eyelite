@echo off
rem ============================================================
rem  build-check.cmd - syntax gate (never touches EyeLite.exe)
rem
rem  Run this while EyeLite.exe is running (its file is locked) to verify
rem  that the source still compiles and that the embedded resource names
rem  are correct. Output goes to %TEMP% and is deleted right away.
rem
rem  A real build is still build.cmd (produces EyeLite.exe + fingerprint).
rem
rem  NOTE: keep this file pure ASCII, same reason as build.cmd.
rem ============================================================
call "%~dp0build.cmd" /check
