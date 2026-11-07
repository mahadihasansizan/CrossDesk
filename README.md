# CrossDesk

CrossDesk shares a mouse, keyboards, clipboard, and files between two Windows PCs on the same local network. Pair both PCs with a fresh code. Move the shared pointer across the configured screen edge to direct both keyboards to that PC.

## Build

Install the .NET 10 SDK for Windows. From the repository root, run:

```powershell
dotnet publish DeskRelay.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ../standalone
```

The portable app is `../standalone/CrossDesk.exe`. To build Setup, install Inno Setup 6 and run `ISCC.exe /DAppVersion=1.2.0 /O. Setup.iss`. Use the version from the project file when building a new release.

Use the same CrossDesk version on both PCs. Windows may ask you to allow CrossDesk through the firewall on the private network.
