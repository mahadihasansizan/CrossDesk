using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.IO;
using System.Windows.Threading;

namespace DeskRelay;

record TransferEntry(string Path, long Length, bool Directory);
record TransferOffer(string Id, string Kind, TransferEntry[] Entries, long Bytes);
record TransferProgress(string Id, string Name, long Completed, long Total, string State);

sealed partial class SharingService : IDisposable
{
    readonly Dispatcher dispatcher;
    readonly string receiveRoot;
    readonly IPAddress bindAddress;
    readonly TcpListener listener;
    readonly X509Certificate2 certificate;
    readonly byte[] secret = RandomNumberGenerator.GetBytes(32);
    readonly CancellationTokenSource lifetime = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> transfers = new();
    string? remote;
    public string Endpoint { get; }
    public Func<TransferOffer, Task<bool>>? ConfirmReceive { get; set; }
    public event Action<TransferProgress>? ProgressChanged;
    public event Action<string>? StatusChanged;

    public SharingService(Dispatcher dispatcher, IPAddress bindAddress, string receiveRoot)
    {
        this.dispatcher = dispatcher;
        this.bindAddress = bindAddress;
        this.receiveRoot = Path.GetFullPath(receiveRoot);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var generated = new CertificateRequest("CN=Desk Relay Sharing", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
        listener = new TcpListener(bindAddress, 0);
        listener.Start();
        Endpoint = $"{bindAddress}:{((IPEndPoint)listener.LocalEndpoint).Port}:{Convert.ToHexString(secret)}:{certificate.GetCertHashString(HashAlgorithmName.SHA256)}";
        _ = AcceptLoop();
    }

    public void SetRemote(string endpoint)
    {
        var fields = endpoint.Split(':');
        if (fields.Length != 4 || !IPAddress.TryParse(fields[0], out _) || !int.TryParse(fields[1], out int port) || port is < 1 or > 65535 || fields[2].Length != 64 || fields[3].Length != 64)
            throw new IOException("Invalid sharing endpoint.");
        Convert.FromHexString(fields[2]);
        Convert.FromHexString(fields[3]);
        remote = endpoint;
    }

    async Task AcceptLoop()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
                _ = Receive(client);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    async Task<SslStream> Connect(CancellationToken token)
    {
        var fields = (remote ?? throw new IOException("Connect a computer first.")).Split(':');
        var client = new TcpClient(new IPEndPoint(bindAddress, 0));
        try
        {
            await client.ConnectAsync(IPAddress.Parse(fields[0]), int.Parse(fields[1]), token).ConfigureAwait(false);
            var pin = Convert.FromHexString(fields[3]);
            var stream = new SslStream(client.GetStream(), false, (_, cert, _, _) => cert != null && CryptographicOperations.FixedTimeEquals(pin, cert.GetCertHash(HashAlgorithmName.SHA256)));
            try
            {
                await stream.AuthenticateAsClientAsync(LanTls.ClientOptions("Desk Relay Sharing"), token).ConfigureAwait(false);
                await stream.WriteAsync(Convert.FromHexString(fields[2]), token).ConfigureAwait(false);
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }
        catch { client.Dispose(); throw; }
    }

    static async Task WriteJson<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        await stream.WriteAsync(length, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    static async Task<T> ReadJson<T>(Stream stream, CancellationToken token)
    {
        var length = new byte[4];
        await stream.ReadExactlyAsync(length, token).ConfigureAwait(false);
        int size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size < 1 || size > 4 * 1024 * 1024) throw new IOException("Invalid transfer metadata.");
        var bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new IOException("Missing transfer metadata.");
    }

    void Report(TransferProgress progress) => dispatcher.BeginInvoke(() => ProgressChanged?.Invoke(progress));
    void Status(string message) => dispatcher.BeginInvoke(() => StatusChanged?.Invoke(message));
    public void CancelAll() { foreach (var c in transfers.Values) c.Cancel(); }
    public void Dispose()
    {
        StopClipboard();
        lifetime.Cancel();
        CancelAll();
        listener.Stop();
        certificate.Dispose();
    }
}
