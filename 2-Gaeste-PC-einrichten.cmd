@echo off
REM Richtet den Gaeste-PC ein (fragt nach Administrator-Rechten).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0GaestePC.ps1" -Modus Einrichten
