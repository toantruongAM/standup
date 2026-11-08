@echo off
rem Wraps dist\StandUp.exe into release\StandUp-Setup.exe. Run build.cmd first.
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist dist\StandUp.exe (
    echo dist\StandUp.exe is missing. Run build.cmd first.
    exit /b 1
)
if not exist release mkdir release
"%CSC%" /nologo /optimize+ /target:winexe /out:release\StandUp-Setup.exe /win32manifest:src\app.manifest /win32icon:art\app.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:Microsoft.CSharp.dll /resource:dist\StandUp.exe,payload.exe installer\Setup.cs
exit /b %errorlevel%
