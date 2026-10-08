@echo off
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /win32icon:app.ico /resource:app.ico,MonitorControl.app.ico /out:MonitorControl.exe MonitorControl.cs
