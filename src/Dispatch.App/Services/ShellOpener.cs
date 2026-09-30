using System.Diagnostics;

namespace Dispatch.App.Services;

/// <summary>Opens files and URLs with the operating system's default application.</summary>
public static class ShellOpener
{
    public static void Open(string pathOrUrl)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", pathOrUrl);
        else
            Process.Start("xdg-open", pathOrUrl);
    }

    /// <summary>Writes <paramref name="content"/> to a temporary file with the given extension and opens it.</summary>
    public static string OpenTemp(string name, string extension, byte[] content)
    {
        var directory = Path.Combine(Path.GetTempPath(), "dispatch-preview");
        Directory.CreateDirectory(directory);
        var safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')).Trim('-');
        var path = Path.Combine(directory, $"{(safe.Length == 0 ? "response" : safe)}-{DateTime.Now:HHmmss}{extension}");
        File.WriteAllBytes(path, content);
        Open(path);
        return path;
    }
}
