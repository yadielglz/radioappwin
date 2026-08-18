@echo off
setlocal
set "ROOT=%~dp0.."
set "PROJECT=%ROOT%\recovery\3.58\recovered-source\RadioApp.csproj"
set "OUTPUT=%ROOT%\windows-7-x64\publish"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: .NET SDK is not installed or not in PATH.
  exit /b 1
)

if exist "%OUTPUT%" rmdir /s /q "%OUTPUT%"

dotnet publish "%PROJECT%" ^
  -c Release ^
  -f net6.0-windows7.0 ^
  -r win-x64 ^
  --self-contained true ^
  -p:RuntimeFrameworkVersion=6.0.36 ^
  -o "%OUTPUT%"

if errorlevel 1 exit /b %errorlevel%

echo.
echo Windows 7 x64 build complete:
echo %OUTPUT%
exit /b 0
