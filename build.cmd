@echo off
rem ── PHOSPHOR build: produces dist\Phosphor.exe (single file, needs only .NET Framework 4.8 built into Windows 10/11)
setlocal
cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
  echo [!] .NET SDK not found. Install it: winget install Microsoft.DotNet.SDK.8
  exit /b 1
)

rem optional first argument: version, e.g. build.cmd 1.2.0
set "VER_ARG="
if not "%~1"=="" set "VER_ARG=-p:Version=%~1"

dotnet build src\Phosphor.csproj -c Release -o build\bin --nologo -v minimal %VER_ARG%
if errorlevel 1 exit /b 1

if not exist dist mkdir dist
copy /y build\bin\Phosphor.exe dist\Phosphor.exe >nul
echo.
echo [OK] dist\Phosphor.exe
endlocal
