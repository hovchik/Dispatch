using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Dispatch.Application.Abstractions;

namespace Dispatch.Infrastructure.Security;

/// <summary>
/// Encrypts secret variable values with AES-256-GCM. The key is created once and kept in the OS credential store:
/// DPAPI (Windows), the login Keychain (macOS) or the Secret Service via <c>secret-tool</c> (Linux); if none is
/// available it lives in a user-only (0600) file next to the database.
/// </summary>
public sealed class SecretProtector : ISecretProtector
{
    public const string Prefix = "enc:v1:";
    private const string ServiceName = "dispatch-api-client";
    private const string AccountName = "variables-key";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly Lazy<byte[]> _key;

    public SecretProtector(string dataDirectory) : this(() => LoadOrCreateKey(dataDirectory))
    {
    }

    internal SecretProtector(Func<byte[]> keyFactory) => _key = new Lazy<byte[]>(keyFactory, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsProtected(string value) => value.StartsWith(Prefix, StringComparison.Ordinal);

    public string Protect(string plainText)
    {
        if (plainText.Length == 0 || IsProtected(plainText))
            return plainText;
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = Encoding.UTF8.GetBytes(plainText);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(_key.Value, TagSize))
            aes.Encrypt(nonce, plain, cipher, tag);
        return Prefix + Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    public string Unprotect(string protectedText)
    {
        if (!IsProtected(protectedText))
            return protectedText;
        var data = Convert.FromBase64String(protectedText[Prefix.Length..]);
        if (data.Length < NonceSize + TagSize)
            throw new CryptographicException("Corrupt secret value.");
        var plain = new byte[data.Length - NonceSize - TagSize];
        using (var aes = new AesGcm(_key.Value, TagSize))
            aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize, plain.Length), data.AsSpan(data.Length - TagSize), plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] LoadOrCreateKey(string dataDirectory)
    {
        if (OperatingSystem.IsWindows())
            return WindowsKey(dataDirectory);

        if (OperatingSystem.IsMacOS())
        {
            if (CommandKey("security", ["find-generic-password", "-a", AccountName, "-s", ServiceName, "-w"]) is { } existing)
                return existing;
            var key = RandomNumberGenerator.GetBytes(32);
            if (Capture("security", ["add-generic-password", "-U", "-a", AccountName, "-s", ServiceName, "-w", Convert.ToBase64String(key)], null) is not null)
                return key;
        }

        if (OperatingSystem.IsLinux())
        {
            if (CommandKey("secret-tool", ["lookup", "service", ServiceName, "account", AccountName]) is { } existing)
                return existing;
            var key = RandomNumberGenerator.GetBytes(32);
            if (Capture("secret-tool", ["store", "--label=Dispatch variables key", "service", ServiceName, "account", AccountName],
                    Convert.ToBase64String(key)) is not null
                && CommandKey("secret-tool", ["lookup", "service", ServiceName, "account", AccountName]) is { } stored)
                return stored;
        }

        return FileKey(dataDirectory);
    }

    private static byte[] WindowsKey(string dataDirectory)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        var path = Path.Combine(dataDirectory, "secrets.key.dpapi");
        if (File.Exists(path))
            return ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        var key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllBytes(path, ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));
        return key;
    }

    private static byte[] FileKey(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "secrets.key");
        if (File.Exists(path))
            return Convert.FromBase64String(File.ReadAllText(path).Trim());
        Directory.CreateDirectory(dataDirectory);
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(path, Convert.ToBase64String(key));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }

    private static byte[]? CommandKey(string command, string[] args)
    {
        var output = Capture(command, args, null);
        if (output is null)
            return null;
        try
        {
            var key = Convert.FromBase64String(output.Trim());
            return key.Length == 32 ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Runs a helper tool; null when it's missing, fails, or hangs.</summary>
    private static string? Capture(string command, string[] args, string? input)
    {
        try
        {
            var info = new ProcessStartInfo(command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = input is not null,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);
            using var process = Process.Start(info);
            if (process is null)
                return null;
            if (input is not null)
            {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
            }
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
