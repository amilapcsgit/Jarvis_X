using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Jarvis.Backend;

namespace Jarvis;

public partial class MainWindow
{
    readonly Dictionary<string, string> desktopTurns = new();

    async Task RunDesktopPlanner(RunScope active, string brief, OllamaModel model, CancellationToken cancel)
    {
        string chat = session ?? throw new InvalidOperationException("Select a desktop chat first.");
        string input = Path.Combine(backend.Home, active.Id + "-desktop.json");
        try
        {
            await File.WriteAllTextAsync(input, JsonSerializer.Serialize(new
            {
                run = active.Id, brief, model = model.InferenceTag, vision = model.Vision, history = desktopTurns.GetValueOrDefault(chat, "")
            }), cancel);
            var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("JARVIS_UVX") ?? "uvx.exe")
                { WorkingDirectory = active.Project, CreateNoWindow = true };
            foreach (string argument in new[] { "--offline", "--python", "3.14", "--from", "windows-mcp==0.9.0",
                "python", Path.Combine(AppContext.BaseDirectory, "desktop_mcp.py"), "--plan", input })
                info.ArgumentList.Add(argument);
            info.Environment["JARVIS_RUN_SCOPE"] = backend.ScopeFile;
            cancel.ThrowIfCancellationRequested();
            var (worker, launcher) = backend.LaunchOwned(info);
            using var cancellation = cancel.Register(() => { try { if (!worker.HasExited) worker.Kill(true); } catch (InvalidOperationException) { } });
            var stderr = launcher.StandardError.ReadToEndAsync();
            PublishRow(active.Id + "-user", "You", brief);
            Post("sent", brief);
            string action = "";
            string? outcome = null;
            int step = 0;
            string window = active.Destination;
            while (await launcher.StandardOutput.ReadLineAsync(cancel) is { } line)
            {
                if (!line.StartsWith('{')) continue;
                var message = JsonNode.Parse(line);
                string kind = S(message?["kind"]), value = S(message?["value"]);
                switch (kind)
                {
                    case "phase": if (!active.Paused) SetStatus(value); break;
                    case "image": lastImage = value; Post("image", value); break;
                    case "window": window = value.Length == 0 ? active.Destination : value; break;
                    case "action":
                        action = value;
                        SetStatus("Acting · " + action);
                        PutItem(active.Id + ++step, new(action, "Running", window, ""));
                        break;
                    case "result":
                    case "failed":
                        PutItem(active.Id + step, new(action, kind == "result" ? "Completed" : "Failed", window, "", value));
                        break;
                    case "done":
                    case "question": outcome = value; lastRunOk = kind == "done"; break;
                }
            }
            await launcher.WaitForExitAsync(cancel);
            if (launcher.ExitCode != 0 || outcome == null) throw new InvalidOperationException(RunJournal.Excerpt(await stderr));
            PublishRow(active.Id + "-reply", "Jarvis", outcome);
            desktopTurns[chat] = $"{brief}\n{outcome}"[..Math.Min(2400, brief.Length + outcome.Length + 1)];
            SetStatus(lastRunOk ? "Completed · Desktop" : "Desktop paused · review the result");
            Journal.Write(active.Id, "desktop", state: lastRunOk ? "Completed" : "Waiting");
        }
        finally { File.Delete(input); }
    }
}
