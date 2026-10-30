@echo off
rem Builds TaskbarSpeedTest.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
setlocal
set ROOT=%~dp0

"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 ^
  /win32manifest:"%ROOT%app.manifest" /win32icon:"%ROOT%assets\app.ico" /out:"%ROOT%TaskbarSpeedTest.exe" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll ^
  "%ROOT%src\*.cs"
exit /b %errorlevel%
