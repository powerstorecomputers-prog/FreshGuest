@echo off
REM Schaltet den Gaeste-Modus wieder aus (fragt nach Administrator-Rechten).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0GaestePC.ps1" -Modus Rueckgaengig
