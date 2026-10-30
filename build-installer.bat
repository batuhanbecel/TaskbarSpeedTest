@echo off
rem Builds dist\TaskbarSpeedTest-Setup-<version>.exe (needs Inno Setup 6).
setlocal
set ROOT=%~dp0

rem --skip-exe: reuse an already built (e.g. signed) TaskbarSpeedTest.exe.
if not "%~1"=="--skip-exe" (call "%ROOT%build.bat" || exit /b 1)

set ISCC=
for %%P in ("%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" "%ProgramFiles%\Inno Setup 6\ISCC.exe" "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe") do if exist %%P set ISCC=%%P
if not defined ISCC (
  echo Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup
  exit /b 1
)
%ISCC% /Q "%ROOT%installer\TaskbarSpeedTest.iss"
exit /b %errorlevel%
