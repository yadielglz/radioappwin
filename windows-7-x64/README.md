# RadioApp 4.0.6 - Windows 7 x64 Compatibility Build

This folder contains the Windows 7 SP1 x64 build path for RadioApp 4.0.6.

## Target

- Windows 7 SP1, 64-bit
- .NET 6.0.36 self-contained runtime
- Target framework: `net6.0-windows7.0`
- Application version remains `4.0.6`

## Build

Run `build-win7.cmd` from a Windows development machine with a recent .NET SDK installed.

The published files are written to:

`windows-7-x64\publish`

## Windows 7 prerequisites

The self-contained build does not require a separately installed .NET runtime, but Windows 7 SP1 should have the Microsoft Visual C++ 2015-2019 Redistributable and KB3063858 installed.

## Web Studio compatibility

RadioApp's native HTTP API can run on Windows 7. Microsoft's built-in server-side WebSocket implementation requires Windows 8 or newer, so the Windows 7 web client must use HTTP polling as its live-state fallback.

## Support status

Windows 7 and .NET 6 are both out of Microsoft support. This target is maintained only for legacy RadioApp compatibility.
