using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using static Jarvis.Backend;

namespace Jarvis;

public partial class MainWindow : Window
{
    const string Page = "https://reactor.example/index.html";
    readonly Backend backend = new();
    readonly CancellationTokenSource lifetime = new();
    readonly DesktopTarget.Native.WinEvent focusChanged;
    readonly Dictionary<string, TimelineItem> timeline = new();
    readonly Dictionary<string, string> rowVersions = new();
    readonly Dictionary<string, string> permissionParts = new();
    IReadOnlyList<DesktopTarget> targets = [];
    DesktopTarget? selected;
    RunScope? scope;
    CancellationTokenSource? run;
    JsonNode? pending;
    nint focusHook;
    HwndSource? nativeWindow;
    bool hotkeyRegistered;
    string? session, attachment, lastImage;
    string agent = "chat";
    bool inkling, ready, deciding, stopping, closing, allowClose, diagnostics;
    int viewEpoch;
    Window? diagnosticWindow;
    System.Windows.Controls.TextBox? consoleOutput;
    string status = "Starting local services…";
    string modelName => inkling ? "Inkling 2 Mini" : "Qwen 3.5 4B";
    string Route => "session/" + session;
    string ProjectFile => Path.Combine(backend.Home, "last-project-v2.txt");
    string SessionFile => Path.Combine(backend.Home, "last-session-v2.txt");
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    sealed record TimelineItem(string Tool, string State, string Target, string Arguments, string? Detail = null);

    public MainWindow()
    {
        InitializeComponent();
        focusChanged = ForegroundChanged;
        SourceInitialized += (_, _) =>
        {
            nativeWindow = PresentationSource.FromVisual(this) as HwndSource;
            if (nativeWindow == null) return;
            nativeWindow.AddHook(WindowMessage);
            hotkeyRegistered = RegisterHotKey(nativeWindow.Handle, 1, 0x4006, 0x7B);
            if (hotkeyRegistered)
                StopButton.ToolTip = "Interrupt the active run and release the desktop. Ctrl+Shift+F12 works from another app.";
        };
        Loaded += async (_, _) => await Start();
        Closing += ClosingWindow;
        StateChanged += (_, _) => Post("visibility", new { minimized = WindowState == WindowState.Minimized });
        PreviewKeyDown += async (_, args) =>
        {
            if (args.Key != Key.Escape || run == null) return;
            args.Handled = true;
            await EmergencyStop();
        };
    }

