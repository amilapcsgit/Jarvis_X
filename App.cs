using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace Jarvis;

public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args is ["--jarvis-worker"]) { Shutdown(WorkerJob.Worker()); return; }
        instance = new Mutex(true, @"Local\Jarvis.Workspace", out bool first);
        if (!first) { Shutdown(); return; }
        LoadInstalledSettings();
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
    static void LoadInstalledSettings()
    {
        string config = Path.Combine(AppContext.BaseDirectory, "jarvis-install.json");
        if (!File.Exists(config)) return;
        var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(config));
        if (settings == null) return;
        foreach (string name in new[] { "JARVIS_OPENCODE", "JARVIS_BLENDER", "JARVIS_UVX" })
            if (settings.TryGetValue(name, out string? value)) Environment.SetEnvironmentVariable(name, value);
        if (settings.TryGetValue("JARVIS_UV_CACHE", out string? cache))
            Environment.SetEnvironmentVariable("UV_CACHE_DIR", cache);
        if (settings.TryGetValue("JARVIS_UV_PYTHON", out string? python))
            Environment.SetEnvironmentVariable("UV_PYTHON_INSTALL_DIR", python);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
