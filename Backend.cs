using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Jarvis;

sealed class Backend : IDisposable
{
    readonly HttpClient local = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
    readonly List<Process> owned = [];
    Process? opencode, ollama, inkling, blender;
    HttpClient? api;
    public string Home { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis");
    public string ScopeFile => Path.Combine(Home, "active-run.json");
    public string Project { get; set; }
    public int? BlenderPid => blender is { HasExited: false } ? blender.Id : null;
    public bool OwnsBlender => BlenderPid.HasValue;

    public int[] RuntimePids()
    {
        lock (owned) return owned.Where(process => !process.HasExited).Select(process => process.Id).ToArray();
    }

    public bool StopExecution()
    {
        try
        {
            if (opencode is not { HasExited: false } worker) return true;
            worker.Kill(true);
            return worker.WaitForExit(1500);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
        {
            return opencode?.HasExited == true;
        }
    }

    public void ForceStopBlender()
    {
        if (blender is { HasExited: false }) blender.Kill(true);
    }
    public Backend() { Project = Path.Combine(Home, "Projects"); Directory.CreateDirectory(Project); }
    public string Location => "?location%5Bdirectory%5D=" + Uri.EscapeDataString(Project);

    public async Task Connect()
    {
        if (api != null && opencode is { HasExited: false }) return;
        File.Delete(ScopeFile);
        var config = Path.Combine(Home, "config-v2");
        Directory.CreateDirectory(config);
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jarvis.opencode.json")!;
        var settings = JsonNode.Parse(source)!;
        settings["mcp"]!["servers"]!["blender"]!["command"]!.AsArray()[7] = Path.Combine(AppContext.BaseDirectory, "blender_mcp_offline.py");
        settings["mcp"]!["servers"]!["windows_mcp"]!["command"]!.AsArray()[7] = Path.Combine(AppContext.BaseDirectory, "desktop_mcp.py");
        File.WriteAllText(Path.Combine(config, "opencode.json"), settings.ToJsonString());
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var env = new Dictionary<string,string> {
            ["OPENCODE_SERVER_PASSWORD"] = password, ["OPENCODE_SERVER_USERNAME"] = "opencode",
            ["JARVIS_RUN_SCOPE"] = ScopeFile, ["ANONYMIZED_TELEMETRY"] = "false", ["OPENCODE_CONFIG_DIR"] = config, ["OPENCODE_DISABLE_PROJECT_CONFIG"] = "1",
            ["OPENCODE_DISABLE_CLAUDE_CODE"] = "1", ["OPENCODE_DISABLE_MODELS_FETCH"] = "1",
            ["XDG_CONFIG_HOME"] = Path.Combine(Home, "xdg-config"),
            ["XDG_DATA_HOME"] = Path.Combine(Home, "xdg-data"),
            ["XDG_STATE_HOME"] = Path.Combine(Home, "xdg-state"),
            ["XDG_CACHE_HOME"] = Path.Combine(Home, "xdg-cache")
        };
        api?.Dispose();
        api = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) {
            BaseAddress = new($"http://127.0.0.1:{port}/api/"), Timeout = TimeSpan.FromSeconds(40)
        };
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + password)));
        opencode = Start(RuntimePath("JARVIS_OPENCODE", @"D:\LocalAI\opencode\node_modules\@opencode\cli\bin\opencode.exe"),
            ["serve", "--hostname", "127.0.0.1", "--port", port.ToString()], env);
        for (int i = 0; i < 60 && !opencode.HasExited; i++)
        {
            try { await Call("info"); return; } catch (HttpRequestException) { await Task.Delay(300); }
        }
        throw new InvalidOperationException("OpenCode did not start. See Jarvis's local OpenCode log.");
    }

    public async Task Model(bool useInkling, CancellationToken cancel)
    {
        if (useInkling)
        {
            if (!await Alive("http://127.0.0.1:8080/health"))
            {
                if (ollama is { HasExited: false }) { ollama.Kill(true); await ollama.WaitForExitAsync(cancel); }
                else if (await Alive("http://127.0.0.1:11434/api/version"))
                {
                    var state = JsonNode.Parse(await local.GetStringAsync("http://127.0.0.1:11434/api/ps", cancel));
                    foreach (var model in Array(state?["models"]))
                    {
                        if (S(model?["name"]) != "qwen3.5-pilot:4b")
                            throw new InvalidOperationException("Unload the other running Ollama model before starting Inkling.");
                    }
                    await local.PostAsJsonAsync("http://127.0.0.1:11434/api/generate",
                        new { model = "qwen3.5-pilot:4b", keep_alive = 0 }, cancel);
                }
                if (inkling is null || inkling.HasExited)
                {
                    string weights = RuntimePath("JARVIS_INKLING_MODEL", @"D:\LocalAI\models\Inkling-2-Mini-UD-IQ4_XS.gguf");
                    string projector = RuntimePath("JARVIS_INKLING_PROJECTOR", @"D:\LocalAI\models\mmproj-F16.gguf");
                    if (!File.Exists(weights) || !File.Exists(projector)) throw new FileNotFoundException("Inkling weights or projector are missing.");
                    var cuda = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        @"Programs\Ollama\lib\ollama\cuda_v12");
                    inkling = Start(RuntimePath("JARVIS_LLAMA_SERVER", @"D:\LocalAI\llama\llama-server.exe"),
                        ["--model",weights,"--mmproj",projector,"--alias","inkling-2-mini","--host","127.0.0.1","--port","8080",
                         "--ctx-size","8192","--parallel","1","--fit","on","--jinja"],
                        new() { ["PATH"] = cuda + ";" + Environment.GetEnvironmentVariable("PATH") });
                }
                await Wait("http://127.0.0.1:8080/health", inkling, cancel);
            }
            return;
        }
        if (inkling is { HasExited: false }) { inkling.Kill(true); await inkling.WaitForExitAsync(cancel); }
        else if (await Alive("http://127.0.0.1:8080/health"))
            throw new InvalidOperationException("Stop the external Inkling server before using Qwen.");
        if (!await Alive("http://127.0.0.1:11434/api/version"))
        {
            if (ollama is null || ollama.HasExited)
                ollama = Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Programs\Ollama\ollama.exe"), ["serve"], new() { ["OLLAMA_NO_CLOUD"]="1", ["OLLAMA_HOST"]="127.0.0.1:11434" });
            await Wait("http://127.0.0.1:11434/api/version", ollama, cancel);
        }
        var models = JsonNode.Parse(await local.GetStringAsync("http://127.0.0.1:11434/api/tags", cancel));
        if (!Array(models?["models"]).Any(m => S(m?["name"]) == "qwen3.5-pilot:4b"))
            throw new InvalidOperationException("The local qwen3.5-pilot:4b tag is missing. Run Setup-Jarvis.ps1.");
    }

    public async Task Blender(CancellationToken cancel)
    {
        if (await Port(19876)) return;
        var root = AppContext.BaseDirectory;
        var script = Path.Combine(root, "blender_boot.py");
        if (!File.Exists(Path.Combine(root, "runtime", "blender-scripts", "addons", "blender_mcp.py")) || !File.Exists(script))
            throw new FileNotFoundException("Jarvis's Blender add-on is missing. Run Setup-Jarvis.ps1.");
        if (blender is null || blender.HasExited)
            blender = Start(RuntimePath("JARVIS_BLENDER", @"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe"),
                ["--factory-startup","--offline-mode","--python",script], new() { ["DISABLE_TELEMETRY"]="true", ["PYTHONUNBUFFERED"]="1" }, visible:true);
        for (int i = 0; i < 60 && !blender.HasExited; i++)
        {
            cancel.ThrowIfCancellationRequested();
            if (await Port(19876)) return;
            await Task.Delay(500, cancel);
        }
        throw new InvalidOperationException("Blender started without its loopback bridge. Check the Jarvis Blender setup.");
    }

    static async Task<bool> Port(int port)
    {
        try { using var socket = new TcpClient(); await socket.ConnectAsync(IPAddress.Loopback, port); return true; }
        catch (SocketException) { return false; }
    }
    async Task<bool> Alive(string url)
    {
        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); using var response = await local.GetAsync(url, timeout.Token); return response.IsSuccessStatusCode; }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { return false; }
    }
    async Task Wait(string url, Process? process, CancellationToken cancel)
    {
        for (int i = 0; i < 120 && process is { HasExited:false }; i++)
        {
            cancel.ThrowIfCancellationRequested();
            if (await Alive(url)) return;
            await Task.Delay(500, cancel);
        }
        throw new InvalidOperationException("The local model did not become ready.");
    }
    static string RuntimePath(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } path ? path : fallback;

    Process Start(string executable, string[] arguments, Dictionary<string,string>? environment = null, bool visible = false)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("Local dependency missing", executable);
        var info = new ProcessStartInfo(executable) { WorkingDirectory = Project, UseShellExecute = false, CreateNoWindow = !visible,
            RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        if (environment != null) foreach (var item in environment) info.Environment[item.Key] = item.Value;
        var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start " + executable);
        lock (owned) owned.Add(process);
        {
            var log = Path.Combine(Home, Path.GetFileNameWithoutExtension(executable) + ".log");
            void Log(string? line) {
                if (line == null || line.Contains("server password", StringComparison.OrdinalIgnoreCase)) return;
                lock (owned) try { File.AppendAllText(log, line + Environment.NewLine); } catch (IOException) { }
            }
            process.OutputDataReceived += (_, e) => Log(e.Data);
            process.ErrorDataReceived += (_, e) => Log(e.Data);
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
        }
        return process;
    }
    public async Task<JsonNode?> Call(string path, object? body = null, CancellationToken cancel = default)
    {
        if (api is null) throw new InvalidOperationException("OpenCode is disconnected.");
        using var response = body == null ? await api.GetAsync(path,cancel).ConfigureAwait(false) : await api.PostAsJsonAsync(path,body,cancel).ConfigureAwait(false);
        var value = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"OpenCode {(int)response.StatusCode}: {value}");
        return value.Length == 0 ? null : JsonNode.Parse(value);
    }
    public string ReadLogs()
    {
        var output = new StringBuilder();
        foreach (string name in new[] { "opencode", "blender", "ollama", "llama-server" })
        {
            var path = Path.Combine(Home, name + ".log");
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                file.Seek(Math.Max(0, file.Length - 5000), SeekOrigin.Begin);
                using var reader = new StreamReader(file);
                output.AppendLine($"── {name} ──").AppendLine(reader.ReadToEnd());
            }
            catch (IOException) { }
        }
        return output.ToString();
    }

    public async Task<string> Diff()
    {
        var info = new ProcessStartInfo("git.exe") { WorkingDirectory = Project, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] {"--no-pager", "diff", "--no-ext-diff", "--no-textconv", "HEAD", "--"}) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Git is unavailable.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(await error);
        return await output;
    }
    public static JsonArray Array(JsonNode? node) => node as JsonArray ?? new JsonArray();
    public static string S(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    public void Dispose()
    {
        foreach (var item in Enumerable.Reverse(owned))
        {
            try
            {
                if (!item.HasExited)
                {
                    if (item == blender) item.CloseMainWindow();
                    else item.Kill(true);
                }
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            item.Dispose();
        }
        api?.Dispose(); local.Dispose();
    }
}
