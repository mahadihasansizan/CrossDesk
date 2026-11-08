using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DeskRelay;

enum Side : byte { Left, Right }
enum Kind { KeepAlive, Move, Key, Button, Release, Focus }
readonly record struct Frame(Kind Kind, int A = 0, int B = 0, int C = 0);

static class LanTls
{
    // Both callers validate the exact certificate fingerprint from the pairing exchange.
    public static SslClientAuthenticationOptions ClientOptions(string target) => new()
    {
        TargetHost = target,
        CertificateChainPolicy = new X509ChainPolicy
        {
            DisableCertificateDownloads = true,
            RevocationMode = X509RevocationMode.NoCheck
        }
    };
}

record Lan(IPAddress Address, IPAddress Mask, string Name)
{
    public override string ToString() => $"{Name}  ·  {Address}";

    public bool Contains(IPAddress peer)
    {
        if (peer.AddressFamily != AddressFamily.InterNetwork) return false;
        var address = Address.GetAddressBytes();
        var mask = Mask.GetAddressBytes();
        var other = peer.GetAddressBytes();
        for (int i = 0; i < 4; i++)
            if ((address[i] & mask[i]) != (other[i] & mask[i])) return false;
        return true;
    }

    public static Lan[] Find() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up &&
            n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                a.PrefixLength > 0 && a.PrefixLength < 32 && !IPAddress.IsLoopback(a.Address))
            .Select(a => new Lan(a.Address, a.IPv4Mask, n.Name)))
        .ToArray();
}

sealed class Invitation : IDisposable
{
    readonly TcpListener listener;
    readonly X509Certificate2 certificate;
    readonly byte[] secret = RandomNumberGenerator.GetBytes(24);
    readonly Lan lan;
    public string Code { get; }

    public Invitation(Lan lan)
    {
        this.lan = lan;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Desk Relay", key, HashAlgorithmName.SHA256);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
        listener = new TcpListener(lan.Address, 0);
        try { listener.Start(4); }
        catch { certificate.Dispose(); throw; }
        Code = $"DR2:{lan.Address}:{((IPEndPoint)listener.LocalEndpoint).Port}:" +
            $"{Convert.ToHexString(secret)}:{certificate.GetCertHashString(HashAlgorithmName.SHA256)}";
    }

    public async Task<Peer> Accept(Side side, CancellationToken cancel, string sharingEndpoint = "")
    {
        while (true)
        {
            var socket = await listener.AcceptTcpClientAsync(cancel).ConfigureAwait(false);
            if (!lan.Contains(((IPEndPoint)socket.Client.RemoteEndPoint!).Address))
            {
                socket.Dispose();
                continue;
            }

            var tls = new SslStream(socket.GetStream());
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                var token = timeout.Token;
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate
                }, token).ConfigureAwait(false);

                var presented = new byte[secret.Length];
                await tls.ReadExactlyAsync(presented, token).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(secret, presented))
                    throw new IOException("Pairing code was rejected.");

                var name = await Peer.ReadName(tls, token).ConfigureAwait(false);
                var remoteSharing = await Peer.ReadEndpoint(tls, token).ConfigureAwait(false);
                await tls.WriteAsync(new[] { (byte)side }, token).ConfigureAwait(false);
                await Peer.WriteName(tls, token).ConfigureAwait(false);
                await Peer.WriteEndpoint(tls, sharingEndpoint, token).ConfigureAwait(false);
                return new Peer(socket, tls, name, side) { RemoteSharingEndpoint = remoteSharing, IsHost = true };
            }
            catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException)
            {
                tls.Dispose();
                socket.Dispose();
                cancel.ThrowIfCancellationRequested();
            }
            catch
            {
                tls.Dispose();
                socket.Dispose();
                throw;
            }
        }
    }

    public void Dispose()
    {
        listener.Stop();
        certificate.Dispose();
        CryptographicOperations.ZeroMemory(secret);
    }
}

sealed class Peer : IDisposable
{
    readonly TcpClient socket;
    readonly SslStream tls;
    readonly CancellationTokenSource stop = new();
    readonly LinkedList<Frame> pending = new();
    readonly SemaphoreSlim ready = new(0, 1);
    readonly object queueLock = new();
    int closed;
    public string Name { get; }
    public string RemoteSharingEndpoint { get; init; } = string.Empty;
    public bool IsHost { get; init; }
    public Side Side { get; }
    public bool Online => Volatile.Read(ref closed) == 0;

    internal Peer(TcpClient socket, SslStream tls, string name, Side side)
    {
        this.socket = socket;
        this.tls = tls;
        Name = name;
        Side = side;
        socket.NoDelay = true;
    }

