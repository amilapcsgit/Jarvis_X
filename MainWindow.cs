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
    string? activeRunId;
    CancellationTokenSource? run;
    JsonNode? pending;
    nint focusHook;
    HwndSource? nativeWindow;
    bool hotkeyRegistered;
    string? session, attachment, lastImage;
    string agent = "chat";
    CancellationTokenSource metricsRun = new();
    bool inkling, ready, deciding, stopping, hardStopped, closing, allowClose, diagnostics;
    Task? webStartup;
    TaskCompletionSource<bool>? chatApproval;
    int viewEpoch;
    Window? diagnosticWindow;
    System.Windows.Controls.TextBox? consoleOutput;
    string status = "Starting local services…";
    string modelName => inkling ? "Inkling 2 Mini" : SelectedTag;
    string Route => "session/" + session;
    string ProjectFile => Path.Combine(backend.Home, "last-project-v2.txt");
    string SessionFile => Path.Combine(backend.Home, "last-session-v2.txt");
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    sealed record TimelineItem(string Tool, string State, string Target, string Arguments, string? Detail = null);

    public MainWindow()
    {
        InitializeComponent();
        string iconPath = Path.Combine(AppContext.BaseDirectory, "web", "assets", "jarvis.ico");
        if (File.Exists(iconPath))
        {
            using var iconStream = File.OpenRead(iconPath);
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(iconStream,
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        }
        focusChanged = ForegroundChanged;
        SourceInitialized += (_, _) =>
        {
            nativeWindow = PresentationSource.FromVisual(this) as HwndSource;
            if (nativeWindow == null) return;
            nativeWindow.AddHook(WindowMessage);
            hotkeyRegistered = RegisterHotKey(nativeWindow.Handle, 1, 0x4006, 0x7B);
            if (hotkeyRegistered)
                StopButton.ToolTip = "Kill Jarvis workers and revoke approvals. Unsaved work in Jarvis-started Blender can be lost. Ctrl+Shift+F12 also works.";
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
        _ = ServeDiagnostics(lifetime.Token);
        focusHook = DesktopTarget.Native.SetWinEventHook(3, 3, 0, focusChanged, 0, 0, 0);
        try
        {
            string settings = File.Exists(ProjectFile) ? ProjectFile : Path.Combine(backend.Home, "last-project.txt");
            string previous = File.Exists(settings) ? File.ReadAllText(settings) : "";
            if (Directory.Exists(previous)) backend.Project = previous;
            webStartup = OpenWeb();
            await webStartup;
            if (hardStopped) return;
            _ = CollectMetrics();
            session = File.Exists(SessionFile) ? File.ReadAllText(SessionFile).Trim() : null;
            ready = true;
            SetStatus("Workspace ready");
            if (File.Exists(ModelFile))
            {
                string savedModel = File.ReadAllText(ModelFile).Trim();
                if (Backend.IsLocalTag(savedModel)) preferredTag = savedModel;
            }
            PostState();
        }
        catch (Exception ex)
        {
            if (hardStopped) return;
            ready = Workspace.CoreWebView2 != null;
            WebError.Text = "Reactor workspace unavailable. " + ex.Message;
            SetStatus(ex.Message);
        }
        finally { PostState(); }
    }

    async Task OpenWeb()
    {
        var options = new CoreWebView2EnvironmentOptions("--disable-background-networking --disable-component-update --disable-sync");
        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(backend.Home, "webview-private-" + Environment.ProcessId), options);
        await Workspace.EnsureCoreWebView2Async(environment);
        if (hardStopped) return;
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
        if (hardStopped || Workspace.CoreWebView2?.Source != Page || allowClose) return;
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

    sealed record ChatGrant(string Chat, string Project, string Mode, string? Target);
    readonly Dictionary<string, ChatGrant> grants = new(StringComparer.Ordinal);
    ChatGrant? CurrentGrant => session != null && grants.TryGetValue(session, out var grant) ? grant : null;
    bool Covers(string mode, string project, string? target = null) => CurrentGrant is { } grant &&
        grant.Mode == mode && grant.Target == target &&
        string.Equals(grant.Project, Path.GetFullPath(project), StringComparison.OrdinalIgnoreCase);
    void RevokeChatGrant()
    {
        if (session != null) grants.Remove(session);
        if (fallbackActive || scope?.Agent is "desktop" or "blender") { scope?.Revoke(); run?.Cancel(); }
    }

    static readonly HashSet<string> BlenderActions = new(StringComparer.Ordinal)
    {
        "blender_build_house", "blender_look", "blender_get_scene_info", "blender_get_addon_status",
        "blender_execute_blender_code", "blender_save_project", "blender_put_parts"
    };
    static readonly HashSet<string> DesktopActions = new(StringComparer.Ordinal)
    {
        "windows_mcp_Snapshot", "windows_mcp_Screenshot", "windows_mcp_Click", "windows_mcp_Type", "windows_mcp_Shortcut",
        "windows_mcp_Windows", "windows_mcp_Focus", "windows_mcp_Scroll", "windows_mcp_Drag"
    };
    static readonly HashSet<string> BaselineBuild = new(StringComparer.Ordinal)
    {
        "git_status", "git_diff", "dotnet_build", "dotnet_test", "dotnet_run", "python_test", "npm_build", "npm_test", "npm_start"
    };
    static bool KnownFamily(string family) => BaselineBuild.Contains(family) || family is "dotnet_restore" or "python_pytest";
    RunJournal? journal;
    string? lastBuildPrompt, lastBuildImage, currentRunOutput;
    bool lastRunOk;
    JsonNode? recoveredPermissionInput;
    int autoAttempts;
    (string Id, string Arguments, bool Covered)? presentedApproval;
    DateTime nextAutoAttempt;
    RunJournal Journal => journal ??= new(backend.Home);
    bool BuildGrant => Covers("build", backend.Project);
    bool BlenderGrant => Covers("blender", backend.Project);
    bool fullDesktop = true;
    bool DesktopGrant => Covers("desktop", backend.Project, fullDesktop ? null : selected?.Id);
    void ClearGrants() => grants.Clear();
    const string BlenderPythonAction = "blender_execute_blender_code";

    bool IsCoveredAction(RunScope active, string action, string family) => active.Agent switch
    {
        "blender" => BlenderActions.Contains(action),
        "desktop" => DesktopActions.Contains(action),
        "build" => action == "build_run" && KnownFamily(family),
        _ => false
    };
    bool HasGrant(RunScope active, JsonNode request, string family) =>
        Covers(active.Agent, active.Project, active.Target?.Id) &&
        IsCoveredAction(active, Action(request), family);

    async Task<bool> ApproveChat(RunScope active, CancellationToken cancel)
    {
        if (active.Agent is not ("blender" or "desktop" or "build") ||
            Covers(active.Agent, active.Project, active.Target?.Id)) return true;
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        chatApproval = decision;
        Post("permission", new { id = "chat", action = "Allow this chat", target = active.Destination,
            arguments = "", fullInput = true, group = active.Agent, chatGrant = true });
        SetStatus("Waiting for chat approval");
        try
        {
            if (!await decision.Task.WaitAsync(cancel)) { SetStatus("Chat approval rejected"); return false; }
            cancel.ThrowIfCancellationRequested();
            if (hardStopped || session == null) return false;
            grants[session] = new(session, active.Project, active.Agent, active.Target?.Id);
            Journal.Write(active.Id, "authorization", state: "Approved", detail: "Chat grant for " + active.Agent);
            PostState();
            return true;
        }
        finally
        {
            chatApproval = null;
            Post("permission", null);
        }
    }

    void PostState() => Post("state", new
    {
        ready, status, agent, inkling, blenderGrant = BlenderGrant, desktopGrant = DesktopGrant, buildGrant = BuildGrant, acceptCodex, canContinueBlender = lastBuildPrompt != null, busy = run != null || stopping || refreshingModels, deciding,
        project = backend.Project, session, attachment = attachment == null ? null : Path.GetFileName(attachment),
        execution, fullDesktop, desktopPaused = scope?.Paused == true, target = selected?.Id, forceBlender = backend.OwnsBlender, console = diagnosticWindow != null
    });

    async Task AdmittedCommand(JsonNode? request)
    {
        if (request is not JsonObject) return;
        string command = S(request["command"]), value = S(request["value"]);
        if (hardStopped) return;
        if (command.StartsWith("runtime-", StringComparison.Ordinal))
        {
            await RuntimeCommand(command, value, S(request["id"]));
            return;
        }
        if (command == "ollama-desktop")
        {
            try { backend.OpenOllamaDesktop(); }
            catch (Exception error) { SetStatus(error.Message); }
            return;
        }
        if (command == "hello")
        {
            PostState();
            Post("visibility", new { minimized = WindowState == WindowState.Minimized });
            if (ready && agent != "chat") { await backend.Connect(); await LoadHistory(); ResetView(); await RefreshMessages(); }
            return;
        }
        if (command is "model" or "models-refresh" or "model-pull" or "model-cancel")
        { await ModelCommand(command, value); return; }
        if (command is "desktop-take-control" or "desktop-resume")
        {
            if (scope?.Agent == "desktop")
            {
                scope.Pause(command == "desktop-take-control");
                SetStatus(scope.Paused ? "Desktop paused · you have control" : "Desktop resumed · observe before input");
                if (!scope.Paused && pending != null)
                    await Decide(RequestId(pending), "granted");
            }
            return;
        }
        if (command == "revoke-grants") { RevokeChatGrant(); PostState(); return; }
        if (command == "codex-queue") { SetCodexQueue(value == "on"); return; }
        if (command == "stop") { modelPull?.Cancel(); await EmergencyStop(); return; }
        if (command == "diagnostics") { diagnostics = value == "on"; return; }
        if (command == "permission")
        {
            string id = S(request["id"]);
            if (id == "chat")
            {
                if (value is "workspace-session" or "reject") chatApproval?.TrySetResult(value == "workspace-session");
            }
            else await Decide(id, value);
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
            if (agent == "desktop" && selected != null && await FocusTarget(selected, lifetime.Token)
                && DesktopGrant && pending is { } requestToResume)
                await Decide(RequestId(requestToResume), "granted");
            return;
        }
        if (run != null || stopping || !ready) return;
        switch (command)
        {
            case "execution" when value is "auto" or "native" or "python": execution = value; PostState(); break;
            case "folder": await ChooseProject(); break;
            case "new": NewChat(); break;
            case "history": await SelectChat(value); break;
            case "attach": Attach(); break;
            case "detach": attachment = null; PostState(); break;
            case "send": await SendMessage(value); break;
            case "continue-blender" when lastBuildPrompt != null:
                if (agent != "blender") RevokeChatGrant();
                agent = "blender";
                attachment = lastBuildImage;
                string prompt = lastBuildPrompt;
                lastBuildPrompt = lastBuildImage = null;
                await SendMessage(prompt);
                break;
            case "diff": Post("diff", await backend.Diff()); break;
            case "desktop-scope":
                if (fullDesktop != (value == "full")) RevokeChatGrant();
                fullDesktop = value == "full";
                PostState();
                break;
            case "mode" when value is "chat" or "plan" or "build" or "blender" or "desktop":
                if (agent != value) RevokeChatGrant();
                agent = value;
                PostState();
                if (value == "desktop") await RefreshTargets();
                break;
            case "windows": await RefreshTargets(); break;
            case "target":
                if (selected?.Id != value && CurrentGrant?.Mode == "desktop") RevokeChatGrant();
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
        if (!string.Equals(backend.Project, folder.FolderName, StringComparison.OrdinalIgnoreCase))
        {
            ClearGrants();
            SetCodexQueue(false);
            lastBuildPrompt = lastBuildImage = null;
        }
        backend.Project = folder.FolderName;
        File.WriteAllText(ProjectFile, backend.Project);
        NewChat();
        await LoadHistory();
    }

    void NewChat()
    {
        if (CurrentGrant?.Mode == "desktop") RevokeChatGrant();
        session = null;
        File.Delete(SessionFile);
        ResetView();
        PostState();
    }

    async Task SelectChat(string id)
    {
        if (!historyIds.Contains(id)) return;
        if (session != id && CurrentGrant?.Mode == "desktop") RevokeChatGrant();
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
        Post("blender-output", new { output = (string?)null, checkpoint = (string?)null });
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

    static bool RepeatedDesktopFailure(JsonNode? messages, long started) =>
        Array(messages?["data"])
            .Where(entry => Number(entry?["time"]?["created"]) >= started)
            .SelectMany(entry => Array(entry?["content"]))
            .Where(part => S(part?["type"]) == "tool" && S(part?["name"]).StartsWith("windows_mcp_", StringComparison.Ordinal)
                && S(part?["state"]?["status"]) is "failed" or "error")
            .GroupBy(part => (S(part?["name"]), part?["state"]?["input"]?.ToJsonString()))
            .Any(group => group.Count() >= 2);

    async Task PrepareSession(string title, bool useInkling, string tag, CancellationToken cancel)
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
            providerID = useInkling ? "inkling" : "ollama", id = useInkling ? "inkling-2-mini" : tag
        } }, cancel);
    }

    async Task SendMessage(string value)
    {
        if (hardStopped || stopping || closing || run != null || refreshingModels) return;
        string text = value.Trim();
        if (text.Length == 0 || text.Length > 100_000) return;
        if (agent == "chat")
        {
            await SendBuiltIn(value, text);
            return;
        }
        DesktopTarget? target = agent == "desktop" && !fullDesktop ? selected : null;
        if (agent == "desktop" && !fullDesktop && (target == null || !target.IsAlive))
        { SetStatus("Choose an open window for Desktop mode."); return; }

        using var current = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        run = current;
        bool useInkling = inkling;
        string tag = SelectedTag;
        var active = new RunScope(backend.ScopeFile, backend.Project, target, agent, agent == "desktop" && fullDesktop);
        activeRunId = active.Id;
        if (activeCodexTask != null) activeCodexTask.Run = active.Id;
        long started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var inputReference = attachment;
        string? toolFailure = null;
        currentRunOutput = null;
        lastRunOk = false;
        if (agent == "build" && System.Text.RegularExpressions.Regex.IsMatch(text,
            @"(?i)\b(blender|3d|mesh|viewport)\b|\.blend\b"))
        { lastBuildPrompt = value; lastBuildImage = attachment; }
        Journal.Write(active.Id, "run", state: "Running", detail: $"{active.Agent} · {modelName}");
        SetStatus("Connecting to " + modelName + "…");
        try
        {
            OllamaModel? model = useInkling ? null : await backend.DescribeModel(tag, current.Token);
            if (model != null && attachment != null && !model.Vision)
                throw new InvalidOperationException($"{tag} cannot read images. Detach the image or choose a vision model.");
            if (model != null && (agent is "blender" or "build" or "plan") && !model.Tools && (agent != "blender" || execution == "native"))
                throw new InvalidOperationException($"{tag} cannot call tools. Choose a model with tool support.");
            if (model != null) await backend.EnsureInferenceModel(model, current.Token);
            await backend.Connect(model);
            current.Token.ThrowIfCancellationRequested();
            await PrepareSession(text[..Math.Min(text.Length, 56)], useInkling, model?.InferenceTag ?? tag, current.Token);
            if (!await ApproveChat(active, current.Token)) return;
            if (target != null && !await FocusTarget(target, current.Token))
            {
                if (!target.IsAlive)
                    throw new InvalidOperationException("The selected desktop window closed before dispatch.");
                Journal.Write(active.Id, "focus", state: "Paused", detail: "Awaiting the selected window before tool dispatch");
            }
            await backend.PrepareModel(useInkling, current.Token);
            if (agent == "blender") { SetStatus("Connecting to visible Blender…"); await backend.Blender(current.Token); }
            if (agent is "blender" or "build" || agent == "desktop" && model == null)
            {
                string connector = agent == "desktop" ? "windows_mcp" : agent;
                Journal.Write(active.Id, "connector startup", connector);
                await WaitMcp(connector, current.Token);
            }
            current.Token.ThrowIfCancellationRequested();
            scope = active;
            active.Publish();
            if (agent == "desktop" && model != null)
            {
                await RunDesktopPlanner(active, text, model, current.Token);
                return;
            }
            if (agent == "blender" && (execution == "python" || execution == "auto" && model != null &&
                !System.Text.RegularExpressions.Regex.IsMatch(text, @"(?i)\b(simple house|modern house|residential house)\b")))
            {
                await PythonFallback(active, text, model, current.Token, structured: execution == "auto");
                return;
            }
            if (agent == "blender")
            {
                text += "\nSelected project folder: " + active.Project;
                if (attachment != null) text += "\nThe attached image is the modeling reference. Inspect it before choosing geometry.";
            }
            if (target != null) text += "\nSelected window: " + target.Label;
            if (active.FullDesktop) text += "\nScope: full desktop session. Use Windows to discover apps, Focus to switch, then Snapshot before sending input.";
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
            JsonNode? finalMessages = null;
            for (int i = 0; ; i++)
            {
                current.Token.ThrowIfCancellationRequested();
                var approvals = await backend.Call(Route + "/permission", cancel: current.Token);
                var messages = await backend.Call(Route + "/message", cancel: current.Token);
                finalMessages = messages;
                await ObserveApproval(Array(approvals?["data"]).FirstOrDefault(), messages);
                toolFailure = await RefreshMessages(current.Token, started, messages);
                if (active.Agent == "desktop" && RepeatedDesktopFailure(messages, started))
                    throw new InvalidOperationException("Desktop stopped after the same action failed twice.");
                var sessions = (await backend.Call("session/active", cancel: current.Token))?["data"] as JsonObject;
                bool busy = sessions?.ContainsKey(session!) == true;
                seenActive |= busy;
                if (!busy && pending == null && (seenActive || i >= 10)) break;
                await Task.Delay(360, current.Token);
            }
            if (agent == "blender" && execution == "auto")
            {
                var parts = Array(finalMessages?["data"]).Where(entry => S(entry?["type"]) == "assistant"
                    && Number(entry?["time"]?["created"]) >= started).SelectMany(entry => Array(entry?["content"])).ToList();
                bool edited = parts.Any(part => S(part?["name"]) is "blender_execute_blender_code" or "blender_put_parts" or "blender_save_project" or "blender_build_house");
                var reply = string.Join("\n", parts.Where(part => S(part?["type"]) == "text").Select(part => S(part?["text"]))).Trim();
                bool proposed = reply.Contains("```python", StringComparison.OrdinalIgnoreCase) || reply.Contains("import bpy");
                bool unsupported = toolFailure?.Contains("tool", StringComparison.OrdinalIgnoreCase) == true &&
                    toolFailure.Contains("support", StringComparison.OrdinalIgnoreCase);
                if (!edited && (unsupported || proposed && !reply.EndsWith('?')))
                {
                    attachment = inputReference;
                    await PythonFallback(active, value, model, current.Token);
                    return;
                }
            }
            currentRunOutput = VerifiedOutput(active);
            lastRunOk = toolFailure == null || currentRunOutput != null;
            Journal.Write(active.Id, "run", state: lastRunOk ? "Completed" : "Failed", detail: currentRunOutput ?? toolFailure);
            if (toolFailure != null && !lastRunOk) SetStatus(toolFailure);
            else SetStatus("Ready · " + modelName);
        }
        catch (OperationCanceledException) { Journal.Write(active.Id, "run", state: "Interrupted"); }
        catch (Exception ex)
        {
            Journal.Write(active.Id, "run", state: "Failed", detail: ex.Message);
            if (!hardStopped) SetStatus(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(scope, active)) RevokeScope();
            pending = null;
            Post("permission", null);
            if (active.Agent == "blender")
            {
                string checkpoint = Path.Combine(backend.Home, "checkpoints", active.Id, "before.blend");
                bool checkpointExists = File.Exists(checkpoint);
                if (checkpointExists) Journal.Write(active.Id, "checkpoint", "blender", "Completed", checkpoint);
                Post("blender-output", new { output = currentRunOutput, checkpoint = checkpointExists ? checkpoint : null });
            }
            if (ReferenceEquals(run, current)) { run = null; activeRunId = null; }
            if (pendingModel is { } next && !hardStopped && !closing)
            {
                pendingModel = null;
                await ModelCommand("model", next);
            }
            if (activeCodexTask == null && !closing) PublishCodexTasks();
            PostState();
            if (activeCodexTask == null) StartNextCodexTask();
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
            if (recent)
            {
                modelEntries.Add(entry);
                string providerError = S(entry?["error"]?["message"]);
                if (providerError.Length > 0) failure ??= providerError;
            }
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
        if (scope is { } active)
            Journal.Write(active.Id, item.State == "Running" ? "execution" : "tool status",
                item.Tool, item.State, item.State is "Failed" or "Rejected" ? item.Detail : null);
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
    JsonNode? Input(JsonNode request)
    {
        var input = request["metadata"]?["input"];
        return input is JsonObject { Count: > 0 } ? input : recoveredPermissionInput ?? input;
    }
    string Arguments(JsonNode request) => Input(request)?.ToJsonString(Pretty)
        ?? string.Join(Environment.NewLine, Array(request["resources"]).Select(S));
    async Task ObserveApproval(JsonNode? request, JsonNode? messages)
    {
        bool same = RequestId(pending) == RequestId(request);
        if (same && (request == null || deciding || autoAttempts >= 2 || DateTime.UtcNow < nextAutoAttempt)) return;
        if (!same) { autoAttempts = 0; recoveredPermissionInput = null; }
        pending = request;
        if (request == null || scope == null)
        {
            Post("permission", null);
            return;
        }
        string action = Action(request);
        var candidates = Array(messages?["data"]).SelectMany(entry => Array(entry?["content"]))
            .Where(part => S(part?["type"]) == "tool" && S(part?["name"]) == action &&
                S(part?["state"]?["status"]) is "running" or "pending")
            .Where(part => PartId(part) is { } id && (!permissionParts.ContainsValue(id) || id == CallId(request))).ToArray();
        if (candidates.Length == 1)
        {
            permissionParts[RequestId(request)] = PartId(candidates[0])!;
            recoveredPermissionInput = candidates[0]?["state"]?["input"] ?? candidates[0]?["input"];
        }
        string arguments = Arguments(request);
        string target = scope.Destination;
        if (action == "blender_build_house" && Input(request) is not JsonObject { Count: > 0 })
            arguments = "Create one editable house with one window and one door.\nSave as Jarvis House.blend or the next unused numbered filename in this folder.";
        if (action == "blender_execute_blender_code" && S(Input(request)?["code"]) is { Length: > 0 } python)
            arguments = python;
        var active = scope;
        string family = S(Input(request)?["family"]);
        bool covered = HasGrant(active, request, family);
        bool automatic = covered && !active.Paused && (active.Target == null || active.Target.IsForeground);
        PutItem(CallId(request), new(action, automatic ? "Queued" : "Waiting for approval", target, arguments));
        if (automatic)
        {
            autoAttempts++;
            if (autoAttempts > 1) Journal.Write(active.Id, "retry", action, detail: "Approval reply retry");
            nextAutoAttempt = DateTime.UtcNow.AddSeconds(1);
            await Decide(RequestId(request), "granted");
            if (pending == null || autoAttempts < 2) return;
        }
        bool fullInput = (action != BlenderPythonAction || S(Input(request)?["code"]).Length != 0)
            && (action is not ("shell" or "bash") || arguments.Trim() is not ("" or "*"));
        nextAutoAttempt = DateTime.UtcNow.AddSeconds(1);
        var presentation = (RequestId(request), arguments, covered);
        if (presentedApproval != presentation)
        {
            presentedApproval = presentation;
            Post("permission", new { id = RequestId(request), action, target, arguments, fullInput, covered });
        }
        SetStatus(active.Target is { IsForeground: false } && DesktopGrant
            ? "Desktop paused · re-focus the selected window to resume" : "Waiting for approval · " + action);
    }

    async Task Decide(string id, string choice)
    {
        if (choice is not ("once" or "reject" or "granted") || deciding || pending == null ||
            id != RequestId(pending) || scope == null || run == null || stopping) return;
        var request = pending;
        var active = scope;
        var cancel = run.Token;
        var key = CallId(request);
        string action = Action(request);
        string family = S(Input(request)?["family"]);
        bool approve = choice != "reject";
        deciding = true;
        PostState();
        try
        {
            if (approve && choice == "once" && action == BlenderPythonAction && S(Input(request)?["code"]).Length == 0)
                throw new InvalidOperationException("OpenCode did not supply the full Python script. Reject this request.");
            if (approve && action is "shell" or "bash" && (Arguments(request).Trim() is "" or "*"))
                throw new InvalidOperationException("OpenCode did not supply the full shell command. Reject this request.");
            if (approve && active.Paused) return;
            if (approve && active.Target is { } target)
            {
                if (!target.IsAlive) { RevokeScope(); throw new InvalidOperationException("The selected window closed. Stop and select another window."); }
                if (!target.IsForeground)
                {
                    if (choice == "granted")
                    {
                        SetStatus("Desktop paused · refocus the selected window to resume.");
                        return;
                    }
                    if (DesktopTarget.Native.GetForegroundWindow() != new WindowInteropHelper(this).Handle ||
                        !await FocusTarget(target, cancel)) return;
                }
            }
            cancel.ThrowIfCancellationRequested();
            if (!ReferenceEquals(active, scope) || id != RequestId(pending)) return;
            if (choice == "granted" && !HasGrant(active, request, family)) return;
            string authorization = choice == "granted" ? "Approved by " + active.Agent + " chat grant" : choice;
            var item = timeline[key];
            PutItem(key, item with { State = approve ? "Approved" : "Rejected",
                Detail = authorization });
            try { await backend.Call(Route + "/permission/" + id + "/reply", new { decision = approve ? "once" : "reject" }, cancel); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Journal.Write(active.Id, "authorization", action, "Failed", ex.Message);
                PutItem(key, item with { Detail = "Could not send the decision. " + ex.Message });
                throw;
            }
            Journal.Write(active.Id, "authorization", action, approve ? "Approved" : "Rejected", authorization);
            if (approve) Journal.Write(active.Id, "dispatch", action, detail: family);
            if (RequestId(pending) == id) { pending = null; Post("permission", null); }
            if (!hardStopped) SetStatus(approve ? "Running · " + action : "Permission rejected · " + action);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!hardStopped) SetStatus(ex.Message); }
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
        stopping = hardStopped = true;
        ready = false;
        backend.BlockLaunches();
        modelPull?.Cancel();
        pendingModel = null;
        metricsRun.Cancel();
        string runId = activeRunId ?? "host";
        ClearGrants();
        SetCodexQueue(false);
        RevokeScope();
        run?.Cancel();
        chatApproval?.TrySetCanceled();
        pending = null;
        SetStatus("Emergency Stop · terminating Jarvis workers");
        Journal.Write(runId, "emergency stop", state: "Interrupted", detail: "Revoked all chat grants and disabled the queue");
        var workerStop = Task.Run(backend.StopExecution);
        try { if (webStartup != null) await webStartup.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        var webStop = StopPrivateWebView();
        var failures = (await workerStop).Concat(await webStop).ToArray();
        stopping = false;
        ResumeButton.Visibility = failures.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Journal.Write(runId, "worker termination", state: failures.Length == 0 ? "Completed" : "Failed",
            detail: failures.Length == 0 ? "No owned workers remain" : string.Join(", ", failures));
        SetStatus(failures.Length == 0 ? "Stopped · all Jarvis workers exited" : "Stop incomplete · " + string.Join(", ", failures));
        WebError.Text = failures.Length == 0 ? "Jarvis is stopped. Resume to start a fresh workspace." : status;
    }

    async Task<string[]> StopPrivateWebView()
    {
        try
        {
            var environment = Workspace.CoreWebView2?.Environment;
            if (environment != null)
                foreach (var info in environment.GetProcessInfos())
                {
                    try
                    {
                        if (webProcesses.Any(p => p.Id == info.ProcessId)) continue;
                        var process = Process.GetProcessById(info.ProcessId);
                        _ = process.Handle; // Hold identity through browser shutdown.
                        webProcesses.Add(process);
                    }
                    catch (ArgumentException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
        }
        catch (InvalidOperationException) { }
        Workspace.Visibility = Visibility.Hidden;
        Workspace.Dispose();
        WorkspaceHost.Children.Remove(Workspace);
        return await Task.Run(() =>
        {
            var survivors = new List<string>();
            foreach (var process in webProcesses.ToArray())
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        if (!process.WaitForExit(1500)) { survivors.Add($"{process.ProcessName} ({process.Id})"); continue; }
                    }
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception)
                {
                    survivors.Add($"WebView2 ({process.Id})");
                    continue;
                }
                process.Dispose();
                webProcesses.Remove(process);
            }
            return survivors.ToArray();
        });
    }

    readonly List<Process> webProcesses = [];

    async void Resume(object sender, RoutedEventArgs e)
    {
        if (!hardStopped || stopping || closing || run != null) return;
        ResumeButton.Visibility = Visibility.Collapsed;
        hardStopped = false;
        backend.ResumeLaunches();
        metricsRun.Dispose();
        metricsRun = new();
        Workspace = new Microsoft.Web.WebView2.Wpf.WebView2 { Visibility = Visibility.Hidden };
        WorkspaceHost.Children.Add(Workspace);
        try
        {
            webStartup = OpenWeb();
            await webStartup;
            if (hardStopped) return;
            await backend.Connect();
            if (hardStopped) return;
            ResetView();
            await LoadHistory();
            if (session != null) await RefreshMessages();
            ready = true;
            SetStatus("Ready · chat approval required");
        }
        catch (Exception ex) { if (!hardStopped) SetStatus(ex.Message); }
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
                if (hardStopped) { await Task.Delay(500, cancel); continue; }
                var metricsToken = metricsRun.Token;
                int[] web = Workspace.CoreWebView2?.Environment.GetProcessInfos()
                    .Select(process => process.ProcessId).ToArray() ?? [];
                bool logs = diagnostics || consoleOutput != null;
                var sample = await Task.Run(() => (sampler.Sample(backend.RuntimePids(), web, metricsToken),
                    logs ? string.Join(Environment.NewLine, Journal.Read(limit: 18).Select(item => item?.ToJsonString())) + Environment.NewLine + backend.ReadLogs() : null), cancel);
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
        if (!hardStopped) await EmergencyStop();
        SetCodexQueue(false);
        ClearGrants();
        lifetime.Cancel();
        metricsRun.Dispose();
        if (hotkeyRegistered && nativeWindow != null) UnregisterHotKey(nativeWindow.Handle, 1);
        if (focusHook != 0) DesktopTarget.Native.UnhookWinEvent(focusHook);
        await Task.Run(backend.Dispose);
        allowClose = true;
        Workspace.Dispose();
        _ = Dispatcher.BeginInvoke(Close);
    }
}
