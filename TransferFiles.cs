using System.IO;
using System.Security.Cryptography;
using System.Net.Sockets;
using System.Net.Security;

namespace DeskRelay;

sealed partial class SharingService
{
    public async Task SendFilesAsync(string[] paths, bool clipboard = false, CancellationToken token = default)
    {
        var id = Guid.NewGuid().ToString("N");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        transfers[id] = cancel;
        var ct = cancel.Token;
        try
        {
            var sources = new List<(TransferEntry Entry, string Source)>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                var name = Path.GetFileName(full);
                if (!names.Add(name)) throw new IOException("Selected items have duplicate names.");
                Add(full, name);
            }
            void Add(string source, string relative)
            {
                ct.ThrowIfCancellationRequested();
                if (sources.Count >= 20000) throw new IOException("Send fewer than 20,000 items per transfer.");
                var attributes = File.GetAttributes(source);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Symbolic links and junctions cannot be transferred.");
                bool folder = (attributes & FileAttributes.Directory) != 0;
                sources.Add((new TransferEntry(relative, folder ? 0 : new FileInfo(source).Length, folder), source));
                if (folder)
                    foreach (var child in Directory.EnumerateFileSystemEntries(source))
                        Add(child, relative + "/" + Path.GetFileName(child));
            }
            long total = sources.Sum(s => s.Entry.Length);
            var offer = new TransferOffer(id, clipboard ? "clipboard-files" : "files", sources.Select(s => s.Entry).ToArray(), total);
            using var stream = await Connect(ct).ConfigureAwait(false);
            await WriteJson(stream, offer, ct).ConfigureAwait(false);
            if (!await ReadJson<bool>(stream, ct).ConfigureAwait(false)) throw new IOException("The other computer declined the transfer.");
            long done = 0;
            var buffer = new byte[128 * 1024];
            foreach (var item in sources.Where(s => !s.Entry.Directory))
            {
                await using var file = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, true);
                if (file.Length != item.Entry.Length) throw new IOException("A source file changed. Try again.");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long remaining = file.Length;
                while (remaining > 0)
                {
                    int count = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
                    if (count == 0) throw new IOException("A source file changed during transfer.");
                    hash.AppendData(buffer, 0, count);
                    await stream.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                    remaining -= count;
                    done += count;
                    Report(new(id, item.Entry.Path, done, total, "Sending"));
                }
                await stream.WriteAsync(hash.GetHashAndReset(), ct).ConfigureAwait(false);
            }
            if (!await ReadJson<bool>(stream, ct).ConfigureAwait(false)) throw new IOException("The receiver could not verify the transfer.");
            Report(new(id, "Transfer complete", total, total, "Complete"));
        }
        catch (Exception e) { Report(new(id, e is OperationCanceledException ? "Transfer cancelled" : e.Message, 0, 0, "Stopped")); throw; }
        finally { transfers.TryRemove(id, out _); }
    }

    async Task Receive(TcpClient client)
    {
        using (client)
        using (var stream = new SslStream(client.GetStream()))
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            string id = Guid.NewGuid().ToString("N");
            string? staging = null;
            transfers[id] = cancel;
            try
            {
                cancel.CancelAfter(TimeSpan.FromSeconds(10));
                var ct = cancel.Token;
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, ct).ConfigureAwait(false);
                var auth = new byte[32];
                await stream.ReadExactlyAsync(auth, ct).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(auth, secret)) return;
                cancel.CancelAfter(Timeout.InfiniteTimeSpan);
                var offer = await ReadJson<TransferOffer>(stream, ct).ConfigureAwait(false);
                if (offer.Kind is "text" or "image")
                {
                    await ReceiveClipboard(stream, offer, ct).ConfigureAwait(false);
                    return;
                }
                if (offer.Kind is not ("files" or "clipboard-files") || offer.Entries.Length is 0 or > 20000) throw new IOException("Unsupported transfer.");
                var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long total = 0;
                foreach (var entry in offer.Entries)
                {
                    ValidatePath(entry.Path);
                    if (!entries.Add(entry.Path.Replace('/', '\\')) || entry.Length < 0 || (entry.Directory && entry.Length != 0)) throw new IOException("Invalid file manifest.");
                    total = checked(total + entry.Length);
                }
                if (total != offer.Bytes) throw new IOException("Transfer size does not match its manifest.");
                bool accepted = ConfirmReceive != null && await dispatcher.InvokeAsync(() => ConfirmReceive(offer)).Task.Unwrap().ConfigureAwait(false);
                await WriteJson(stream, accepted, ct).ConfigureAwait(false);
                if (!accepted) return;
                Directory.CreateDirectory(receiveRoot);
                staging = Path.Combine(receiveRoot, ".partial-" + id);
                Directory.CreateDirectory(staging);
                long done = 0;
                var buffer = new byte[128 * 1024];
                foreach (var entry in offer.Entries)
                {
                    var destination = Path.Combine(staging, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                    if (entry.Directory) { Directory.CreateDirectory(destination); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, true))
                    {
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        long remaining = entry.Length;
                        while (remaining > 0)
                        {
                            int count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
                            if (count == 0) throw new EndOfStreamException("The sender disconnected.");
                            hash.AppendData(buffer, 0, count);
                            await file.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                            remaining -= count;
                            done += count;
                            Report(new(id, entry.Path, done, total, "Receiving"));
                        }
                        var expected = new byte[32];
                        await stream.ReadExactlyAsync(expected, ct).ConfigureAwait(false);
                        if (!CryptographicOperations.FixedTimeEquals(expected, hash.GetHashAndReset())) throw new IOException("File integrity check failed.");
                    }
                }
                var final = Path.Combine(receiveRoot, "Received-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + id[..6]);
                Directory.Move(staging, final);
                staging = null;
                if (offer.Kind == "clipboard-files") await PublishFiles(final).ConfigureAwait(false);
                await WriteJson(stream, true, ct).ConfigureAwait(false);
                Report(new(id, final, total, total, "Complete"));
            }
            catch (Exception e) { if (!lifetime.IsCancellationRequested) Report(new(id, e is OperationCanceledException ? "Transfer cancelled" : e.Message, 0, 0, "Stopped")); }
            finally
            {
                transfers.TryRemove(id, out _);
                if (staging != null && Path.GetFullPath(staging).StartsWith(receiveRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) throw new IOException("Invalid file path.");
        foreach (var part in path.Replace('\\', '/').Split('/'))
        {
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
                throw new IOException("Unsafe file path in transfer.");
        }
    }
}
