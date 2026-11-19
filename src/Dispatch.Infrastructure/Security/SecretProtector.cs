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

        // Once secrets were encrypted with a file key, keep using it: switching to the credential store on a later start
        // (because it happened to be available then) would make the existing secrets unreadable.
        if (File.Exists(Path.Combine(dataDirectory, FileKeyName)))
            return FileKey(dataDirectory);

        if (OperatingSystem.IsMacOS())
        {
            if (CommandKey("security", ["find-generic-password", "-a", AccountName, "-s", ServiceName, "-w"]) is { } existing)
                return existing;
            var key = RandomNumberGenerator.GetBytes(32);
            // Without -U: if the item exists but could not be read (locked keychain), it must not be overwritten.
            if (Capture("security", ["add-generic-password", "-a", AccountName, "-s", ServiceName, "-w", Convert.ToBase64String(key)], null) is not null)
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
        return ReadOrCreate(path,
            () => ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser),
            key => ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser), restrict: false);
    }

    internal const string FileKeyName = "secrets.key";

    private static byte[] FileKey(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, FileKeyName);
        if (File.Exists(path))
            return Convert.FromBase64String(File.ReadAllText(path).Trim());
        return ReadOrCreate(path,
            () => Convert.FromBase64String(File.ReadAllText(path).Trim()),
            key => Encoding.ASCII.GetBytes(Convert.ToBase64String(key)), restrict: true);
    }

    /// <summary>
    /// Creates the key file once, under a lock file. Two processes starting together (the app and the CLI) must not
    /// each keep a key of their own, and the key goes to a temporary file that is then moved into place: a key file cut
    /// short by a crash or power loss would make every secret unreadable and every later save fail.
    /// </summary>
    private static byte[] ReadOrCreate(string path, Func<byte[]> read, Func<byte[], byte[]> encode, bool restrict)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var _ = AcquireLock(path + ".lock");
        if (File.Exists(path))
            return read(); // Another process created it while this one waited.
        var key = RandomNumberGenerator.GetBytes(32);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, encode(key));
        if (restrict && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
        return key;
    }

    private static FileStream AcquireLock(string lockPath)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(25);
            }
        }
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
