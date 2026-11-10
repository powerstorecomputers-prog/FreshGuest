@echo off
REM Baut FreshGuest.exe mit dem C#-Compiler, der bei Windows dabei ist (.NET Framework 4.8).
REM Logo/Icon kommen aus ..\logo       (neu erzeugen: powershell -ExecutionPolicy Bypass -File ..\logo\render.ps1)
REM Anleitung kommt aus ..\FreshGuest-Anleitung.html (neu erzeugen: powershell -ExecutionPolicy Bypass -File ..\anleitung\bauen.ps1)
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "..\logo\FreshGuest.ico" (echo LOGO FEHLT - zuerst logo\render.ps1 ausfuehren & exit /b 1)
set "GUIDE="
if exist "..\FreshGuest-Anleitung.html" set "GUIDE=/resource:..\FreshGuest-Anleitung.html,FreshGuest-Anleitung.html"
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ /out:..\FreshGuest.exe ^
  /win32manifest:app.manifest /win32icon:..\logo\FreshGuest.ico ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationClient.dll" ^
  /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationTypes.dll" ^
  /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll" ^
  /resource:..\GaestePC.ps1,GaestePC.ps1 ^
  /resource:..\logo\FreshGuest-Schriftzug.png,FreshGuest-Schriftzug.png ^
  /resource:..\logo\FreshGuest-448.png,FreshGuest-448.png ^
  %GUIDE% ^
  FreshGuest.cs Gast.cs Anmeldung.cs Kachelbild.cs
if errorlevel 1 (echo BUILD FEHLGESCHLAGEN & exit /b 1)
echo OK: ..\FreshGuest.exe
