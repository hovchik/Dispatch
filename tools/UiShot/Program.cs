using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Dispatch.App;
using Dispatch.App.ViewModels;

// Renders the real Dispatch main window (and the tool windows) off-screen with the headless Avalonia
// platform and saves PNG screenshots, so UI changes can be reviewed without a display.
// Usage: dotnet run --project tools/UiShot -- <outDir> [light]
var outDir = args.Length > 0 ? args[0] : "shots";
var light = args.Contains("light");
Directory.CreateDirectory(outDir);

// Isolated data directory so the harness never touches a real profile.
var data = Path.Combine(Path.GetTempPath(), "dispatch-uishot-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(data);
Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
Environment.SetEnvironmentVariable("LOCALAPPDATA", data);
Environment.SetEnvironmentVariable("HOME", data);

// A tiny local API so the response pane has something real to show.
var listener = new HttpListener();
listener.Prefixes.Add("http://127.0.0.1:18777/");
listener.Start();
_ = Task.Run(async () =>
{
    while (listener.IsListening)
    {
        var ctx = await listener.GetContextAsync();
        var json = """{"id":42,"name":"Ada Lovelace","email":"ada@example.com","roles":["admin","analyst"],"address":{"city":"London","zip":"SW1A"},"active":true,"score":98.6,"tags":[],"createdAt":"2026-10-08T09:12:00Z"}""";
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers["X-Request-Id"] = "7f3a9c1e";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }
});

var lifetime = new ClassicDesktopStyleApplicationLifetime();
AppBuilder.Configure<App>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .UseSkia()
    .WithInterFont()
    .SetupWithLifetime(lifetime);

var window = lifetime.MainWindow!;
if (light && Avalonia.Application.Current is { } app)
    app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
window.Show();
var vm = (MainWindowViewModel)window.DataContext!;

void Pump(int ms)
{
    var until = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < until)
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(10);
    }
    Dispatcher.UIThread.RunJobs();
}

void Wait(Task task)
{
    while (!task.IsCompleted)
        Pump(50);
    if (task.IsFaulted) Console.Error.WriteLine(task.Exception);
}

void Shot(string name)
{
    Pump(150);
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    Dispatcher.UIThread.RunJobs();
    using var frame = window.CaptureRenderedFrame();
    if (frame is null) { Console.WriteLine("no frame for " + name); return; }
    var path = Path.Combine(outDir, name + ".png");
    frame.Save(path);
    Console.WriteLine("saved " + path);
}

Pump(2500); // InitializeAsync: database, settings, tab restore
if (light) vm.IsDarkTheme = false;
Shot("01-welcome");
vm.IsWelcomeOpen = false;
Pump(100);
Shot("02-empty-first-tab");

Wait(vm.LoadSamplesAsync());
Pump(800);
Shot("03-samples-loaded");

var tab = vm.SelectedTab!;
tab.Url = "http://127.0.0.1:18777/users/42?expand=roles";
tab.Name = "Get user";
tab.Headers.BulkText = "Accept: application/json\nX-Trace: on";
Pump(100);
Shot("04-request-editor");
if (tab.SendCommand.CanExecute(null))
    Wait(tab.SendCommand.ExecuteAsync(null));
Pump(1200);
Shot("05-response");

vm.OpenPaletteCommand.Execute(null);
Pump(100);
Shot("06-palette");
vm.Palette.CloseCommand.Execute(null);

foreach (var kind in new[] { Dispatch.Domain.RequestKind.GraphQl, Dispatch.Domain.RequestKind.Grpc, Dispatch.Domain.RequestKind.WebSocket })
    vm.NewRequestCommand.Execute(kind);
Pump(300);
Shot("07-many-tabs-grpc");

vm.IsSideBySide = true;
Pump(300);
Shot("08-side-by-side");
vm.IsSideBySide = false;
Pump(100);

// The lifetime only tracks windows once it has been started, so ask the dialog service (via reflection) instead.
IEnumerable<Window> OpenToolWindows()
{
    var app = (App)Avalonia.Application.Current!;
    var services = (IServiceProvider)app.GetType().GetField("_services", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(app)!;
    var dialogs = services.GetService(typeof(Dispatch.App.Services.DialogService))!;
    var open = (System.Collections.IDictionary)dialogs.GetType().GetField("_open", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(dialogs)!;
    return open.Values.Cast<Window>().ToList();
}

void ToolShot(string name, Action open)
{
    open();
    Pump(600);
    var tool = OpenToolWindows().LastOrDefault();
    if (tool is null)
    {
        Console.WriteLine($"no tool window for {name}; error = {vm.ErrorMessage}");
        return;
    }
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    Dispatcher.UIThread.RunJobs();
    using var frame = tool.CaptureRenderedFrame();
    if (frame is not null) { var path = Path.Combine(outDir, name + ".png"); frame.Save(path); Console.WriteLine("saved " + path); }
    tool.Close();
    Pump(100);
}
ToolShot("09-runner", () => vm.OpenRunnerCommand.Execute(null));
ToolShot("10-help", () => vm.ShowHelpCommand.Execute(null));
ToolShot("11-loadtest", () => vm.OpenLoadTestCommand.Execute(null));
ToolShot("12-import", () => vm.OpenImportCommand.Execute(null));
ToolShot("13-mock", () => vm.OpenMockServerCommand.Execute(null));
ToolShot("14-capture", () => vm.OpenCaptureCommand.Execute(null));

listener.Stop();
Console.WriteLine("done");