    public static async Task<Peer> Join(string code, CancellationToken cancel, string sharingEndpoint = "")
    {
        var parts = code.Trim().Split(':');
        if (parts.Length != 5 || parts[0] != "DR2" || !IPAddress.TryParse(parts[1], out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork ||
            !int.TryParse(parts[2], out int port) || port is < 1 or > 65535 ||
            parts[3].Length != 48 || parts[4].Length != 64)
            throw new FormatException("Paste the full pairing code from the other computer.");

        byte[] secret, pin;
        try
        {
            secret = Convert.FromHexString(parts[3]);
            pin = Convert.FromHexString(parts[4]);
        }
        catch (FormatException)
        {
            throw new FormatException("Paste the full pairing code from the other computer.");
        }

        var adapters = Lan.Find();
        var local = adapters.FirstOrDefault(l => l.Contains(address));
        if (local is null || adapters.Any(l => l.Address.Equals(address)))
            throw new IOException("Use the code from another computer on the same local network.");

        var socket = new TcpClient(new IPEndPoint(local.Address, 0));
        SslStream? tls = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var token = timeout.Token;
            await socket.ConnectAsync(address, port, token).ConfigureAwait(false);
            socket.NoDelay = true;
            tls = new SslStream(socket.GetStream(), false, (_, cert, _, _) => cert is not null &&
                CryptographicOperations.FixedTimeEquals(pin, cert.GetCertHash(HashAlgorithmName.SHA256)));
            await tls.AuthenticateAsClientAsync(LanTls.ClientOptions("Desk Relay"), token).ConfigureAwait(false);
            await tls.WriteAsync(secret, token).ConfigureAwait(false);
            await WriteName(tls, token).ConfigureAwait(false);
            await WriteEndpoint(tls, sharingEndpoint, token).ConfigureAwait(false);
            var side = new byte[1];
            await tls.ReadExactlyAsync(side, token).ConfigureAwait(false);
            if (side[0] > 1) throw new IOException("The other computer returned an invalid layout.");
            var name = await ReadName(tls, token).ConfigureAwait(false);
            var remoteSharing = await ReadEndpoint(tls, token).ConfigureAwait(false);
            return new Peer(socket, tls, name, side[0] == 0 ? Side.Right : Side.Left) { RemoteSharingEndpoint = remoteSharing };
        }
        catch
        {
            tls?.Dispose();
            socket.Dispose();
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    internal static async Task WriteEndpoint(Stream stream, string endpoint, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(endpoint);
        if (bytes.Length > 1024) throw new IOException("Sharing endpoint is too long.");
        var length = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)bytes.Length);
        await stream.WriteAsync(length, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    internal static async Task<string> ReadEndpoint(Stream stream, CancellationToken token)
    {
        var length = new byte[2];
        await stream.ReadExactlyAsync(length, token).ConfigureAwait(false);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(length);
        if (count > 1024) throw new IOException("Invalid sharing endpoint.");
        var bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    internal static async Task WriteName(Stream stream, CancellationToken token)
    {
        var name = Encoding.UTF8.GetBytes(Environment.MachineName);
        if (name.Length > 128) name = "Windows PC"u8.ToArray();
        await stream.WriteAsync(new[] { (byte)name.Length }, token).ConfigureAwait(false);
        await stream.WriteAsync(name, token).ConfigureAwait(false);
    }

    internal static async Task<string> ReadName(Stream stream, CancellationToken token)
    {
        var length = new byte[1];
        await stream.ReadExactlyAsync(length, token).ConfigureAwait(false);
        if (length[0] is 0 or > 128) throw new IOException("Invalid computer name.");
        var data = new byte[length[0]];
        await stream.ReadExactlyAsync(data, token).ConfigureAwait(false);
        var name = new string(Encoding.UTF8.GetString(data).Where(c => char.IsLetterOrDigit(c) ||
            c is '-' or '_' or '.' or ' ').ToArray()).Trim();
        return name.Length == 0 ? "Windows PC" : name;
    }

    public bool Send(Frame frame)
    {
        lock (queueLock)
        {
            if (!Online) return false;
            if (frame.Kind == Kind.Move && pending.Last is { Value.Kind: Kind.Move } tail)
            {
                tail.Value = frame;
                return true;
            }

            if (pending.Count >= 256)
            {
                // Keep native hooks free of socket shutdown work.
                ThreadPool.QueueUserWorkItem(_ => Dispose());
                return false;
            }

            pending.AddLast(frame);
            if (ready.CurrentCount == 0) ready.Release();
            return true;
        }
    }

    public async Task Run(Func<Frame, Task> receive)
    {
        var read = ReadLoop(receive);
        var write = WriteLoop();
        var ended = await Task.WhenAny(read, write).ConfigureAwait(false);
        Dispose();
        try { await Task.WhenAll(read, write).ConfigureAwait(false); }
        catch when (ended.IsCanceled || ended.IsCompletedSuccessfully) { }
        finally
        {
            tls.Dispose();
            stop.Dispose();
            ready.Dispose();
        }
    }

    async Task ReadLoop(Func<Frame, Task> receive)
    {
        var bytes = new byte[16];
        while (!stop.IsCancellationRequested)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await tls.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
            var kind = (Kind)BinaryPrimitives.ReadInt32LittleEndian(bytes);
            if (kind is < Kind.KeepAlive or > Kind.Focus) throw new IOException("Unknown input event.");
            if (kind != Kind.KeepAlive)
                await receive(new Frame(kind, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)),
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12)))).ConfigureAwait(false);
        }
    }

    async Task WriteLoop()
    {
        const int frameSize = 16;
        var batch = new byte[32 * frameSize];
        while (!stop.IsCancellationRequested)
        {
            await ready.WaitAsync(TimeSpan.FromSeconds(1), stop.Token).ConfigureAwait(false);
            int count = 0;
            lock (queueLock)
            {
                while (count < batch.Length && pending.First is { } item)
                {
                    Encode(batch.AsSpan(count, frameSize), item.Value);
                    pending.RemoveFirst();
                    count += frameSize;
                }
                if (pending.Count > 0 && ready.CurrentCount == 0) ready.Release();
            }
            if (count == 0)
            {
                Encode(batch.AsSpan(0, frameSize), default);
                count = frameSize;
            }
            await tls.WriteAsync(batch.AsMemory(0, count), stop.Token).ConfigureAwait(false);
        }
    }

    static void Encode(Span<byte> bytes, Frame frame)
    {
        BinaryPrimitives.WriteInt32LittleEndian(bytes, (int)frame.Kind);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], frame.A);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], frame.B);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[12..], frame.C);
    }

    public void Dispose()
    {
        lock (queueLock)
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
        }
        stop.Cancel();
        socket.Dispose();
    }
}
