# CrossDesk

CrossDesk shares a mouse, keyboards, clipboard, and files between two Windows PCs on the same local network. Enter the same saved shared code on both PCs and choose Save and connect. Move the shared pointer across the configured screen edge to direct both keyboards to that PC.

## Build

Install the .NET 10 SDK for Windows. From the repository root, run:

```powershell
dotnet publish DeskRelay.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ../standalone
```

The portable app is `../standalone/CrossDesk.exe`. To build Setup, install Inno Setup 6 and run `ISCC.exe /DAppVersion=1.5.0 /O. Setup.iss`. Use the version from the project file when building a new release.

Use the same CrossDesk version on both PCs. Sharing works over the local network while both PCs remain connected and can reach each other, even if internet access is down. If Windows Firewall blocks pairing, select **Allow LAN access** on both PCs and approve the Windows administrator prompt. The rule applies to that CrossDesk executable and accepts inbound TCP only from the local subnet across Windows network profiles.

## Updates

Installed copies check for new releases at startup. **Check for updates** also works from the app. Because the repository is private, automatic version checks require GitHub CLI on the Windows PATH, signed in with `gh auth login` using an account with access to this repository. When an update is found, CrossDesk asks before opening the exact Setup download in your browser. Sign in to GitHub in the browser if prompted, then run Setup. Without GitHub CLI, the button can still open the releases page. An unavailable update service does not interrupt local sharing.

See [Architecture](ARCHITECTURE.md) for pairing, input routing, transfers, and verification details.

## Saved connection and startup

On both PCs, enter the same shared code (12–128 characters) and choose **Save and connect**. Keep automatic connection enabled. CrossDesk discovers the other PC on the selected LAN adapter and retries after unexpected disconnects. With automatic connection enabled, Disconnect immediately starts reconnection using the saved code. Turn off automatic connection to pause retries. The code remains saved until you explicitly replace or remove it. Generate new code and Remove saved code each ask for confirmation.

Installed copies start at Windows sign-in by default. The startup checkbox disables that behavior. CrossDesk permits one instance per signed-in user.

Use **Allow LAN access** on both PCs to permit TCP sharing and UDP discovery from the local subnet. Broadcast discovery requires a LAN that allows device-to-device traffic. Guest Wi-Fi isolation can prevent discovery and sharing.

Input capture and injection run on a dedicated thread. Received movement backlogs are coalesced while clicks, keystrokes, and focus transitions keep their order. Local tests confirm this behavior and operation while the UI thread is blocked. End-to-end Wi-Fi latency on two physical PCs remains unmeasured.

The connection screen has one visible, selectable shared-code field. Generate a code on one PC, copy it to the other, and save it on both. The code persists across restarts. Reconnect uses the saved code, and editing the field takes effect only after Save and connect.
