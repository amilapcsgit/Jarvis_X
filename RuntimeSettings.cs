using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace Jarvis;

static class RuntimePreferences
{
    static string FileName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis", "runtime-settings.json");
    internal static readonly (string Key, string Label, bool Folder)[] Fields =
    [
        ("JARVIS_OLLAMA_DESKTOP", "Ollama Desktop executable", false),
        ("JARVIS_OLLAMA", "Ollama CLI executable", false),
        ("JARVIS_OLLAMA_MODELS", "Managed Ollama models folder", true),
        ("JARVIS_BLENDER", "Blender executable", false),
        ("JARVIS_OPENCODE", "OpenCode executable", false),
        ("JARVIS_UVX", "uvx executable", false),
        ("JARVIS_LLAMA_SERVER", "llama.cpp server executable", false),
        ("JARVIS_INKLING_MODEL", "Inkling weights", false),
        ("JARVIS_INKLING_PROJECTOR", "Inkling projector", false),
        ("JARVIS_PINOKIO", "Pinokio executable", false),
        ("JARVIS_COMFYUI", "ComfyUI URL", false)
    ];
    internal static string Get(string key) => Environment.GetEnvironmentVariable(key) ?? "";
    internal static void Load()
    {
        try
        {
            if (!File.Exists(FileName)) return;
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FileName));
            if (values == null) return;
            foreach (var item in values) Environment.SetEnvironmentVariable(item.Key, item.Value);
        }
        catch (IOException) { }
        catch (JsonException) { }
    }
    internal static void Save(Dictionary<string, string> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FileName)!);
        File.WriteAllText(FileName + ".tmp", JsonSerializer.Serialize(values));
        File.Move(FileName + ".tmp", FileName, true);
        foreach (var item in values) Environment.SetEnvironmentVariable(item.Key, item.Value);
    }
}

sealed partial class Backend
{
    readonly SemaphoreSlim ollamaGate = new(1);
    Process? ownedOllama;
    internal static string OllamaPath(bool desktop) => RuntimePath(
        desktop ? "JARVIS_OLLAMA_DESKTOP" : "JARVIS_OLLAMA",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Ollama", desktop ? "ollama app.exe" : "ollama.exe"));

    internal void OpenOllamaDesktop()
    {
        Start(OllamaPath(true), [], visible: true);
    }

    internal async Task<bool> StopOwnedOllama(CancellationToken cancel)
    {
        await ollamaGate.WaitAsync(cancel);
        try
        {
            if (ownedOllama is not { HasExited: false } process) return false;
            process.Kill(true);
            await process.WaitForExitAsync(cancel);
            return true;
        }
        finally { ollamaGate.Release(); }
    }

    async Task<bool> OllamaReady(CancellationToken cancel)
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        probe.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var response = await local.GetAsync(Ollama + "/api/version", probe.Token);
            if (!response.IsSuccessStatusCode) return false;
            var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(probe.Token));
            return S(result?["version"]).Length > 0;
        }
        catch (HttpRequestException) { return false; }
        catch (JsonException) { return false; }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { return false; }
    }

    internal async Task EnsureOllama(CancellationToken cancel)
    {
        await ollamaGate.WaitAsync(cancel);
        try
        {
            if (await OllamaReady(cancel)) return;
            if (await Port(11434))
                throw new InvalidOperationException("Port 11434 is in use. Check which service owns it in Settings.");
            bool managed = RuntimePreferences.Get("JARVIS_OLLAMA_MODE") == "managed";
            string path = OllamaPath(!managed);
            if (!File.Exists(path)) throw new FileNotFoundException("Set the Ollama executable path in Jarvis Settings.", path);
            var environment = new Dictionary<string, string> { ["OLLAMA_HOST"] = "127.0.0.1:11434" };
            string models = RuntimePreferences.Get("JARVIS_OLLAMA_MODELS");
            if (managed && models.Length > 0) environment["OLLAMA_MODELS"] = models;
            if (ownedOllama is not { HasExited: false })
                ownedOllama = Start(path, managed ? ["serve"] : [], environment, visible: !managed);
            for (int attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(500, cancel);
                if (await OllamaReady(cancel)) return;
            }
            throw new InvalidOperationException("Ollama did not become ready on port 11434. Check the runtime logs in Settings.");
        }
        finally { ollamaGate.Release(); }
    }
}

