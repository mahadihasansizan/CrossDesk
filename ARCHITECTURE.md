# CrossDesk architecture

CrossDesk runs the same Windows x64 application on both PCs. The PC that creates the invitation is the pairing host for that session. Either PC can move the shared pointer across the configured edge and direct both keyboards to the active screen.

| Component | Responsibility |
| --- | --- |
| MainWindow | WPF interface for pairing, layout, keyboard destination, clipboard settings, and transfer progress |
| Link | Same-subnet pairing, certificate pinning, session authentication, ordered input messages, and connection health |
| InputEngine and Native | Windows input capture, pointer movement, keyboard focus, input injection, and held-input cleanup |
| SharingService and SharingClipboard | Separate encrypted connections for clipboard text, images, and copied files |
| TransferFiles | Transfer approval, streaming, progress, cancellation, SHA-256 verification, and staging |
| Setup.iss | Per-user Windows installation, Start menu shortcut, and confirmation before repair or upgrade |

## Pairing and input

1. The host listens on the selected IPv4 adapter and generates a pairing code containing its address, port, random secret, and certificate fingerprint.
2. The joining PC checks that the host is on its local subnet and validates the certificate fingerprint before sending the pairing secret. The two PCs exchange names, screen arrangement, and sharing endpoints over TLS.
3. Windows hooks capture physical input. Pointer coordinates are normalized before transmission. Consecutive pending movement updates can be replaced by the latest position while clicks, keystrokes, and focus messages retain their order. The input queue is bounded.
4. Crossing an edge sends a focus message so both keyboards follow the pointer. The receiving PC applies remote events through Windows SendInput. Injected events are filtered from capture to prevent feedback.
5. The connection sends idle heartbeats. On disconnect the app releases injected input and returns keyboard routing to local control. Ctrl+Alt+Esc releases control manually.

## Clipboard and files

Clipboard sync is controlled in the app. Clipboard text and images travel over the separate sharing connection. Copying files or folders uses the same transfer path as Send Files, and the receiver must approve the offer. The clipboard receives file paths after the download is available.

File data streams separately from input events. The receiver validates relative paths and checks each file's SHA-256 digest before publishing the transfer. Cancelled or failed transfers clean up their staging directory. The separate connection keeps bulk data out of the input queue. Both connections still share the available Wi-Fi bandwidth.

## Packaging and verification

The portable EXE bundles the .NET desktop runtime. Setup installs the same executable and shows the installed version before a repair or upgrade. Use the same version on both PCs.

Pairing and transfer connections use numeric LAN addresses. Outbound connections bind to the selected adapter. Both TLS clients disable online certificate downloads and validate the pinned certificate from the pairing exchange. The optional firewall action permits inbound TCP for the running executable from the local subnet across Windows network profiles.

Update checks run separately from the local connection. The app uses the signed-in GitHub CLI account to look up the latest private release and compares its version tag with the running assembly version. With user confirmation it opens the exact Setup asset in the browser. Setup handles the installation prompt.

Local checks covered certificate-pinned pairing, rejection of an incorrect secret, bidirectional input message ordering, movement coalescing, disconnect handling, and an approved 8 MiB file transfer. Focus message ordering was checked on one PC. Physical two-PC keyboard handoff and Wi-Fi latency remain unverified. Windows can restrict input injection into elevated applications and secure desktops.

## Version 1.4 automatic connections and latency

The saved-code mode discovers peers through UDP port 45873. Persistent device IDs select complementary connection roles. Both peers prove possession of a PBKDF2-derived shared key using HMAC over fresh nonces, both device IDs, and the server certificate fingerprint. The saved code is protected with Windows DPAPI for the current user. Network loss causes another discovery and authentication attempt.

Hooks and remote injection run on a dedicated STA message loop. UI updates are posted asynchronously. The receiver reads frame batches and merges consecutive pointer positions without crossing click, key, or focus boundaries. This prevents queued obsolete positions from being replayed after temporary congestion. TCP NoDelay remains enabled.
