namespace Dispatch.Infrastructure;

public static class DispatchPaths
{
    /// <summary>Per-user data folder: %LOCALAPPDATA%\Dispatch on Windows, ~/.local/share/Dispatch on Linux/macOS.</summary>
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dispatch");

    /// <summary>The database shared by the desktop app and the CLI.</summary>
    public static string DefaultDatabase => Path.Combine(DataDirectory, "dispatch.db");

    /// <summary>The capture proxy's CA certificate and private key (PKCS#12), kept so trusting it sticks across launches.</summary>
    public static string CaptureCaFile => Path.Combine(DataDirectory, "capture-ca.pfx");
}
