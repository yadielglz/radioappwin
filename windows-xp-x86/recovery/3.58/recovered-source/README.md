# RadioApp 4.0.6 recovered source

Version 4.0 includes reliability fixes for server broadcasting and connection
status reporting. The recovery provenance remains version 3.58, as described
below.

This project was reconstructed from `RadioApp-3.58-Setup.msi`, its WiX debug
database, `RadioApp.dll`, and `RadioApp.pdb`. The original installer was not
modified.

## Recovery status

- Recovered 33 C# source files (about 6,900 lines).
- Recovered all seven WPF XAML files from compiled BAML (about 1,000 lines).
- Recovered the application icon and logo.
- Reconstructed the .NET 8 WPF project and its NuGet package references.
- Confirmed that the project builds and publishes for Windows x64.
- Confirmed that all 44 application-defined classes/enums from the original
  assembly are present in the rebuilt assembly. The only observed type-name
  difference is a compiler-generated async state-machine sequence number.

## Build on Windows

Install the .NET 8 SDK, then run:

```powershell
dotnet restore RadioApp.sln
dotnet build RadioApp.sln -c Release
```

To create a self-contained Windows x64 folder:

```powershell
dotnet publish RadioApp.csproj -c Release -r win-x64 --self-contained true
```

## Important limitations

This is recovered, decompiled source rather than the exact lost files. Original
comments, formatting, solution metadata, some local-variable names, and source
control history cannot be recovered from the release binary. The XAML was
reconstructed from BAML and can differ textually from the original while
representing the same compiled UI. Review the nullable-reference warnings before
using warnings-as-errors.

The original installer project (`Installer/Product.wxs`) was not contained as
source in the MSI. Its bound WiX intermediate metadata is preserved separately
under `../wixpdb/` and can be used to reconstruct a new installer later.

See [AUDIT.md](AUDIT.md) for the broadcast-stability fixes, lifecycle fixes,
and remaining follow-up risks found during the first full-code audit.
