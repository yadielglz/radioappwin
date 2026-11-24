@echo off
setlocal
set "VERSION=26.1124.1000"
set "ROOT=%~dp0.."
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"

pushd "%ROOT%"
if errorlevel 1 exit /b 1

echo Running RadioApp.Tests...
dotnet test RadioApp.Tests\RadioApp.Tests.csproj -c Release
if errorlevel 1 goto :failed

echo Restoring WiX tool...
dotnet tool restore
if errorlevel 1 goto :failed

if exist "%ROOT%\publish" rmdir /s /q "%ROOT%\publish"

echo Publishing RadioApp Studio %VERSION% (win-x64 self-contained)...
dotnet publish RadioApp\RadioApp.csproj -c Release -r win-x64 --self-contained true -o "%ROOT%\publish"
if errorlevel 1 goto :failed

pushd "%~dp0"
echo Building MSI installer...
dotnet wix build Product.wxs -arch x64 -bindpath . -o "..\RadioApp-%VERSION%-Win64-Setup.msi"
set "RESULT=%ERRORLEVEL%"
popd
popd
exit /b %RESULT%

:failed
set "RESULT=%ERRORLEVEL%"
popd
exit /b %RESULT%
