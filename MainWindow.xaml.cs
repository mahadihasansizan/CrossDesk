using System.IO;
using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace DeskRelay;

public partial class MainWindow : Window
{
    InputEngine? input;
    Peer? peer;
    SharingService? sharing;
    bool closing;
    bool settingsReady;
    AutoConnectSettings? autoSettings;
    CancellationTokenSource? automatic;
    readonly CancellationTokenSource updateLifetime = new();
    static Version CurrentVersion => typeof(MainWindow).Assembly.GetName().Version!;
    int generation;
    readonly string receiveRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Desk Relay");

    public MainWindow()
    {
        InitializeComponent();
        var version = typeof(MainWindow).Assembly.GetName().Version!;
        Title = $"CrossDesk {version.ToString(3)}";
        LocalName.Text = Environment.MachineName;
        Loaded += OnLoaded;
        Closed += OnClosed;
        KeyboardOnly.Checked += KeyboardMode_Changed;
        KeyboardOnly.Unchecked += KeyboardMode_Changed;
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            RefreshAdapters();
            input = new InputEngine();
            input.StateChanged += UpdateControl;
            if (AdapterBox.Items.Count == 0)
                SetStatus("Connect to Wi-Fi or Ethernet, then reopen CrossDesk.", true);
        }
        catch (Exception error)
        {
            SaveAutoButton.IsEnabled = false;
            SetStatus("CrossDesk could not start. " + error.Message, true);
        }
        try
        {
            autoSettings = AutoConnectSettings.Load();
            SharedCodeBox.Text = autoSettings.SharedCode;
            AutoConnectToggle.IsChecked = autoSettings.Enabled;
            StartupToggle.IsChecked = StartupSettings.Load();
            settingsReady = true;
            if (autoSettings.Enabled && autoSettings.SharedCode.Length >= 12 && input is not null)
                StartAutomatic();
        }
        catch (Exception error)
        {
            settingsReady = true;
            AutoStatus.Text = "Settings could not be loaded. " + error.Message;
        }
        using var install = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\DeskRelay_is1");
        if (install?.GetValue("InstallLocation") is string location &&
            string.Equals(Environment.ProcessPath, Path.Combine(location, "CrossDesk.exe"), StringComparison.OrdinalIgnoreCase))
            _ = CheckForUpdateAsync(manual: false);
    }

    void AdapterBox_Opened(object sender, EventArgs e) => RefreshAdapters();

    void RefreshAdapters()
    {
        var previous = (AdapterBox.SelectedItem as Lan)?.Address;
        var adapters = Lan.Find();
        AdapterBox.ItemsSource = adapters;
        AdapterBox.SelectedItem = adapters.FirstOrDefault(adapter => adapter.Address.Equals(previous))
            ?? adapters.FirstOrDefault();
    }

    async void Update_Click(object sender, RoutedEventArgs e) => await CheckForUpdateAsync(manual: true);

    async Task CheckForUpdateAsync(bool manual)
    {
        UpdateButton.IsEnabled = false;
        try
        {
            var update = await CrossDeskUpdate.CheckAsync(CurrentVersion, updateLifetime.Token);
            if (closing) return;
            if (update is null)
            {
                if (manual) MessageBox.Show(this, "You have the latest CrossDesk release.", "CrossDesk updates");
                return;
            }
            if (MessageBox.Show(this,
                $"CrossDesk {update.Version} is available. Open GitHub to download Setup?\n\nSign in with an account that can access the private repository.",
                "CrossDesk update available", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) == MessageBoxResult.Yes)
                CrossDeskUpdate.Open(update.DownloadUrl);
        }
        catch (Exception)
        {
            if (manual && !closing && MessageBox.Show(this,
                "The update check could not reach GitHub or access the private repository. Automatic checks need GitHub CLI signed in with repository access.\n\nOpen GitHub releases in your browser?",
                "CrossDesk updates", MessageBoxButton.YesNo, MessageBoxImage.Information,
                MessageBoxResult.No) == MessageBoxResult.Yes)
            {
                try { CrossDeskUpdate.Open(CrossDeskUpdate.ReleasesUrl); }
                catch (Exception) { MessageBox.Show(this, CrossDeskUpdate.ReleasesUrl, "Open this address in your browser"); }
            }
        }
        finally { UpdateButton.IsEnabled = true; }
    }

    async void Firewall_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
            "Allow CrossDesk input and file connections (TCP) and automatic device discovery (UDP) from your local subnet, even if Windows changes the network profile?",
            "Allow local connections", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) != MessageBoxResult.Yes) return;
        FirewallButton.IsEnabled = false;
        try
        {
            string path = Environment.ProcessPath ?? throw new IOException("CrossDesk executable path is unavailable.");
            string quotedPath = "'" + path.Replace("'", "''") + "'";
            string script = "$app = " + quotedPath + "; foreach ($protocol in @('TCP','UDP')) { " +
                "& netsh.exe advfirewall firewall add rule ('name=CrossDesk local subnet ' + $protocol) " +
                "dir=in action=allow ('protocol=' + $protocol) profile=any remoteip=LocalSubnet ('program=' + $app); " +
                "if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } }; exit 0";
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) })
                info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new IOException("Windows Firewall could not open.");
            await process.WaitForExitAsync();
            if (!closing)
                MessageBox.Show(this, process.ExitCode == 0 ? "Local connections are allowed. Pair again using your Wi-Fi or Ethernet address."
                    : "Windows did not add the firewall rule. Check with your device administrator.", "CrossDesk local connections");
        }
        catch (Exception error)
        {
            if (!closing) MessageBox.Show(this, error.Message, "CrossDesk local connections");
        }
        finally { FirewallButton.IsEnabled = true; }
    }

    SharingService PrepareSharing(Lan lan)
    {
        var service = new SharingService(Dispatcher, lan.Address, receiveRoot);
        service.ConfirmReceive = offer =>
        {
            input?.ReturnLocal();
            int files = offer.Entries.Count(entry => !entry.Directory);
            var result = MessageBox.Show(this,
                $"{peer?.Name ?? "The connected computer"} wants to send {files} file(s) " +
                $"({FormatBytes(offer.Bytes)}).\n\nSave in {receiveRoot}?",
                "Receive files", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No);
            return Task.FromResult(result == MessageBoxResult.Yes);
        };
        service.StatusChanged += message =>
        {
            if (sharing == service) TransferStatus.Text = message;
        };
        service.ProgressChanged += progress =>
        {
            if (sharing != service) return;
            TransferStatus.Text = $"{progress.Name} · {FormatBytes(progress.Completed)} / {FormatBytes(progress.Total)} · {progress.State}";
            TransferBar.Value = progress.Total > 0 ? 100d * progress.Completed / progress.Total : 0;
            TransferBar.Visibility = Visibility.Visible;
            CancelTransferButton.Visibility = progress.Completed < progress.Total
                ? Visibility.Visible : Visibility.Collapsed;
        };
        sharing = service;
        return service;
    }

    async Task RunConnection(Peer connected, SharingService service, int attempt)
    {
        peer = connected;
        service.SetRemote(connected.RemoteSharingEndpoint);
        input!.Attach(connected);
        SharingPanel.Visibility = Visibility.Visible;
        ControlPanel.Visibility = Visibility.Visible;
        DisconnectButton.Visibility = Visibility.Visible;
        ConnectionBadge.Text = "Connected";
        ConnectionBadge.Foreground = (System.Windows.Media.Brush)FindResource("Success");
        RemoteName.Text = connected.Name;
        RemoteHint.Text = "Connected securely";
        SideBox.SelectedIndex = connected.Side == Side.Left ? 0 : 1;
        SideBox.IsEnabled = false;
        if (ClipboardToggle.IsChecked == true) service.StartClipboard();
        UpdateControl();
        SetStatus("Connected over your local network. Move across the shared edge to switch computers.");
        try
        {
            await connected.Run(frame => input!.ReceiveAsync(connected, frame));
        }
        catch (Exception error)
        {
            if (attempt == generation && !closing)
                SetStatus("Connection ended. " + error.Message + " Automatic connection will retry with your saved code.", true);
        }
        finally
        {
            if (attempt == generation && !closing)
            {
                ResetConnection();
                SetStatus("The connection ended. Automatic connection will retry with your saved code.", true);
            }
        }
    }

    void ResetConnection()
    {
        generation++;
        input?.Detach();
        peer?.Dispose();
        peer = null;
        sharing?.Dispose();
        sharing = null;
        if (closing) return;
        SetPairingBusy(false);
        SharingPanel.Visibility = Visibility.Collapsed;
        ControlPanel.Visibility = Visibility.Collapsed;
        DisconnectButton.Visibility = Visibility.Collapsed;
        ConnectionBadge.Text = "Not connected";
        ConnectionBadge.Foreground = (System.Windows.Media.Brush)FindResource("Ink");
        RemoteName.Text = "Your other PC";
        RemoteHint.Text = "Use the same shared code";
        SideBox.IsEnabled = true;
        TransferBar.Visibility = Visibility.Collapsed;
        CancelTransferButton.Visibility = Visibility.Collapsed;
        TransferStatus.Text = "Ready to share.";
        UpdateControl();
    }

    void SetPairingBusy(bool busy)
    {
        AdapterBox.IsEnabled = !busy;
        SideBox.IsEnabled = !busy;
    }

    void UpdateControl()
    {
        ControlStatus.Text = peer is null
            ? "Pair both computers to share your mouse and keyboard."
            : input?.KeyboardFollowsPeer switch
            {
                true => $"Both keyboards type on {peer.Name}.",
                false => "Both keyboards type on this PC.",
                null => "Move the mouse to choose where both keyboards type."
            };
    }

    void Side_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LocalDevice is null || RemoteDevice is null) return;
        bool left = SideBox.SelectedIndex == 0;
        Grid.SetColumn(LocalDevice, left ? 2 : 0);
        Grid.SetColumn(RemoteDevice, left ? 0 : 2);
    }

    void Switch_Click(object sender, RoutedEventArgs e) => input?.Toggle(KeyboardOnly.IsChecked == true);
    void KeyboardMode_Changed(object sender, RoutedEventArgs e)
    {
        if (input is not null) input.KeyboardOnly = KeyboardOnly.IsChecked == true;
    }
    void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (autoSettings is { Enabled: true, SharedCode.Length: >= 12 })
        {
            StartAutomatic();
            SetStatus("Reconnecting with your saved code…");
            return;
        }
        StopAutomatic();
        ResetConnection();
        SetStatus("Disconnected. Both computers have local control.");
    }
    void Clipboard_Changed(object sender, RoutedEventArgs e)
    {
        if (sharing is null) return;
        if (ClipboardToggle.IsChecked == true) sharing.StartClipboard();
        else sharing.StopClipboard();
    }

    async void SendFiles_Click(object sender, RoutedEventArgs e)
    {
        input?.ReturnLocal();
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Choose files to send" };
        if (dialog.ShowDialog(this) == true) await SendPaths(dialog.FileNames);
    }

    async void SendFolder_Click(object sender, RoutedEventArgs e)
    {
        input?.ReturnLocal();
        var dialog = new OpenFolderDialog { Title = "Choose a folder to send" };
        if (dialog.ShowDialog(this) == true) await SendPaths([dialog.FolderName]);
    }

    async Task SendPaths(string[] paths)
    {
        var service = sharing;
        if (service is null) return;
        SendFilesButton.IsEnabled = SendFolderButton.IsEnabled = false;
        CancelTransferButton.Visibility = Visibility.Visible;
        TransferStatus.Text = "Preparing files…";
        try { await service.SendFilesAsync(paths); }
        catch (OperationCanceledException) { if (sharing == service) TransferStatus.Text = "Transfer cancelled."; }
        catch (Exception error) { if (sharing == service) TransferStatus.Text = "Transfer failed. " + error.Message; }
        finally
        {
            SendFilesButton.IsEnabled = SendFolderButton.IsEnabled = true;
            if (sharing == service) CancelTransferButton.Visibility = Visibility.Collapsed;
        }
    }

    void CancelTransfer_Click(object sender, RoutedEventArgs e) => sharing?.CancelAll();

    void SetStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(error ? "Error" : "Muted");
    }

    static string FormatBytes(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.0} MB"
        : bytes >= 1L << 10 ? $"{bytes / (double)(1L << 10):0.0} KB" : $"{bytes} bytes";

    void SaveAuto_Click(object sender, RoutedEventArgs e)
    {
        if (SharedCodeBox.Text.Trim().Length is < 12 or > 128)
        {
            AutoStatus.Text = "Choose a shared code between 12 and 128 characters, then enter it on both PCs.";
            SharedCodeBox.Focus();
            return;
        }
        try
        {
            var saved = new AutoConnectSettings
            {
                DeviceId = autoSettings?.DeviceId ?? Guid.NewGuid(),
                SharedCode = SharedCodeBox.Text.Trim(),
                Enabled = true
            };
            saved.Save();
            autoSettings = saved;
            settingsReady = false;
            AutoConnectToggle.IsChecked = true;
            settingsReady = true;
            StartAutomatic();
        }
        catch (Exception error) { AutoStatus.Text = "Could not save connection settings. " + error.Message; }
    }

    void AutoConnect_Changed(object sender, RoutedEventArgs e)
    {
        if (!settingsReady) return;
        try
        {
            autoSettings ??= AutoConnectSettings.Load();
            bool enabled = AutoConnectToggle.IsChecked == true;
            if (enabled && autoSettings.SharedCode.Length < 12)
            {
                settingsReady = false;
                AutoConnectToggle.IsChecked = false;
                settingsReady = true;
                AutoStatus.Text = "Enter the same shared code on both PCs and choose Save and connect.";
                return;
            }
            autoSettings.Enabled = enabled;
            autoSettings.Save();
            if (enabled) StartAutomatic();
            else
            {
                StopAutomatic();
                ResetConnection();
                AutoStatus.Text = "Automatic connection is off.";
            }
        }
        catch (Exception error) { AutoStatus.Text = "Could not change automatic connection. " + error.Message; }
    }

    void GenerateCode_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Replace the saved code? Enter the new code on your other PC too.",
            "Generate new code", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SharedCodeBox.Text = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        SaveAuto_Click(sender, e);
    }

    void RemoveCode_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Remove the saved code from this PC and stop automatic connections?",
            "Remove saved code", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            var settings = new AutoConnectSettings { DeviceId = autoSettings?.DeviceId ?? Guid.NewGuid() };
            settings.Save();
            autoSettings = settings;
            settingsReady = false;
            AutoConnectToggle.IsChecked = false;
            SharedCodeBox.Clear();
            settingsReady = true;
            StopAutomatic();
            ResetConnection();
            AutoStatus.Text = "Saved code removed from this PC.";
        }
        catch (Exception error) { AutoStatus.Text = "Could not remove the code. " + error.Message; }
    }

    void CopySavedCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (autoSettings?.SharedCode.Length >= 12)
            {
                Clipboard.SetText(autoSettings.SharedCode);
                AutoStatus.Text = "Saved code copied. Enter it on your other PC.";
            }
        }
        catch { AutoStatus.Text = "The clipboard is busy. Try copying again."; }
    }

    void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (!settingsReady) return;
        try { StartupSettings.SetEnabled(StartupToggle.IsChecked == true); }
        catch (Exception error)
        {
            settingsReady = false;
            StartupToggle.IsChecked = !StartupToggle.IsChecked;
            settingsReady = true;
            AutoStatus.Text = "Windows startup could not be changed. " + error.Message;
        }
    }

    void StartAutomatic()
    {
        StopAutomatic();
        ResetConnection();
        if (closing || input is null || autoSettings is not { Enabled: true } ||
            autoSettings.SharedCode.Length < 12) return;
        var cancellation = new CancellationTokenSource();
        automatic = cancellation;
        _ = AutomaticLoop(cancellation, autoSettings.SharedCode);
    }

    void StopAutomatic()
    {
        automatic?.Cancel();
        automatic = null;
        if (!closing && settingsReady)
            AutoStatus.Text = "Automatic connection is paused. Choose Save and connect to resume.";
    }

    async Task AutomaticLoop(CancellationTokenSource cancellation, string code)
    {
        var token = cancellation.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                int attempt = generation;
                try
                {
                RefreshAdapters();
                if (AdapterBox.SelectedItem is not Lan lan)
                {
                    AutoStatus.Text = "Waiting for Wi-Fi or Ethernet…";
                    await Task.Delay(2000, token);
                    continue;
                }
                ResetConnection();
                attempt = generation;
                    var service = PrepareSharing(lan);
                    AutoStatus.Text = "Looking for your other PC with the same shared code…";
                    var connected = await AutoConnect.ConnectAsync(lan, code,
                        SideBox.SelectedIndex == 0 ? Side.Left : Side.Right, service.Endpoint, token);
                    if (token.IsCancellationRequested || attempt != generation)
                    {
                        connected.Dispose();
                        break;
                    }
                    AutoStatus.Text = "Connected automatically. CrossDesk will reconnect if the connection drops.";
                    await RunConnection(connected, service, attempt);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    if (token.IsCancellationRequested) break;
                    if (attempt == generation) ResetConnection();
                    AutoStatus.Text = "Waiting to reconnect. " + error.Message;
                }
                await Task.Delay(2000, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (automatic == cancellation) automatic = null;
            cancellation.Dispose();
        }
    }

    void OnClosed(object? sender, EventArgs e)
    {
        updateLifetime.Cancel();
        updateLifetime.Dispose();
        closing = true;
        StopAutomatic();
        ResetConnection();
        input?.Dispose();
    }
}
