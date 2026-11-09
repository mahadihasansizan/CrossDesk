# CrossDesk

CrossDesk shares a mouse, keyboards, clipboard, and files between two Windows PCs on the same local network. Pair both PCs with a fresh code. Move the shared pointer across the configured screen edge to direct both keyboards to that PC.

## Build

Install the .NET 10 SDK for Windows. From the repository root, run:

```powershell
dotnet publish DeskRelay.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ../standalone
```

The portable app is `../standalone/CrossDesk.exe`. To build Setup, install Inno Setup 6 and run `ISCC.exe /DAppVersion=1.4.0 /O. Setup.iss`. Use the version from the project file when building a new release.

Use the same CrossDesk version on both PCs. Sharing works over the local network while both PCs remain connected and can reach each other, even if internet access is down. If Windows Firewall blocks pairing, select **Allow local connections** on both PCs and approve the Windows administrator prompt. The rule applies to that CrossDesk executable and accepts inbound TCP only from the local subnet across Windows network profiles.

## Updates

Installed copies check for new releases at startup. **Check for updates** also works from the app. Because the repository is private, automatic version checks require GitHub CLI on the Windows PATH, signed in with `gh auth login` using an account with access to this repository. When an update is found, CrossDesk asks before opening the exact Setup download in your browser. Sign in to GitHub in the browser if prompted, then run Setup. Without GitHub CLI, the button can still open the releases page. An unavailable update service does not interrupt local sharing.

See [Architecture](ARCHITECTURE.md) for pairing, input routing, transfers, and verification details.

## Saved connection and startup

On both PCs, enter the same shared code (12–128 characters) and choose **Save and connect**. Keep automatic connection enabled. CrossDesk discovers the other PC on the selected LAN adapter and retries after unexpected disconnects. A deliberate Disconnect pauses retries until Save and connect is used again.

Installed copies start at Windows sign-in by default. The startup checkbox disables that behavior. CrossDesk permits one instance per signed-in user.

Use **Allow local connections** on both PCs to permit TCP sharing and UDP discovery from the local subnet. Broadcast discovery requires a LAN that allows device-to-device traffic. Guest Wi-Fi isolation can prevent discovery and sharing.

Input capture and injection run on a dedicated thread. Received movement backlogs are coalesced while clicks, keystrokes, and focus transitions keep their order. Local tests confirm this behavior and operation while the UI thread is blocked. End-to-end Wi-Fi latency on two physical PCs remains unmeasured.
