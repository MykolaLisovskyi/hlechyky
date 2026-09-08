@echo off
title Hlechyky
rem Stops the server and liquidsoap. Icecast and the Spotify stream from D:\radio keep running.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start.ps1" stop
pause
