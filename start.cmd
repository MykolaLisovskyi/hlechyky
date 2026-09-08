@echo off
title Hlechyky
rem Double-click: brings up Icecast (via D:\radio, or liquidsoap\docker-compose.dev.yml when D:\radio is absent), liquidsoap, the server and Caddy, then shows status.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start.ps1" start
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start.ps1" status
echo.
echo Site: https://hlechyky.pp.ua   (friends: https://hlechyky.pp.ua/?k=...; local: http://127.0.0.1:8080)
pause
