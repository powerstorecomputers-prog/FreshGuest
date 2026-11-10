@echo off
REM Zeigt nur den Status an, aendert nichts.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0GaestePC.ps1" -Modus Status