public partial class MainWindow
{
    void OpenRuntimeSettings(object sender, RoutedEventArgs args) => PublishRuntimeSettings();

    void PublishRuntimeSettings() => Post("runtime-settings", new
    {
        fields = RuntimePreferences.Fields.Select(f => new { key = f.Key, label = f.Label, folder = f.Folder,
            value = f.Key == "JARVIS_OLLAMA_DESKTOP" ? Backend.OllamaPath(true)
                : f.Key == "JARVIS_OLLAMA" ? Backend.OllamaPath(false) : RuntimePreferences.Get(f.Key) }),
        managed = RuntimePreferences.Get("JARVIS_OLLAMA_MODE") == "managed"
    });

    async Task RuntimeCommand(string command, string value, string id)
    {
        try
        {
            switch (command)
            {
                case "runtime-add-knowledge":
                    var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Text and Markdown|*.txt;*.md", Multiselect = true };
                    if (picker.ShowDialog(this) != true) return;
                    Directory.CreateDirectory(KnowledgeFolder);
                    foreach (var source in picker.FileNames)
                    {
                        if (new FileInfo(source).Length > 256_000) throw new InvalidOperationException("Choose text documents under 256 KB.");
                        File.Copy(source, Path.Combine(KnowledgeFolder, Path.GetFileName(source)), true);
                    }
                    Post("runtime-result", "Knowledge files added.");
                    return;
                case "runtime-browse":
                    var field = RuntimePreferences.Fields.FirstOrDefault(f => f.Key == id);
                    if (field.Key == null || id == "JARVIS_COMFYUI") return;
                    string? path = null;
                    if (field.Folder)
                    {
                        var folder = new Microsoft.Win32.OpenFolderDialog();
                        if (folder.ShowDialog(this) == true) path = folder.FolderName;
                    }
                    else
                    {
                        var file = new Microsoft.Win32.OpenFileDialog();
                        if (file.ShowDialog(this) == true) path = file.FileName;
                    }
                    if (path != null) Post("runtime-path", new { key = id, path });
                    return;
                case "runtime-save":
                    if (run != null || modelPull != null) throw new InvalidOperationException("Wait for the current run or download.");
                    var values = JsonSerializer.Deserialize<Dictionary<string, string>>(value) ?? [];
                    var saved = new Dictionary<string, string>();
                    foreach (var entry in RuntimePreferences.Fields)
                    {
                        string candidate = values.GetValueOrDefault(entry.Key, "").Trim();
                        if (candidate.Length > 0 && entry.Key != "JARVIS_COMFYUI" &&
                            !(entry.Folder ? Directory.Exists(candidate) : File.Exists(candidate)))
                            throw new InvalidOperationException($"{entry.Label} does not exist.");
                        saved[entry.Key] = candidate;
                    }
                    saved["JARVIS_OLLAMA_MODE"] = values.GetValueOrDefault("JARVIS_OLLAMA_MODE") == "managed" ? "managed" : "desktop";
                    RuntimePreferences.Save(saved);
                    await backend.ReloadRuntimeSettings();
                    Post("runtime-result", "Runtime settings saved.");
                    return;
                case "runtime-ollama-desktop": backend.OpenOllamaDesktop(); break;
                case "runtime-ollama-start": await backend.EnsureOllama(lifetime.Token); break;
                case "runtime-ollama-restart":
                case "runtime-ollama-stop":
                    if (run != null || modelPull != null) throw new InvalidOperationException("Wait for the current run or download.");
                    if (!await backend.StopOwnedOllama(lifetime.Token))
                        throw new InvalidOperationException("No Jarvis-owned Ollama worker is running.");
                    if (command == "runtime-ollama-restart") await backend.EnsureOllama(lifetime.Token);
                    break;
                case "runtime-logs": Post("runtime-result", backend.ReadLogs()); return;
                case "runtime-pinokio-search":
                case "runtime-pinokio-registry":
                case "runtime-pinokio-install":
                case "runtime-pinokio-launch":
                    await PinokioCommand(command, value);
                    return;
                case "runtime-comfy-test":
                    if (!Uri.TryCreate(value.TrimEnd('/') + "/system_stats", UriKind.Absolute, out var url) ||
                        url.Scheme is not ("http" or "https") || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
                        throw new InvalidOperationException("Enter a local or LAN ComfyUI base URL.");
                    using (var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) })
                    {
                        var result = JsonNode.Parse(await client.GetStringAsync(url, lifetime.Token));
                        if (result?["system"] == null) throw new InvalidOperationException("ComfyUI system information was missing.");
                    }
                    break;
                default: return;
            }
            Post("runtime-result", "Action completed.");
        }
        catch (Exception error) { Post("runtime-result", error.Message); }
    }
}