    nint WindowMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam == 1)
        {
            handled = true;
            _ = EmergencyStop();
        }
        return 0;
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(nint window, int id);

    async Task Start()
    {
        focusHook = DesktopTarget.Native.SetWinEventHook(3, 3, 0, focusChanged, 0, 0, 0);
        try
        {
            string settings = File.Exists(ProjectFile) ? ProjectFile : Path.Combine(backend.Home, "last-project.txt");
            string previous = File.Exists(settings) ? File.ReadAllText(settings) : "";
            if (Directory.Exists(previous)) backend.Project = previous;
            await OpenWeb();
            _ = CollectMetrics();
            SetStatus("Connecting to local OpenCode…");
            await backend.Connect();
            session = File.Exists(SessionFile) ? File.ReadAllText(SessionFile).Trim() : null;
            await LoadHistory();
            if (session != null) await RefreshMessages();
            ready = true;
            SetStatus("Workspace ready · Qwen starts on demand");
        }
        catch (Exception ex)
        {
            ready = Workspace.CoreWebView2 != null;
            WebError.Text = "Reactor workspace unavailable. " + ex.Message;
            SetStatus(ex.Message);
        }
        finally { PostState(); }
    }

    async Task OpenWeb()
    {
        var options = new CoreWebView2EnvironmentOptions("--disable-background-networking --disable-component-update --disable-sync");
        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(backend.Home, "webview"), options);
        await Workspace.EnsureCoreWebView2Async(environment);
        var web = Workspace.CoreWebView2;
        web.SetVirtualHostNameToFolderMapping("reactor.example", Path.Combine(AppContext.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.Deny);
        web.Settings.AreHostObjectsAllowed = false;
        web.Settings.AreDefaultScriptDialogsEnabled = false;
        web.Settings.AreDevToolsEnabled = false;
        web.Settings.AreDefaultContextMenusEnabled = false;
        web.NavigationStarting += (_, args) => args.Cancel = args.Uri != Page;
        web.FrameNavigationStarting += (_, args) => args.Cancel = true;
        web.NewWindowRequested += (_, args) => args.Handled = true;
        web.DownloadStarting += (_, args) => args.Cancel = true;
        web.WebMessageReceived += async (_, args) =>
        {
            if (args.Source != Page || closing) return;
            try { await AdmittedCommand(JsonNode.Parse(args.WebMessageAsJson)); }
            catch (Exception ex) { SetStatus(ex.Message); }
        };
        web.ProcessFailed += (_, args) =>
        {
            WebError.Text = "The Reactor panel stopped responding. " + args.ProcessFailedKind + ". Restart Jarvis to reload it.";
            if (args.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited)
                Workspace.Visibility = Visibility.Hidden;
        };
        Workspace.Visibility = Visibility.Visible;
        web.Navigate(Page);
    }

    void Post(string type, object? value)
    {
        if (Workspace.CoreWebView2?.Source != Page || allowClose) return;
        try { Workspace.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type, value })); }
        catch (InvalidOperationException) { }
    }

    void SetStatus(string value)
    {
        status = value;
        NativeStatus.Text = value.Split('\n', 2)[0].TrimEnd();
        NativeStatus.ToolTip = value;
        PostState();
    }

    string? blenderGrantProject, blenderPythonGrantProject;
    bool BlenderPythonGrant => string.Equals(blenderPythonGrantProject, backend.Project, StringComparison.OrdinalIgnoreCase);
    const string BlenderPythonAction = "blender_execute_blender_code";
    void ClearBlenderGrants() => blenderGrantProject = blenderPythonGrantProject = null;
    bool BlenderGrant => string.Equals(blenderGrantProject, backend.Project, StringComparison.OrdinalIgnoreCase);
    static bool SessionBlenderTool(string action) => action is "blender_build_house" or "blender_look";

    void PostState() => Post("state", new
    {
        ready, status, agent, inkling, blenderGrant = BlenderGrant, blenderPythonGrant = BlenderPythonGrant, busy = run != null || stopping, deciding,
        project = backend.Project, session, attachment = attachment == null ? null : Path.GetFileName(attachment),
        target = selected?.Id, forceBlender = backend.OwnsBlender, console = diagnosticWindow != null
    });

    async Task AdmittedCommand(JsonNode? request)
    {
        if (request is not JsonObject) return;
        string command = S(request["command"]), value = S(request["value"]);
        if (command == "hello")
        {
            PostState();
            Post("visibility", new { minimized = WindowState == WindowState.Minimized });
            if (ready) { await LoadHistory(); ResetView(); await RefreshMessages(); }
            return;
        }
        if (command == "revoke-blender") { ClearBlenderGrants(); PostState(); return; }
        if (command == "stop") { await EmergencyStop(); return; }
        if (command == "diagnostics") { diagnostics = value == "on"; return; }
        if (command == "permission")
        {
            await Decide(S(request["id"]), value);
            return;
        }
        if (command == "console") { ToggleConsole(value == "on"); return; }
        if (command == "show-blender")
        {
            if (backend.BlenderPid is int pid)
            {
                var windows = await Task.Run(DesktopTarget.Find, lifetime.Token);
                windows.FirstOrDefault(item => item.Pid == pid)?.Focus();
            }
            return;
        }
        if (command == "force-blender") { await ForceBlender(); return; }
        if (command == "focus")
        {
            if (agent == "desktop" && selected != null) await FocusTarget(selected, lifetime.Token);
            return;
        }
        if (run != null || stopping || !ready) return;
        switch (command)
        {
            case "folder": await ChooseProject(); break;
            case "new": NewChat(); break;
            case "history": await SelectChat(value); break;
            case "attach": Attach(); break;
            case "detach": attachment = null; PostState(); break;
            case "send": await SendMessage(value); break;
            case "diff": Post("diff", await backend.Diff()); break;
            case "mode" when value is "chat" or "plan" or "build" or "blender" or "desktop":
                agent = value;
                PostState();
                if (value == "desktop") await RefreshTargets();
                break;
            case "model" when value is "qwen" or "inkling":
                inkling = value == "inkling"; Post("model", Telemetry.Model(modelName, [])); PostState(); break;
            case "windows": await RefreshTargets(); break;
            case "target":
                selected = targets.FirstOrDefault(window => window.Id == value && window.IsAlive);
                PostState();
                break;
            case "launch": await Launch(value); break;
        }
    }

    async Task LoadHistory()
    {
        string project = backend.Project;
        var response = await backend.Call("session" + backend.Location, cancel: lifetime.Token);
        if (project != backend.Project) return;
        var items = Array(response?["data"]).Where(node =>
        {
            string folder = S(node?["location"]?["directory"]);
            if (folder.Length == 0) folder = S(node?["directory"]);
            return folder.Length == 0 || Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))
                .Equals(Path.TrimEndingDirectorySeparator(project), StringComparison.OrdinalIgnoreCase);
        }).Select(node => new { id = S(node?["id"]), title = S(node?["title"]) })
            .Where(item => item.title != "Jarvis smoke test").ToArray();
        if (session != null && !items.Any(x => x.id == session)) session = null;
        historyIds = items.Select(x => x.id).ToHashSet();
        Post("history", items);
        PostState();
    }
    HashSet<string> historyIds = [];

    async Task ChooseProject()
    {
        var folder = new OpenFolderDialog { InitialDirectory = backend.Project };
        if (folder.ShowDialog(this) != true) return;
        if (!string.Equals(backend.Project, folder.FolderName, StringComparison.OrdinalIgnoreCase)) ClearBlenderGrants();
        backend.Project = folder.FolderName;
        File.WriteAllText(ProjectFile, backend.Project);
        NewChat();
        await LoadHistory();
    }

    void NewChat()
    {
        session = null;
        File.Delete(SessionFile);
        ResetView();
        PostState();
    }

    async Task SelectChat(string id)
    {
        if (!historyIds.Contains(id)) return;
        session = id;
        File.WriteAllText(SessionFile, id);
        ResetView();
        PostState();
        await RefreshMessages();
    }

    void ResetView()
    {
        ++viewEpoch;
        timeline.Clear();
        permissionParts.Clear();
        rowVersions.Clear();
        lastImage = null;
        Post("reset", null);
    }

    void Attach()
    {
        var dialog = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.webp" };
        if (dialog.ShowDialog(this) != true) return;
        if (new FileInfo(dialog.FileName).Length > 10_000_000)
            throw new InvalidOperationException("Choose an image smaller than 10 MB.");
        attachment = dialog.FileName;
        PostState();
    }

    async Task RefreshTargets()
    {
        targets = await Task.Run(DesktopTarget.Find, lifetime.Token);
        if (selected != null) selected = targets.FirstOrDefault(target => target.Id == selected.Id);
        Post("windows", targets.Select(target => new { id = target.Id, title = target.Title, process = target.Process, pid = target.Pid, icon = target.Icon }));
        PostState();
    }

    async Task Launch(string value)
    {
        if (value == "blender")
        {
            SetStatus("Starting visible Blender…");
            await backend.Blender(lifetime.Token);
        }
        else
        {
            string? executable = value switch { "notepad" => "notepad.exe", "calculator" => "calc.exe", _ => null };
            if (value == "pick")
            {
                var picker = new OpenFileDialog { Filter = "Windows executable|*.exe", CheckFileExists = true };
                if (picker.ShowDialog(this) != true) return;
                executable = picker.FileName;
            }
            if (executable == null) return;
            using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        }
        await Task.Delay(500, lifetime.Token);
        await RefreshTargets();
        SetStatus("Choose the window to work on");
    }

    async Task<bool> FocusTarget(DesktopTarget target, CancellationToken cancel)
    {
        if (!target.Focus()) { SetStatus("The selected window closed. Refresh the list."); return false; }
        for (int i = 0; i < 8; i++)
        {
            if (target.IsForeground) return true;
            await Task.Delay(65, cancel);
        }
        SetStatus("Desktop paused · focus the selected window manually and take a fresh snapshot.");
        return false;
    }

    void ForegroundChanged(nint hook, uint kind, nint hwnd, int obj, int child, uint thread, uint time)
    {
        if (scope?.Target is not { } target || hwnd.ToInt64() == target.Handle || hwnd == new WindowInteropHelper(this).Handle) return;
        try
        {
            scope.InvalidateObservation();
            SetStatus("Desktop paused · another window has focus. Re-focus the target and observe again.");
        }
        catch (IOException) { run?.Cancel(); }
    }

    async Task PrepareSession(string title, CancellationToken cancel)
    {
        if (session == null)
        {
            var response = await backend.Call("session", new { location = new { directory = backend.Project }, title }, cancel);
            session = S(response?["data"]?["id"]);
            if (session.Length == 0) { session = null; throw new InvalidOperationException("OpenCode did not create a session."); }
            File.WriteAllText(SessionFile, session);
        }
        await backend.Call(Route + "/agent", new { agent }, cancel);
        await backend.Call(Route + "/model", new { model = new
        {
            providerID = inkling ? "inkling" : "ollama", id = inkling ? "inkling-2-mini" : "qwen3.5-pilot:4b"
        } }, cancel);
    }

    async Task SendMessage(string value)
    {
        string text = value.Trim();
        if (text.Length == 0 || text.Length > 100_000) return;
        DesktopTarget? target = agent == "desktop" ? selected : null;
        if (agent == "desktop" && (target == null || !target.IsAlive))
        { SetStatus("Choose an open window for Desktop mode."); return; }

        using var current = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        run = current;
        var active = new RunScope(backend.ScopeFile, backend.Project, target, agent);
        long started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string? toolFailure = null;
        SetStatus("Connecting to " + modelName + "…");
        try
        {
            if (target != null && !await FocusTarget(target, current.Token)) return;
            await backend.Connect().WaitAsync(current.Token);
            await backend.Model(inkling, current.Token);
            if (agent == "blender") { SetStatus("Connecting to visible Blender…"); await backend.Blender(current.Token); }
            if (agent is "desktop" or "blender") await WaitMcp(agent == "desktop" ? "windows_mcp" : "blender", current.Token);
            await PrepareSession(text[..Math.Min(text.Length, 56)], current.Token);
            current.Token.ThrowIfCancellationRequested();
            scope = active;
            active.Publish();
            if (agent == "blender")
            {
                text += "\nSelected project folder: " + active.Project;
                if (attachment != null) text += "\nThe attached image is the modeling reference. Inspect it before choosing geometry.";
            }
            if (target != null) text += "\nSelected window: " + target.Label;
            var files = new List<object>();
            if (attachment != null)
            {
                string ext = Path.GetExtension(attachment).ToLowerInvariant();
                string mime = ext == ".png" ? "image/png" : ext == ".webp" ? "image/webp" : "image/jpeg";
                files.Add(new { name = Path.GetFileName(attachment), uri = "data:" + mime + ";base64," +
                    Convert.ToBase64String(await File.ReadAllBytesAsync(attachment, current.Token)) });
            }
            started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await backend.Call(Route + "/prompt", new { text, files }, current.Token);
            attachment = null;
            Post("sent", value);
            Post("model", Telemetry.Model(modelName, []));
            SetStatus("Running locally · " + modelName);
            await LoadHistory();
            bool seenActive = false;
            for (int i = 0; ; i++)
            {
                current.Token.ThrowIfCancellationRequested();
                var approvals = await backend.Call(Route + "/permission", cancel: current.Token);
                var messages = await backend.Call(Route + "/message", cancel: current.Token);
                await ObserveApproval(Array(approvals?["data"]).FirstOrDefault(), messages);
                toolFailure = await RefreshMessages(current.Token, started, messages);
                var sessions = (await backend.Call("session/active", cancel: current.Token))?["data"] as JsonObject;
                bool busy = sessions?.ContainsKey(session!) == true;
                seenActive |= busy;
                if (!busy && pending == null && (seenActive || i >= 10)) break;
                await Task.Delay(360, current.Token);
            }
            if (toolFailure != null) SetStatus(toolFailure);
            else SetStatus("Ready · " + modelName);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!stopping) SetStatus(ex.Message); }
        finally
        {
            if (ReferenceEquals(scope, active)) RevokeScope();
            pending = null;
            Post("permission", null);
            if (ReferenceEquals(run, current)) run = null;
            PostState();
        }
    }

    async Task WaitMcp(string name, CancellationToken cancel)
    {
        string failure = "Waiting for its tool catalog.";
        bool reconnected = false;
        for (int i = 0; i < 40; i++)
        {
            var result = await backend.Call("mcp" + backend.Location, cancel: cancel);
            var server = Array(result?["data"]).FirstOrDefault(x => S(x?["name"]) == name);
            string state = S(server?["status"]?["status"]);
            Post("connector", new { name, state = state.Length == 0 ? "connecting" : state });
            if (state == "connected") return;
            if (state is "failed" or "disabled" or "disconnected")
            {
                failure = S(server?["status"]?["error"]);
                if (!reconnected)
                {
                    reconnected = true;
                    await backend.Call("experimental/mcp/" + name + "/connect" + backend.Location, new { }, cancel);
                }
            }
            await Task.Delay(350, cancel);
        }
        throw new InvalidOperationException(name + " unavailable. " + failure);
    }

    async Task<string?> RefreshMessages(CancellationToken cancel = default, long? started = null, JsonNode? messages = null)
    {
        if (session == null) return null;
        int epoch = viewEpoch;
        var response = messages ?? await backend.Call(Route + "/message", cancel: cancel);
        if (epoch != viewEpoch || closing) return null;
        var modelEntries = new List<JsonNode?>();
        string? failure = null, image = null;
        int ordinal = 0;
        foreach (var entry in Array(response?["data"]).Reverse())
        {
            string id = S(entry?["id"]);
            if (id.Length == 0) id = (++ordinal).ToString();
            var type = S(entry?["type"]);
            if (type == "user") PublishRow(id, "You", S(entry?["text"]));
            if (type != "assistant") continue;
            bool recent = started is long time && Number(entry?["time"]?["created"]) >= time;
            if (recent) modelEntries.Add(entry);
            int index = 0;
            foreach (var part in Array(entry?["content"]))
            {
                string key = id + "/" + index++;
                if (S(part?["type"]) == "text") PublishRow(key, "Jarvis", S(part?["text"]));
                if (S(part?["type"]) != "tool") continue;
                string name = S(part?["name"]);
                var state = part?["state"];
                string stateName = S(state?["status"]);
                key = PartId(part) ?? key;
                var error = state?["error"];
                string errorText = error is JsonObject ? S(error["message"]) : S(error);
                var toolContent = Array(state?["content"]);
                string result = string.Join("\n", toolContent.Select(item => S(item?["text"])).Where(line => line.Length > 0));
                bool failed = stateName is "error" or "failed" || result.StartsWith("Error executing tool", StringComparison.OrdinalIgnoreCase);
                foreach (var item in toolContent)
                {
                    string uri = S(item?["uri"]), data = S(item?["data"]);
                    if (uri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) image = uri;
                    else if (S(item?["type"]) == "image" && data.Length != 0) image = "data:image/png;base64," + data;
                }
                if (recent || started == null)
                {
                    string detail = errorText.Length == 0 ? result : errorText + (result.Length == 0 ? "" : "\n" + result);
                    bool refused = timeline.GetValueOrDefault(key)?.State == "Rejected" || IsPermissionError(detail);
                    string stateLabel = failed ? (refused ? "Rejected" : "Failed") : stateName switch
                    {
                        "completed" => "Completed", "running" => "Running", "pending" => "Queued", _ => stateName
                    };
                    if (pending != null && key == CallId(pending) && !failed && stateLabel != "Completed") continue;
                    if (started == null && run == null && stateLabel is "Running" or "Queued")
                        stateLabel = "Interrupted";
                    if (stateLabel == "Queued" && timeline.ContainsKey(key)) continue;
                    if (stateLabel.Length == 0) continue;
                    string arguments = (state?["input"] ?? part?["input"])?.ToJsonString(Pretty) ?? "";
                    PutItem(key, new(name, stateLabel, scope?.Destination ?? backend.Project,
                        arguments.Length == 0 ? timeline.GetValueOrDefault(key)?.Arguments ?? "" : arguments, detail));
                    if (recent && pending == null && name.StartsWith("blender_", StringComparison.Ordinal) && stateLabel is "Running" or "Completed" or "Failed")
                    {
                        var toolStatus = stateLabel + " · " + name;
                        if (status != toolStatus && (stateLabel == "Running" || status == "Running · " + name)) SetStatus(toolStatus);
                    }
                    if (failed && recent) failure = (refused ? "Permission rejected · " : "Execution failed · ") + name;
                }
            }
        }
        if (image != null && image != lastImage && image.Length <= 25_000_000)
        { lastImage = image; Post("image", image); }
        if (started != null) Post("model", Telemetry.Model(modelName, modelEntries));
        return failure;
    }

    void PublishRow(string id, string author, string body)
    {
        if (body.Length == 0 || rowVersions.GetValueOrDefault(id) == body) return;
        rowVersions[id] = body;
        Post("row", new { id, author, body });
    }
    static long? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;
    static bool IsPermissionError(string detail) =>
        detail.Contains("permission", StringComparison.OrdinalIgnoreCase) &&
        (detail.Contains("reject", StringComparison.OrdinalIgnoreCase) || detail.Contains("denied", StringComparison.OrdinalIgnoreCase));

    void PutItem(string id, TimelineItem item)
    {
        if (timeline.GetValueOrDefault(id) == item) return;
        timeline[id] = item;
        Post("activity", new { id, item.Tool, item.State, item.Target, item.Arguments, item.Detail });
    }

    static string Action(JsonNode? request) => S(request?["action"]);
    static string RequestId(JsonNode? request) => S(request?["id"]);
    string CallId(JsonNode request) => permissionParts.GetValueOrDefault(RequestId(request)) ?? RequestId(request);
    static string? PartId(JsonNode? part)
    {
        string id = S(part?["id"]);
        if (id.Length == 0) id = S(part?["toolCallID"]);
        if (id.Length == 0) id = S(part?["callID"]);
        return id.Length == 0 ? null : id;
    }
    static JsonNode? Input(JsonNode request) => request["metadata"]?["input"];
    static string Arguments(JsonNode request) => Input(request)?.ToJsonString(Pretty)
        ?? string.Join(Environment.NewLine, Array(request["resources"]).Select(S));
    static string GrantKey(JsonNode request)
    {
        string action = Action(request);
        return action is "shell" or "bash" or "blender_execute_blender_code" ? action + "\n" + Arguments(request) : action;
    }

    async Task ObserveApproval(JsonNode? request, JsonNode? messages)
    {
        if (RequestId(pending) == RequestId(request)) return;
        pending = request;
        if (request == null || scope == null)
        {
            Post("permission", null);
            return;
        }
        string action = Action(request);
        string arguments = Arguments(request);
        var candidates = Array(messages?["data"]).SelectMany(entry => Array(entry?["content"]))
            .Where(part => S(part?["type"]) == "tool" && S(part?["name"]) == action &&
                S(part?["state"]?["status"]) is "running" or "pending")
            .Select(PartId).OfType<string>().Where(id => !permissionParts.ContainsValue(id)).ToArray();
        if (candidates.Length == 1) permissionParts[RequestId(request)] = candidates[0];
        string target = scope.Destination;
        if (action == "blender_build_house" && Input(request) is not JsonObject { Count: > 0 })
            arguments = "Create one editable house with one window and one door.\nSave as Jarvis House.blend or the next unused numbered filename in this folder.";
        if (action == "blender_execute_blender_code" && S(Input(request)?["code"]) is { Length: > 0 } python)
            arguments = python;
        PutItem(CallId(request), new(action, "Waiting for approval", target, arguments));
        bool fullInput = action != "blender_execute_blender_code" || S(Input(request)?["code"]).Length != 0;
        if (action is "shell" or "bash") fullInput = arguments.Trim().Length != 0 && arguments.Trim() != "*";
        Post("permission", new { id = RequestId(request), action, target, arguments, fullInput, blenderSession = SessionBlenderTool(action), blenderPython = action == BlenderPythonAction });
        if (fullInput && (scope.Grants.Contains(GrantKey(request)) || BlenderGrant && SessionBlenderTool(action) || BlenderPythonGrant && action == BlenderPythonAction))
            await Decide(RequestId(request), "granted");
        else SetStatus("Waiting for approval · " + action);
    }

    async Task Decide(string id, string choice)
    {
        if (choice is not ("once" or "task" or "reject" or "granted" or "blender-session" or "blender-python-session") || deciding || pending == null ||
            id != RequestId(pending) || scope == null || run == null || stopping) return;
        var request = pending;
        var active = scope;
        var cancel = run.Token;
        var key = CallId(request);
        string action = Action(request);
        if (choice == "blender-session" && !SessionBlenderTool(action)) return;
        if (choice == "blender-python-session" && (action != BlenderPythonAction || scope.Agent != "blender")) return;
        bool approve = choice != "reject";
        deciding = true;
        PostState();
        try
        {
            if (approve && action == "blender_execute_blender_code" && S(Input(request)?["code"]).Length == 0)
                throw new InvalidOperationException("OpenCode did not supply the full Python script. Reject this request.");
            if (approve && action is "shell" or "bash" && (Arguments(request).Trim() is "" or "*"))
                throw new InvalidOperationException("OpenCode did not supply the full shell command. Reject this request.");
            if (approve && active.Target is { } target)
            {
                if (!target.IsAlive) { RevokeScope(); throw new InvalidOperationException("The selected window closed. Stop and select another window."); }
                if (!target.IsForeground)
                {
                    if (choice == "granted")
                    {
                        SetStatus("Desktop paused · focus the selected window and click Approve once to resume.");
                        return;
                    }
                    if (DesktopTarget.Native.GetForegroundWindow() != new WindowInteropHelper(this).Handle ||
                        !await FocusTarget(target, cancel)) return;
                }
            }
            cancel.ThrowIfCancellationRequested();
            if (!ReferenceEquals(active, scope) || id != RequestId(pending)) return;
            if (choice == "granted" && !(active.Grants.Contains(GrantKey(request)) ||
                BlenderGrant && SessionBlenderTool(action) || BlenderPythonGrant && action == BlenderPythonAction)) return;
            var item = timeline[key];
            PutItem(key, item with { State = approve ? "Approved" : "Rejected",
                Detail = choice is "task" or "granted" ? "Approval scoped to this run." : null });
            try { await backend.Call(Route + "/permission/" + id + "/reply", new { decision = approve ? "once" : "reject" }, cancel); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PutItem(key, item with { Detail = "Could not send the decision. " + ex.Message });
                throw;
            }
            if (choice == "blender-session" && ReferenceEquals(scope, active)) blenderGrantProject = backend.Project;
            if (choice == "blender-python-session" && ReferenceEquals(scope, active))
            {
                blenderPythonGrantProject = active.Project;
                blenderGrantProject = active.Project;
            }
            if (choice == "task" && ReferenceEquals(scope, active)) active.Grants.Add(GrantKey(request));
            if (RequestId(pending) == id) { pending = null; Post("permission", null); }
            if (!stopping) SetStatus(approve ? "Running · " + action : "Permission rejected · " + action);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!stopping) SetStatus(ex.Message); }
        finally { deciding = false; PostState(); }
    }

    void RevokeScope()
    {
        var active = scope;
        scope = null;
        if (active == null) return;
        try { active.Revoke(); }
        catch (IOException) { _ = Task.Run(backend.StopExecution); }
    }

    async void Stop(object sender, RoutedEventArgs e) => await EmergencyStop();

    async Task EmergencyStop()
    {
        if (stopping) return;
        stopping = true;
        ClearBlenderGrants();
        bool active = run != null;
        string? route = session == null ? null : Route;
        RevokeScope();
        run?.Cancel();
        if (pending is { } request && timeline.TryGetValue(CallId(request), out var item))
            PutItem(CallId(request), item with { State = "Rejected", Detail = "Emergency Stop cleared this request." });
        pending = null;
        foreach (var entry in timeline.Where(entry => entry.Value.State is "Running" or "Approved" or "Queued").ToArray())
        {
            string detail = entry.Value.Tool.StartsWith("blender_", StringComparison.Ordinal)
                ? "Emergency Stop detached Jarvis. An operation already sent to Blender may finish in the open window."
                : "Emergency Stop interrupted the run.";
            PutItem(entry.Key, entry.Value with { State = "Interrupted", Detail = detail });
        }
        Post("permission", null);
        SetStatus("Emergency Stop · releasing the run");
        try
        {
            if (active && route != null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await backend.Call(route + "/interrupt", new { }, timeout.Token);
            }
        }
        catch (Exception) { }
        finally
        {
            bool terminated = !active || await Task.Run(backend.StopExecution);
            Post("connector", new { name = "windows_mcp", state = "released" });
            Post("connector", new { name = "blender", state = "detached" });
            stopping = false;
            SetStatus(terminated ? "Stopped · desktop released · Blender kept open" : "Desktop released · worker shutdown could not be confirmed");
        }
    }

    async Task ForceBlender()
    {
        if (!backend.OwnsBlender) return;
        if (MessageBox.Show(this, "Force Stop the Blender instance started by Jarvis? Any unsaved work in that instance will be lost.",
            "Force Stop Blender", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (run != null) await EmergencyStop();
        backend.ForceStopBlender();
        Post("connector", new { name = "blender", state = "stopped" });
        SetStatus("Jarvis-owned Blender stopped");
    }

    void ToggleConsole(bool visible)
    {
        if (!visible) { diagnosticWindow?.Close(); return; }
        if (diagnosticWindow != null) { diagnosticWindow.Activate(); return; }
        consoleOutput = new System.Windows.Controls.TextBox
        {
            IsReadOnly = true, FontFamily = new("Consolas"), FontSize = 12,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(8, 19, 30)),
            Foreground = System.Windows.Media.Brushes.White, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            Padding = new Thickness(12), Text = "Waiting for local runtime output…"
        };
        diagnosticWindow = new Window
        {
            Title = "Jarvis runtime console", Owner = this, Content = consoleOutput,
            Width = 780, Height = 450, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        diagnosticWindow.Closed += (_, _) => { diagnosticWindow = null; consoleOutput = null; PostState(); };
        diagnosticWindow.Show();
        PostState();
    }

    async Task CollectMetrics()
    {
        var cancel = lifetime.Token;
        try
        {
            using var sampler = await Task.Run(() => new Telemetry(), cancel);
            while (!cancel.IsCancellationRequested)
            {
                int[] web = Workspace.CoreWebView2?.Environment.GetProcessInfos()
                    .Select(process => process.ProcessId).ToArray() ?? [];
                bool logs = diagnostics || consoleOutput != null;
                var sample = await Task.Run(() => (sampler.Sample(backend.RuntimePids(), web, cancel),
                    logs ? backend.ReadLogs() : null), cancel);
                if (cancel.IsCancellationRequested) break;
                Post("metrics", sample.Item1);
                if (sample.Item2 != null)
                {
                    if (diagnostics) Post("logs", sample.Item2);
                    if (consoleOutput is { } output && output.Text != sample.Item2)
                    {
                        output.Text = sample.Item2;
                        output.ScrollToEnd();
                    }
                }
                await Task.Delay(1800, cancel);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Post("metrics-error", ex.Message); }
    }

    async void ClosingWindow(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        if (run != null) await EmergencyStop();
        lifetime.Cancel();
        if (hotkeyRegistered && nativeWindow != null) UnregisterHotKey(nativeWindow.Handle, 1);
        if (focusHook != 0) DesktopTarget.Native.UnhookWinEvent(focusHook);
        await Task.Run(backend.Dispose);
        allowClose = true;
        Workspace.Dispose();
        _ = Dispatcher.BeginInvoke(Close);
    }
}
