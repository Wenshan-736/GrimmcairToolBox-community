using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Grimmcair.Desktop.Services;

public sealed class DesktopRememberedCredentials
{
    public string Username { get; }
    public string Key { get; }
    public DesktopRememberedCredentials(string username, string key) { Username = username; Key = key; }
    public override string ToString() => "Remembered desktop credentials (private content omitted)";
}

/// <summary>Endpoint-isolated credentials protected by Windows DPAPI CurrentUser. Never persists a session or authorization.</summary>
public sealed class DesktopCredentialStore
{
    private readonly string directory;
    private readonly object gate = new();
    private const uint UiForbidden = 1;
    private const int MaximumProtectedBytes = 32768;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed class CredentialDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public string Kind { get; set; } = "grimmcair.desktop-credentials";
        public string BaseUrl { get; set; } = "";
        public string Username { get; set; } = "";
        public string Key { get; set; } = "";
    }
    public DesktopCredentialStore(string directory) => this.directory = Path.GetFullPath(directory);

    public DesktopRememberedCredentials? Load(string baseUrl)
    {
        var endpoint = DesktopCloudStateStore.NormalizeBaseUrl(baseUrl);
        lock (gate)
        {
            var path = FilePath(endpoint);
            if (!File.Exists(path)) return null;
            byte[]? plaintext = null;
            try
            {
                var size = new FileInfo(path).Length;
                if (size is < 1 or > MaximumProtectedBytes) throw new InvalidDataException();
                plaintext = Unprotect(File.ReadAllBytes(path), Entropy(endpoint));
                var value = JsonSerializer.Deserialize<CredentialDocument>(plaintext, Json) ?? throw new InvalidDataException();
                if (value.SchemaVersion != 1 || value.Kind != "grimmcair.desktop-credentials" || value.BaseUrl != endpoint)
                    throw new InvalidDataException();
                Validate(value.Username, value.Key);
                return new(value.Username, value.Key);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
                or CryptographicException or InvalidOperationException or ArgumentException)
            {
                // Native/JSON errors can contain local details. Only expose this fixed recovery message.
                throw new InvalidDataException("记住的登录资料暂时无法读取，请重新填写用户名和专用密钥。");
            }
            finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
        }
    }

    public void Save(string baseUrl, string username, string key)
    {
        var endpoint = DesktopCloudStateStore.NormalizeBaseUrl(baseUrl);
        username = username.Trim(); Validate(username, key);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new CredentialDocument { BaseUrl = endpoint, Username = username, Key = key }, Json);
        byte[] ciphertext;
        try { ciphertext = Protect(plaintext, Entropy(endpoint)); }
        catch (Exception error) when (error is CryptographicException or InvalidOperationException)
        { throw new InvalidDataException("登录已验证，但记住登录资料未能加密保存。"); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        lock (gate)
        {
            var path = FilePath(endpoint);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(directory);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(ciphertext); stream.Flush(true); }
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new InvalidDataException("登录已验证，但记住登录资料未能写入本机。"); }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* The leftover contains only DPAPI ciphertext. */ }
            }
        }
    }

    public void Clear(string baseUrl)
    {
        var endpoint = DesktopCloudStateStore.NormalizeBaseUrl(baseUrl);
        lock (gate)
        {
            try { File.Delete(FilePath(endpoint)); }
            catch (DirectoryNotFoundException) { /* Nothing was remembered for this endpoint. */ }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new InvalidDataException("记住的登录资料未能删除，请检查本机文件权限。"); }
        }
    }

    private string FilePath(string endpoint) => Path.Combine(directory, DesktopCloudStateStore.Hash(endpoint) + ".dpapi");
    private static byte[] Entropy(string endpoint) => SHA256.HashData(Encoding.UTF8.GetBytes("grimmcair.desktop-credentials:v1:" + endpoint));
    private static void Validate(string username, string key)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 32 || username.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(key) || key.Length > 1024 || key.Any(char.IsControl))
            throw new InvalidDataException("登录资料格式无效，未保存。");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static byte[] Protect(byte[] value, byte[] entropy) => Transform(value, entropy, protect: true);
    private static byte[] Unprotect(byte[] value, byte[] entropy) => Transform(value, entropy, protect: false);
    private static byte[] Transform(byte[] value, byte[] entropy, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("记住登录仅支持 Windows 用户保护。");
        var input = new DataBlob { Size = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        var optionalEntropy = new DataBlob { Size = entropy.Length, Data = Marshal.AllocHGlobal(entropy.Length) };
        var output = new DataBlob();
        try
        {
            Marshal.Copy(value, 0, input.Data, value.Length); Marshal.Copy(entropy, 0, optionalEntropy.Data, entropy.Length);
            var success = protect
                ? CryptProtectData(ref input, null, ref optionalEntropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref optionalEntropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!success) throw new CryptographicException("Windows 用户保护未完成。");
            if (output.Size is < 1 or > MaximumProtectedBytes || output.Data == IntPtr.Zero)
                throw new CryptographicException("Windows 用户保护结果无效。");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            Wipe(input.Data, input.Size); Marshal.FreeHGlobal(input.Data);
            Wipe(optionalEntropy.Data, optionalEntropy.Size); Marshal.FreeHGlobal(optionalEntropy.Data);
            if (output.Data != IntPtr.Zero) { Wipe(output.Data, output.Size); LocalFree(output.Data); }
            CryptographicOperations.ZeroMemory(entropy);
        }
    }
    private static void Wipe(IntPtr pointer, int size)
    {
        if (pointer == IntPtr.Zero || size <= 0) return;
        var zero = new byte[Math.Min(size, 4096)];
        for (var offset = 0; offset < size; offset += zero.Length)
            Marshal.Copy(zero, 0, IntPtr.Add(pointer, offset), Math.Min(zero.Length, size - offset));
    }
}