public partial class MainWindow
{
    async Task PinokioCommand(string command, string value)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string configuration = Path.Combine(home, ".pinokio", "config.json");
        if (!File.Exists(configuration)) throw new InvalidOperationException("Open Pinokio once to initialize its local runtime.");
        string root = Backend.S(JsonNode.Parse(await File.ReadAllTextAsync(configuration, lifetime.Token))?["home"]);
        string entry = Path.Combine(root, "bin", "npm", "node_modules", "pterm", "index.js");
        if (!File.Exists(entry)) throw new InvalidOperationException("Pinokio's pterm entry point is missing.");
        string target = value.Trim();
        if ((command is "runtime-pinokio-install" or "runtime-pinokio-launch") &&
            (target.Length == 0 || target.StartsWith('-') || target.Any(char.IsControl)))
            throw new InvalidOperationException("Paste an exact recipe URL or app reference from Pinokio discovery.");
        if (command == "runtime-pinokio-install" &&
            (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0))
            throw new InvalidOperationException("Choose an HTTPS recipe URL from the registry results.");
        if (command == "runtime-pinokio-launch" &&
            !target.StartsWith("pinokio://127.0.0.1:", StringComparison.OrdinalIgnoreCase) &&
            !target.StartsWith("pinokio://localhost:", StringComparison.OrdinalIgnoreCase) &&
            (!Path.IsPathFullyQualified(target) || !Directory.Exists(target) ||
             !Path.GetFullPath(target).StartsWith(Path.Combine(root, "api") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Choose a local app reference or a folder inside the Pinokio apps directory.");
        if (command == "runtime-pinokio-install" && MessageBox.Show(this, $"Install this Pinokio recipe?\n\n{target}",
            "ComfyUI setup", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        string[] arguments = command switch
        {
            "runtime-pinokio-search" => ["search", "ComfyUI", "--mode", "balanced", "--min-match", "1", "--limit", "8"],
            "runtime-pinokio-registry" => ["registry", "search", "ComfyUI"],
            "runtime-pinokio-install" => ["download", target],
            _ => ["run", target, "--default", "run.js?mode=Default", "--default", "run.js", "--default", "install.js"]
        };
        var info = new ProcessStartInfo("node.exe") { WorkingDirectory = root, CreateNoWindow = true };
        info.ArgumentList.Add(entry);
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        var (worker, launcher) = backend.LaunchOwned(info);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cancel.CancelAfter(TimeSpan.FromMinutes(4));
        using var cancellation = cancel.Token.Register(() => { try { if (!worker.HasExited) worker.Kill(true); } catch (InvalidOperationException) { } });
        var stderr = launcher.StandardError.ReadToEndAsync(cancel.Token);
        string output = "";
        while (await launcher.StandardOutput.ReadLineAsync(cancel.Token) is { } line)
        {
            output += line + "\n";
            if (output.Length > 12000) output = output[^12000..];
            Post("runtime-result", output);
        }
        await launcher.WaitForExitAsync(cancel.Token);
        string errors = await stderr;
        if (launcher.ExitCode != 0)
            throw new InvalidOperationException(errors.Contains("ECONNREFUSED") ? "Pinokio control plane is unreachable. Open Pinokio and check its configured address." : "Pinokio command failed. Check the Pinokio app logs.");
        Post("runtime-result", output.Length > 0 ? output : "Pinokio action completed.");
    }
}
