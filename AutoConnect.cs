using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace DeskRelay;

static class AutoConnect
{
    const int DiscoveryPort = 45873;
    record Beacon(string Protocol, Guid Device, int Port, string Fingerprint);

    public static Task<Peer> ConnectAsync(Lan lan, string sharedCode, Side side, string sharingEndpoint, CancellationToken token)
        => ConnectCore(lan, sharedCode, side, sharingEndpoint, AutoConnectSettings.Load().DeviceId, DiscoveryPort, token);

    internal static async Task<Peer> ConnectCore(Lan lan, string sharedCode, Side side, string sharingEndpoint,
        Guid device, int discoveryPort, CancellationToken token)
    {
        sharedCode = sharedCode.Trim();
        if (sharedCode.Length is < 12 or > 128) throw new ArgumentException("Use a shared code between 12 and 128 characters.");
        byte[] passwordKey = await Task.Run(() => Rfc2898DeriveBytes.Pbkdf2(sharedCode, "CrossDesk auto-connect v1"u8, 600_000, HashAlgorithmName.SHA256, 32), token).ConfigureAwait(false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=CrossDesk automatic connection", signingKey, HashAlgorithmName.SHA256);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
        var listener = new TcpListener(lan.Address, 0);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        listener.Start(8);
        var connected = new TaskCompletionSource<Peer>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? advertise = null;
        Task? accept = null;
        Task? discover = null;
        try
        {
            udp.Client.ExclusiveAddressUse = false;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
            udp.EnableBroadcast = true;
            var beacon = new Beacon("CrossDesk-A1", device, ((IPEndPoint)listener.LocalEndpoint).Port,
                certificate.GetCertHashString(HashAlgorithmName.SHA256));
            var announcement = JsonSerializer.SerializeToUtf8Bytes(beacon);
            var address = lan.Address.GetAddressBytes();
            var mask = lan.Mask.GetAddressBytes();
            var broadcastBytes = new byte[4];
            for (int i = 0; i < 4; i++) broadcastBytes[i] = (byte)(address[i] | ~mask[i]);
            var broadcast = new IPEndPoint(new IPAddress(broadcastBytes), discoveryPort);
            advertise = Advertise();
            accept = Accept();
            discover = Discover();
            return await connected.Task.WaitAsync(token).ConfigureAwait(false);

            void Publish(Peer peer)
            {
                if (!connected.TrySetResult(peer)) peer.Dispose();
            }

            async Task Advertise()
            {
                try
                {
                    while (!lifetime.IsCancellationRequested)
                    {
                        await udp.SendAsync(announcement, broadcast, lifetime.Token).ConfigureAwait(false);
                        await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token).ConfigureAwait(false);
                        if (!IPAddress.IsLoopback(lan.Address) && !Lan.Find().Any(l => l.Address.Equals(lan.Address)))
                            throw new IOException("The network address changed. Reconnecting.");
                    }
                }
                catch (Exception e) { if (!lifetime.IsCancellationRequested) connected.TrySetException(e); }
            }

            async Task Accept()
            {
                try
                {
                    while (!lifetime.IsCancellationRequested)
                    {
                        var socket = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
                        if (!lan.Contains(((IPEndPoint)socket.Client.RemoteEndPoint!).Address)) { socket.Dispose(); continue; }
                        try
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                            timeout.CancelAfter(TimeSpan.FromSeconds(5));
                            Publish(await AuthenticateServer(socket, certificate, passwordKey, device, side, sharingEndpoint, timeout.Token).ConfigureAwait(false));
                        }
                        catch (Exception e) when (e is IOException or AuthenticationException or OperationCanceledException or SocketException)
                        {
                            socket.Dispose();
                            await Task.Delay(300, lifetime.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception e) { if (!lifetime.IsCancellationRequested) connected.TrySetException(e); }
            }

            async Task Discover()
            {
                var nextTry = new Dictionary<Guid, long>();
                try
                {
                    while (!lifetime.IsCancellationRequested)
                    {
                        var packet = await udp.ReceiveAsync(lifetime.Token).ConfigureAwait(false);
                        if (packet.Buffer.Length > 512 || !lan.Contains(packet.RemoteEndPoint.Address)) continue;
                        Beacon? candidate;
                        try { candidate = JsonSerializer.Deserialize<Beacon>(packet.Buffer); }
                        catch (JsonException) { continue; }
                        if (candidate == null || candidate.Protocol != "CrossDesk-A1" || candidate.Device.CompareTo(device) <= 0 || candidate.Port is < 1 or > 65535 || candidate.Fingerprint?.Length != 64) continue;
                        if (nextTry.TryGetValue(candidate.Device, out long due) && Environment.TickCount64 < due) continue;
                        if (nextTry.Count > 128) nextTry.Clear();
                        nextTry[candidate.Device] = Environment.TickCount64 + 5000;
                        var socket = new TcpClient(new IPEndPoint(lan.Address, 0));
                        try
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                            timeout.CancelAfter(TimeSpan.FromSeconds(5));
                            await socket.ConnectAsync(packet.RemoteEndPoint.Address, candidate.Port, timeout.Token).ConfigureAwait(false);
                            Publish(await AuthenticateClient(socket, candidate.Fingerprint, passwordKey, device, candidate.Device, sharingEndpoint, timeout.Token).ConfigureAwait(false));
                        }
                        catch (Exception e) when (e is IOException or AuthenticationException or OperationCanceledException or SocketException or FormatException)
                        { socket.Dispose(); }
                    }
                }
                catch (Exception e) { if (!lifetime.IsCancellationRequested) connected.TrySetException(e); }
            }
        }
        finally
        {
            lifetime.Cancel();
            listener.Stop();
            udp.Dispose();
            await Task.WhenAll(new[] { advertise, accept, discover }.Where(t => t != null).Select(t => t!)).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(passwordKey);
        }
    }

    internal static async Task<Peer> AuthenticateServer(TcpClient socket, X509Certificate2 certificate, byte[] key,
        Guid serverId, Side side, string sharingEndpoint, CancellationToken token)
    {
        var tls = new SslStream(socket.GetStream());
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, token).ConfigureAwait(false);
            var hello = new byte[48];
            await tls.ReadExactlyAsync(hello, token).ConfigureAwait(false);
            var clientId = new Guid(hello.AsSpan(0, 16));
            if (clientId.CompareTo(serverId) >= 0) throw new AuthenticationException("Connection role was rejected.");
            var nonce = RandomNumberGenerator.GetBytes(32);
            var transcript = BuildTranscript(clientId, serverId, hello.AsSpan(16), nonce, certificate.GetCertHash(HashAlgorithmName.SHA256));
            await tls.WriteAsync(nonce, token).ConfigureAwait(false);
            await tls.WriteAsync(Proof(key, transcript, 1), token).ConfigureAwait(false);
            var proof = new byte[32];
            await tls.ReadExactlyAsync(proof, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(proof, Proof(key, transcript, 2))) throw new AuthenticationException("Connection code did not match.");
            var name = await Peer.ReadName(tls, token).ConfigureAwait(false);
            var remoteSharing = await Peer.ReadEndpoint(tls, token).ConfigureAwait(false);
            await tls.WriteAsync(new[] { (byte)side }, token).ConfigureAwait(false);
            await Peer.WriteName(tls, token).ConfigureAwait(false);
            await Peer.WriteEndpoint(tls, sharingEndpoint, token).ConfigureAwait(false);
            return new Peer(socket, tls, name, side) { IsHost = true, RemoteSharingEndpoint = remoteSharing };
        }
        catch { tls.Dispose(); throw; }
    }

    internal static async Task<Peer> AuthenticateClient(TcpClient socket, string fingerprint, byte[] key,
        Guid clientId, Guid serverId, string sharingEndpoint, CancellationToken token)
    {
        var pin = Convert.FromHexString(fingerprint);
        var tls = new SslStream(socket.GetStream(), false, (_, certificate, _, _) => certificate != null &&
            CryptographicOperations.FixedTimeEquals(pin, certificate.GetCertHash(HashAlgorithmName.SHA256)));
        try
        {
            await tls.AuthenticateAsClientAsync(LanTls.ClientOptions("CrossDesk automatic connection"), token).ConfigureAwait(false);
            var nonce = RandomNumberGenerator.GetBytes(32);
            await tls.WriteAsync(clientId.ToByteArray(), token).ConfigureAwait(false);
            await tls.WriteAsync(nonce, token).ConfigureAwait(false);
            var serverNonce = new byte[32];
            var serverProof = new byte[32];
            await tls.ReadExactlyAsync(serverNonce, token).ConfigureAwait(false);
            await tls.ReadExactlyAsync(serverProof, token).ConfigureAwait(false);
            var transcript = BuildTranscript(clientId, serverId, nonce, serverNonce, pin);
            if (!CryptographicOperations.FixedTimeEquals(serverProof, Proof(key, transcript, 1))) throw new AuthenticationException("Connection code did not match.");
            await tls.WriteAsync(Proof(key, transcript, 2), token).ConfigureAwait(false);
            await Peer.WriteName(tls, token).ConfigureAwait(false);
            await Peer.WriteEndpoint(tls, sharingEndpoint, token).ConfigureAwait(false);
            var side = new byte[1];
            await tls.ReadExactlyAsync(side, token).ConfigureAwait(false);
            if (side[0] > 1) throw new IOException("Invalid screen arrangement.");
            var name = await Peer.ReadName(tls, token).ConfigureAwait(false);
            var remoteSharing = await Peer.ReadEndpoint(tls, token).ConfigureAwait(false);
            return new Peer(socket, tls, name, side[0] == 0 ? Side.Right : Side.Left) { RemoteSharingEndpoint = remoteSharing };
        }
        catch { tls.Dispose(); throw; }
    }

    static byte[] BuildTranscript(Guid client, Guid server, ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> serverNonce, byte[] certificateHash)
    {
        var transcript = new byte[128];
        client.TryWriteBytes(transcript);
        server.TryWriteBytes(transcript.AsSpan(16));
        clientNonce.CopyTo(transcript.AsSpan(32));
        serverNonce.CopyTo(transcript.AsSpan(64));
        certificateHash.CopyTo(transcript, 96);
        return transcript;
    }

    static byte[] Proof(byte[] key, byte[] transcript, byte role)
    {
        var message = new byte[transcript.Length + 1];
        message[0] = role;
        transcript.CopyTo(message, 1);
        return HMACSHA256.HashData(key, message);
    }
}
