using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Jarvis.Backend;

namespace Jarvis;

public partial class MainWindow
{
    readonly Queue<CodexTask> codexQueue = new();
    readonly Dictionary<string, CodexTask> codexTasks = new(StringComparer.Ordinal);
    CodexTask? activeCodexTask;
    bool acceptCodex;

    sealed class CodexTask(string prompt, string? image, string project)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Prompt { get; } = prompt;
        public string? Image { get; } = image;
        public string Project { get; } = project;
        public string State { get; set; } = "Queued";
        public string? Output { get; set; }
        public string? Run { get; set; }
    }

    void PublishCodexTasks() => Post("codex-tasks", codexTasks.Values.TakeLast(5).Select(task => new { task.Id, task.State }).ToArray());

    void SetCodexQueue(bool enabled)
    {
        acceptCodex = enabled && ready && !closing && !stopping && !hardStopped;
        if (!acceptCodex)
            while (codexQueue.TryDequeue(out var queued)) queued.State = "Cancelled";
        PostState();
        PublishCodexTasks();
        if (acceptCodex) StartNextCodexTask();
    }

    void StartNextCodexTask()
    {
        if (!acceptCodex || !ready || closing || stopping || refreshingModels || pendingModel != null || run != null || activeCodexTask != null) return;
        if (!codexQueue.TryDequeue(out var next)) return;
        _ = RunCodexTask(next);
    }

    async Task RunCodexTask(CodexTask task)
    {
        activeCodexTask = task;
        task.State = "Running";
        PublishCodexTasks();
        try
        {
            NewChat();
            agent = "blender";
            attachment = task.Image == null ? null :
                ProjectImage(task.Project, Path.GetRelativePath(task.Project, task.Image));
            await SendMessage(task.Prompt);
            task.Output = currentRunOutput;
            task.State = !acceptCodex && !lastRunOk ? "Cancelled" : task.Output != null ? "Completed"
                : lastRunOk ? "Needs follow-up" : "Failed";
            Journal.Write(task.Run ?? task.Id, "Codex task", state: task.State, detail: task.Output);
        }
        catch (Exception ex)
        {
            task.State = "Failed";
            Journal.Write(task.Run ?? task.Id, "Codex task", state: "Failed", detail: ex.Message);
        }
        finally
        {
            activeCodexTask = null;
            PublishCodexTasks();
            PostState();
            StartNextCodexTask();
        }
    }

    string? VerifiedOutput(RunScope active)
    {
        try
        {
            var receipt = JsonNode.Parse(File.ReadAllText(Path.Combine(backend.Home, "verified-output.json")));
            if (S(receipt?["run"]) != active.Id) return null;
            string path = S(receipt?["output"]);
            string? parent = Path.GetDirectoryName(path);
            if (parent == null || !Path.TrimEndingDirectorySeparator(parent).Equals(
                Path.TrimEndingDirectorySeparator(active.Project), StringComparison.OrdinalIgnoreCase)) return null;
            if (!path.EndsWith(".blend", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
            Journal.Write(active.Id, "verification", "blender_save", "Completed", path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    static string? ProjectImage(string project, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Path.IsPathRooted(value) || value.Contains(':')) throw new InvalidOperationException("Use a project-relative image.");
        string path = Path.GetFullPath(value, project);
        string relative = Path.GetRelativePath(project, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Reference must stay in the selected project.");
        string part = project;
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar))
        {
            part = Path.Combine(part, segment);
            if (File.GetAttributes(part).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("The reference cannot traverse a symbolic link.");
        }
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > 10_000_000 || file.Extension.ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".webp"))
            throw new InvalidOperationException("Choose an existing PNG, JPEG or WebP under 10 MB.");
        return path;
    }

    async Task<object> ControlRequest(JsonNode? request, CancellationToken cancel)
    {
        switch (S(request?["method"]))
        {
            case "get_status":
                return new { ready, status = RunJournal.Excerpt(status), agent, model = modelName,
                    busy = run != null || stopping, stopped = hardStopped, acceptCodex, queued = codexQueue.Count,
                    runId = scope?.Id, blender = BlenderGrant, desktop = DesktopGrant, build = BuildGrant };
            case "get_run":
                string runId = S(request?["run_id"]);
                if (!Guid.TryParse(runId, out _)) return new { error = "Provide a run ID." };
                return new { events = await Task.Run(() => Journal.Read(runId), cancel) };
            case "get_recent_errors":
                return new { events = await Task.Run(() => Journal.Read(errors: true), cancel) };
            case "run_health_check":
                return hardStopped ? new { error = "Jarvis is stopped. Resume to run probes." } : await HealthChecks(cancel);
            case "get_task":
                return codexTasks.TryGetValue(S(request?["task_id"]), out var found)
                    ? new { found.Id, found.State, found.Run, found.Output } : new { error = "Unknown task ID." };
            case "submit_blender_task":
                if (!acceptCodex || closing || stopping) return new { error = "Codex submissions are disabled in Jarvis." };
                string prompt = S(request?["prompt"]).Trim();
                if (prompt.Length is < 1 or > 10_000 || codexQueue.Count >= 8) return new { error = "Prompt or queue limit exceeded." };
                string project = Path.GetFullPath(backend.Project);
                var task = new CodexTask(prompt, ProjectImage(project, S(request?["reference"])), project);
                if (codexTasks.Count >= 80)
                {
                    var oldest = codexTasks.Values.FirstOrDefault(item => item.State is not ("Queued" or "Running"));
                    if (oldest != null) codexTasks.Remove(oldest.Id);
                }
                codexTasks[task.Id] = task;
                codexQueue.Enqueue(task);
                PublishCodexTasks();
                Journal.Write(task.Id, "Codex task", state: "Queued");
                PostState();
                StartNextCodexTask();
                return new { task.Id, task.State };
            default: return new { error = "Unknown diagnostics method." };
        }
    }

    async Task ServeDiagnostics(CancellationToken cancel)
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream("Jarvis_X.Diagnostics.v1", PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancel).ConfigureAwait(false);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(65));
                var token = requestTimeout.Token;
                try
                {
                    var lengthBytes = new byte[4];
                    await pipe.ReadExactlyAsync(lengthBytes, token).ConfigureAwait(false);
                    int length = BitConverter.ToInt32(lengthBytes);
                    if (length is < 1 or > 64_000) continue;
                    var bytes = new byte[length];
                    await pipe.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                    object result;
                    try
                    {
                        var request = JsonNode.Parse(bytes);
                        result = await (await Dispatcher.InvokeAsync(() => ControlRequest(request, token))).ConfigureAwait(false);
                    }
                    catch (Exception ex) { result = new { error = RunJournal.Excerpt(ex.Message) }; }
                    var body = JsonSerializer.SerializeToUtf8Bytes(result);
                    await pipe.WriteAsync(BitConverter.GetBytes(body.Length), token).ConfigureAwait(false);
                    await pipe.WriteAsync(body, token).ConfigureAwait(false);
                }
                catch (IOException) { }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Journal.Write("host", "diagnostics", state: "Failed", detail: ex.Message);
        }
    }

    async Task<object> HealthChecks(CancellationToken cancel)
    {
        var code = Check("OpenCode", async token => { await backend.Call("info", cancel: token); return "Local server answered"; });
        var blender = Check("Blender", token => Probe(Backend.BlenderExecutable, ["--version"], token));
        var sdk = Check("bpy", token => Probe(Backend.SdkPython(backend.Home),
            [Path.Combine(AppContext.BaseDirectory, "sdk_probe.py")], token));
        bool useInkling = inkling;
        string tag = SelectedTag;
        var model = Check(modelName, async token =>
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
            using var response = await client.GetAsync(useInkling ? "http://127.0.0.1:8080/health" : "http://127.0.0.1:11434/api/tags", token);
            response.EnsureSuccessStatusCode();
            if (useInkling) return "Local model server answered";
            var info = await backend.DescribeModel(tag, token);
            var running = await backend.OllamaJson("/api/ps", token);
            var loaded = Array(running?["models"]).FirstOrDefault(item => S(item?["name"]) == info.InferenceTag || S(item?["name"]) == tag);
            return loaded == null ? $"{info.Name} is installed and idle" :
                $"{info.Name} · VRAM {loaded["size_vram"]} bytes of {loaded["size"]} · context {loaded["context_length"]}";
        });
        return new { checks = await Task.WhenAll(code, blender, sdk, model) };

        async Task<object> Check(string name, Func<CancellationToken, Task<string>> check)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(42));
            try { return new { name, ok = true, detail = RunJournal.Excerpt(await check(timeout.Token)) }; }
            catch (Exception ex) { return new { name, ok = false, detail = RunJournal.Excerpt(ex.Message) }; }
        }
    }

    async Task<string> Probe(string executable, string[] arguments, CancellationToken cancel)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("Dependency is missing.", executable);
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        var (process, launcher) = backend.LaunchOwned(info);
        Task<string> output = launcher.StandardOutput.ReadToEndAsync(cancel), errors = launcher.StandardError.ReadToEndAsync(cancel);
        try
        {
            await launcher.WaitForExitAsync(cancel);
            if (process.ExitCode != 0) throw new InvalidOperationException(await errors);
            return await output;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw;
        }
    }
}
