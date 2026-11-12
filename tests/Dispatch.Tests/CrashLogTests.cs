using Dispatch.Infrastructure;

namespace Dispatch.Tests;

public class CrashLogTests
{
    [Fact]
    public void Writes_exception_with_stack_trace_to_dated_file_and_prunes_old_ones()
    {
        var original = CrashLog.Directory;
        var directory = Path.Combine(Path.GetTempPath(), "dispatch-crashlog-" + Guid.NewGuid().ToString("N"));
        CrashLog.Directory = directory;
        try
        {
            Directory.CreateDirectory(directory);
            for (var i = 0; i < 25; i++)
                File.WriteAllText(Path.Combine(directory, $"crash-2000-01-{i + 1:00}.log"), "old");

            Exception thrown;
            try { throw new InvalidOperationException("boom"); }
            catch (Exception ex) { thrown = ex; }

            var path = CrashLog.Write("Unit test", thrown);

            Assert.NotNull(path);
            Assert.Equal(directory, Path.GetDirectoryName(path));
            Assert.Equal($"crash-{DateTime.Now:yyyy-MM-dd}.log", Path.GetFileName(path));
            var text = File.ReadAllText(path!);
            Assert.Contains("Unit test", text);
            Assert.Contains("InvalidOperationException: boom", text);
            Assert.Contains(nameof(Writes_exception_with_stack_trace_to_dated_file_and_prunes_old_ones), text);

            var remaining = Directory.GetFiles(directory, "crash-*.log").Select(Path.GetFileName).OrderBy(n => n).ToList();
            Assert.Equal(20, remaining.Count);
            Assert.Contains(Path.GetFileName(path), remaining);
            Assert.DoesNotContain("crash-2000-01-01.log", remaining);
        }
        finally
        {
            CrashLog.Directory = original;
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Write_never_throws_when_the_folder_is_unusable()
    {
        var original = CrashLog.Directory;
        var file = Path.GetTempFileName(); // a file where a directory is expected
        CrashLog.Directory = Path.Combine(file, "logs");
        try
        {
            Assert.Null(CrashLog.Write("Unit test", new Exception("x")));
        }
        finally
        {
            CrashLog.Directory = original;
            File.Delete(file);
        }
    }
}
