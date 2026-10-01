@echo off
rem Double-click or run:  release.cmd            (patch release)
rem                       release.cmd -Bump minor -Notes "what changed"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0release.ps1" %*
pause
