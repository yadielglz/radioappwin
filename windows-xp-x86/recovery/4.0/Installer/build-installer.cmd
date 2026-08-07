@echo off
setlocal
set "ROOT=%~dp0..\..\.."
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
pushd "%ROOT%"
if errorlevel 1 exit /b 1

dotnet tool restore
if errorlevel 1 goto :failed

dotnet publish "%ROOT%\recovery\3.58\recovered-source\RadioApp.csproj" -c Release -r win-x64 --self-contained true -o "%ROOT%\recovery\4.0\publish"
if errorlevel 1 goto :failed

pushd "%~dp0"
if errorlevel 1 goto :failed
dotnet wix build Product.wxs -arch x64 -bindpath . -o "..\RadioApp-4.0.6-Setup.msi"
set "RESULT=%ERRORLEVEL%"
popd
popd
exit /b %RESULT%

:failed
set "RESULT=%ERRORLEVEL%"
popd
exit /b %RESULT%
