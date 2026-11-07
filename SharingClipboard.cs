using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DeskRelay;

sealed partial class SharingService
{
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    DispatcherTimer? clipboardTimer;
    uint clipboardSequence;
    bool clipboardBusy;
    bool clipboardEnabled;

    public void StartClipboard()
    {
        clipboardEnabled = true;
        clipboardSequence = GetClipboardSequenceNumber();
        clipboardTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, ClipboardTick, dispatcher);
        clipboardTimer.Start();
    }
    public void StopClipboard()
    {
        clipboardEnabled = false;
        clipboardTimer?.Stop();
    }

    async void ClipboardTick(object? sender, EventArgs args)
    {
        if (clipboardBusy || remote == null || clipboardSequence == GetClipboardSequenceNumber()) return;
        clipboardBusy = true;
        try
        {
            var sequence = GetClipboardSequenceNumber();
            if (Clipboard.ContainsFileDropList())
            {
                var paths = Clipboard.GetFileDropList().Cast<string>().ToArray();
                clipboardSequence = sequence;
                await Task.Run(() => SendFilesAsync(paths, true, lifetime.Token));
            }
            else
            {
                byte[]? data = null;
                string kind = "text";
                if (Clipboard.ContainsImage())
                {
                    var bitmap = Clipboard.GetImage();
                    if (bitmap != null)
                    {
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var memory = new MemoryStream();
                        encoder.Save(memory);
                        data = memory.ToArray();
                        kind = "image";
                    }
                }
                else if (Clipboard.ContainsText()) data = System.Text.Encoding.UTF8.GetBytes(Clipboard.GetText());
                clipboardSequence = sequence;
                if (data != null)
                {
                    if (data.Length > 32 * 1024 * 1024) throw new IOException("Clipboard item exceeds 32 MB. Send it as a file.");
                    await SendClipboard(kind, data, lifetime.Token);
                }
            }
        }
        catch (ExternalException) { /* The clipboard owner will release it on a later tick. */ }
        catch (Exception e) when (e is IOException or OperationCanceledException or System.Net.Sockets.SocketException) { Status(e.Message); }
        finally { clipboardBusy = false; }
    }

    async Task SendClipboard(string kind, byte[] data, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var stream = await Connect(timeout.Token).ConfigureAwait(false);
        await WriteJson(stream, new TransferOffer(Guid.NewGuid().ToString("N"), kind, [], data.Length), timeout.Token).ConfigureAwait(false);
        if (!await ReadJson<bool>(stream, timeout.Token).ConfigureAwait(false)) return;
        await stream.WriteAsync(data, timeout.Token).ConfigureAwait(false);
        await stream.WriteAsync(SHA256.HashData(data), timeout.Token).ConfigureAwait(false);
        if (!await ReadJson<bool>(stream, timeout.Token).ConfigureAwait(false)) throw new IOException("Clipboard could not be applied on the other computer.");
    }

    async Task ReceiveClipboard(Stream stream, TransferOffer offer, CancellationToken token)
    {
        bool allowed = clipboardEnabled && offer.Bytes is >= 0 and <= 32 * 1024 * 1024;
        await WriteJson(stream, allowed, token).ConfigureAwait(false);
        if (!allowed) return;
        var bytes = new byte[(int)offer.Bytes];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        var hash = new byte[32];
        await stream.ReadExactlyAsync(hash, token).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(bytes))) throw new IOException("Clipboard integrity check failed.");
        await dispatcher.InvokeAsync(() =>
        {
            if (!clipboardEnabled) return;
            if (offer.Kind == "text") Clipboard.SetText(System.Text.Encoding.UTF8.GetString(bytes));
            else
            {
                using var memory = new MemoryStream(bytes);
                var decoder = BitmapDecoder.Create(memory, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var bitmap = decoder.Frames[0];
                if ((long)bitmap.PixelWidth * bitmap.PixelHeight > 100_000_000) throw new IOException("Clipboard image dimensions are too large.");
                Clipboard.SetImage(bitmap);
            }
            clipboardSequence = GetClipboardSequenceNumber();
        });
        await WriteJson(stream, true, token).ConfigureAwait(false);
        Status("Clipboard received");
    }

    async Task PublishFiles(string folder)
    {
        await dispatcher.InvokeAsync(() =>
        {
            if (!clipboardEnabled) return;
            var files = new System.Collections.Specialized.StringCollection();
            files.AddRange(Directory.GetFileSystemEntries(folder));
            Clipboard.SetFileDropList(files);
            clipboardSequence = GetClipboardSequenceNumber();
        });
    }
}
