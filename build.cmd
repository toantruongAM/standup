@echo off
rem Builds dist\StandUp.exe using the C# compiler that ships with Windows.
setlocal enabledelayedexpansion
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set WPF=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF
set RES=
for %%f in (art\photos\*.png) do set RES=!RES! /resource:%%f,photo.%%~nxf
if not exist dist mkdir dist
"%CSC%" /nologo /optimize+ /target:winexe /out:dist\StandUp.exe /win32manifest:src\app.manifest /win32icon:art\app.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Xaml.dll /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\WindowsBase.dll" !RES! src\*.cs
exit /b %errorlevel%
