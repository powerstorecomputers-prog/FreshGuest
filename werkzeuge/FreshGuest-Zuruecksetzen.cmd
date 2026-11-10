@echo off
REM FreshGuest - alles zuruecksetzen (auch Reste alter Versionen).
REM Rechtsklick -> Als Administrator ausfuehren.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0FreshGuest-Zuruecksetzen.ps1"
pause
