using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DeskRelay;

sealed class AutoConnectSettings
{
    static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrossDesk", "connection.json");
    public string SharedCode { get; set; } = "";
    public bool Enabled { get; set; }
    public Guid DeviceId { get; set; } = Guid.NewGuid();
    record StoredSettings(Guid DeviceId, bool Enabled, string ProtectedCode);

    public static AutoConnectSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            var created = new AutoConnectSettings();
            created.Save();
            return created;
        }
        var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(SettingsPath))
            ?? throw new IOException("Saved connection settings could not be read.");
        if (stored.DeviceId == Guid.Empty) throw new IOException("Saved computer identity is invalid.");
        return new AutoConnectSettings
        {
            DeviceId = stored.DeviceId,
            Enabled = stored.Enabled,
            SharedCode = stored.ProtectedCode.Length == 0 ? "" : Encoding.UTF8.GetString(Protect(Convert.FromBase64String(stored.ProtectedCode), false))
        };
    }

    public void Save()
    {
        if (DeviceId == Guid.Empty) DeviceId = Guid.NewGuid();
        string encrypted = SharedCode.Length == 0 ? "" : Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(SharedCode), true));
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        string temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new StoredSettings(DeviceId, Enabled, encrypted)));
            File.Move(temporary, SettingsPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);

    static byte[] Protect(byte[] bytes, bool encrypt)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Marshal.Copy(bytes, 0, input.Data, bytes.Length);
        Blob output = default;
        try
        {
            bool success = encrypt
                ? CryptProtectData(ref input, "CrossDesk saved connection", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not access the saved connection code.");
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            Marshal.Copy(new byte[input.Size], 0, input.Data, input.Size);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }
}
