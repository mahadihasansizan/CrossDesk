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
    Invitation? invitation;
    SharingService? sharing;
    CancellationTokenSource? pairing;
    bool closing;
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
            CreateButton.IsEnabled = false;
            JoinButton.IsEnabled = false;
            SetStatus("CrossDesk could not start. " + error.Message, true);
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
            "Allow this CrossDesk executable to accept TCP connections from your local subnet, even if Windows changes the network profile?",
            "Allow local connections", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) != MessageBoxResult.Yes) return;
        FirewallButton.IsEnabled = false;
        try
        {
            string path = Environment.ProcessPath ?? throw new IOException("CrossDesk executable path is unavailable.");
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"))
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (string argument in new[] { "advfirewall", "firewall", "add", "rule", "name=CrossDesk local subnet",
                "dir=in", "action=allow", "protocol=TCP", "profile=any", "remoteip=LocalSubnet", $"program={path}" })
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

    async void Create_Click(object sender, RoutedEventArgs e)
    {
        RefreshAdapters();
        if (AdapterBox.SelectedItem is not Lan lan)
        {
            SetStatus("Choose a local network connection first.", true);
            return;
        }
        ResetConnection();
        int attempt = generation;
        var cancellation = new CancellationTokenSource();
        pairing = cancellation;
        try
        {
            var service = PrepareSharing(lan);
            invitation = new Invitation(lan);
            InviteCode.Text = invitation.Code;
            InvitationPanel.Visibility = Visibility.Visible;
            SetPairingBusy(true);
            SetStatus("Waiting for your other PC. Allow CrossDesk on your private network if Windows asks.");
            var connected = await invitation.Accept(
                SideBox.SelectedIndex == 0 ? Side.Left : Side.Right,
                cancellation.Token, service.Endpoint);
            if (attempt != generation) { connected.Dispose(); return; }
            invitation.Dispose();
            invitation = null;
            pairing = null;
            await RunConnection(connected, service, attempt);
        }
        catch (Exception error)
        {
            if (attempt == generation && !closing)
            {
                ResetConnection();
                SetStatus("Could not connect. " + error.Message, true);
            }
        }
        finally { cancellation.Dispose(); }
    }

    async void Join_Click(object sender, RoutedEventArgs e)
    {
        string code = JoinCode.Text.Trim();
        var fields = code.Split(':');
        if (fields.Length != 5 || !IPAddress.TryParse(fields[1], out var address))
        {
            SetStatus("Paste the complete pairing code from the other computer.", true);
            return;
        }
        var lan = Lan.Find().FirstOrDefault(candidate => candidate.Contains(address));
        if (lan is null)
        {
            SetStatus("Both computers must be on the same local network.", true);
            return;
        }
        ResetConnection();
        int attempt = generation;
        var cancellation = new CancellationTokenSource();
        pairing = cancellation;
        try
        {
            SetPairingBusy(true);
            SetStatus("Connecting securely to your other PC…");
            var service = PrepareSharing(lan);
            var connected = await Peer.Join(code, cancellation.Token, service.Endpoint);
            if (attempt != generation) { connected.Dispose(); return; }
            pairing = null;
            await RunConnection(connected, service, attempt);
        }
        catch (Exception error)
        {
            if (attempt == generation && !closing)
            {
                ResetConnection();
                SetStatus("Could not connect. " + error.Message +
                    " Confirm both PCs are still on the same LAN and use Allow local connections on both PCs if Windows Firewall blocks pairing.", true);
            }
        }
        finally { cancellation.Dispose(); }
    }

    async Task RunConnection(Peer connected, SharingService service, int attempt)
    {
        peer = connected;
        service.SetRemote(connected.RemoteSharingEndpoint);
        input!.Attach(connected);
        PairPanel.Visibility = Visibility.Collapsed;
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
            await connected.Run(frame => Dispatcher.InvokeAsync(() =>
            {
                if (peer == connected) input?.Receive(frame);
            }).Task);
        }
        catch (Exception error)
        {
            if (attempt == generation && !closing)
                SetStatus("Connection ended. " + error.Message + " Pair again to reconnect.", true);
        }
        finally
        {
            if (attempt == generation && !closing)
            {
                ResetConnection();
                SetStatus("The connection ended. Pair again to reconnect.", true);
            }
        }
    }

    void ResetConnection()
    {
        generation++;
        input?.Detach();
        pairing?.Cancel();
        pairing = null;
        invitation?.Dispose();
        invitation = null;
        peer?.Dispose();
        peer = null;
        sharing?.Dispose();
        sharing = null;
        if (closing) return;
        SetPairingBusy(false);
        PairPanel.Visibility = Visibility.Visible;
        SharingPanel.Visibility = Visibility.Collapsed;
        ControlPanel.Visibility = Visibility.Collapsed;
        DisconnectButton.Visibility = Visibility.Collapsed;
        InvitationPanel.Visibility = Visibility.Collapsed;
        InviteCode.Clear();
        ConnectionBadge.Text = "Not connected";
        ConnectionBadge.Foreground = (System.Windows.Media.Brush)FindResource("Ink");
        RemoteName.Text = "Your other PC";
        RemoteHint.Text = "Pair to get started";
        SideBox.IsEnabled = true;
        TransferBar.Visibility = Visibility.Collapsed;
        CancelTransferButton.Visibility = Visibility.Collapsed;
        TransferStatus.Text = "Ready to share.";
        UpdateControl();
    }

    void SetPairingBusy(bool busy)
    {
        CreateButton.IsEnabled = !busy;
        JoinButton.IsEnabled = !busy;
        JoinCode.IsEnabled = !busy;
        AdapterBox.IsEnabled = !busy;
        SideBox.IsEnabled = !busy;
        CancelPairButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
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
        ResetConnection();
        SetStatus("Disconnected. Both computers have local control.");
    }
    void CancelPair_Click(object sender, RoutedEventArgs e)
    {
        ResetConnection();
        SetStatus("Pairing cancelled.");
    }
    void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(InviteCode.Text); SetStatus("Pairing code copied. Paste it into CrossDesk on your other PC."); }
        catch { SetStatus("The clipboard is busy. Select and copy the code, or try again.", true); }
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

    void OnClosed(object? sender, EventArgs e)
    {
        updateLifetime.Cancel();
        updateLifetime.Dispose();
        closing = true;
        ResetConnection();
        input?.Dispose();
    }
}
