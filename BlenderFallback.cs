using static Jarvis.Backend;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jarvis;

public partial class MainWindow
{
    string execution = "auto";
    bool fallbackActive;
    readonly Dictionary<string, (string Exchange, string? Reference)> blenderTurns = [];

    async Task PythonFallback(RunScope active, string brief, OllamaModel? model, CancellationToken cancel, bool structured = false)
    {
        var chat = session ?? throw new InvalidOperationException("Select a chat first.");
        var tag = model?.InferenceTag ?? "inkling-2-mini";
        var previous = blenderTurns.GetValueOrDefault(chat);
        var reference = attachment ?? previous.Reference;
        string input = Path.Combine(backend.Home, active.Id + ".json");
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("JARVIS_UVX") ?? "uvx.exe")
            { WorkingDirectory = active.Project, CreateNoWindow = true };
        foreach (string arg in new[] {"--offline","--python","3.14","--from","mcp-for-blender==2.3.1","python",
            Path.Combine(AppContext.BaseDirectory, "blender_mcp_offline.py"), "--fallback", input})
            info.ArgumentList.Add(arg);
        info.Environment["JARVIS_RUN_SCOPE"] = backend.ScopeFile;
        string? script = null;
        int edits = 0;
        string tool = structured ? "Blender parts" : "Blender Python";
        fallbackActive = true;
        try
        {
            await File.WriteAllTextAsync(input, JsonSerializer.Serialize(new { run = active.Id, brief, reference,
                model = tag, structured, collection = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(chat)))[..10], vision = model?.Vision ?? true, inkling, history = previous.Exchange ?? "" }), cancel);
            cancel.ThrowIfCancellationRequested();
            if (!BlenderGrant) throw new InvalidOperationException("Blender approval revoked.");
            var (worker, launcher) = backend.LaunchOwned(info);
            using var cancellation = cancel.Register(() => { try { if (!worker.HasExited) worker.Kill(true); } catch (InvalidOperationException) { } });
            var errors = launcher.StandardError.ReadToEndAsync();
            SetStatus($"{tag} · {(structured ? "Structured modeling" : "Python fallback")}");
            Post("sent", brief);
            attachment = null;
            PublishRow(active.Id + "-user", "You", brief);
            string? response = null;
            bool completed = false;

            while (await launcher.StandardOutput.ReadLineAsync(cancel) is { } line)
            {
                if (!line.StartsWith('{')) continue;
                var item = JsonNode.Parse(line);
                string kind = S(item?["kind"]), value = S(item?["value"]);
                if (kind == "script")
                {
                    script = value;
                    PutItem(active.Id + ++edits, new(tool, "Running", active.Project, script));
                }
                if (kind is "edited" or "repair" && script != null)
                {
                    PutItem(active.Id + edits, new(tool, kind == "edited" ? "Completed" : "Failed", active.Project, script, value));
                    script = null;
                }
                if (kind == "image") { lastImage = value; Post("image", value); }
                if (kind == "repair") Journal.Write(active.Id, "Python repair", state: "Failed", detail: value);
                if (kind is "done" or "question") { response = value; completed = kind == "done"; }
            }
            await launcher.WaitForExitAsync(cancel);
            if (launcher.ExitCode != 0 || response == null) throw new InvalidOperationException(RunJournal.Excerpt(await errors));
            currentRunOutput = VerifiedOutput(active);
            lastRunOk = completed;
            blenderTurns[chat] = (brief + "\n" + response, reference);
            PublishRow(active.Id + "-reply", "Jarvis", response);
            SetStatus(currentRunOutput != null ? $"{tag} · saved {currentRunOutput}" : completed ? "Completed · Blender scene updated" : "Waiting for your design answer");
            Journal.Write(active.Id, "Python fallback", state: lastRunOk ? "Completed" : "Waiting", detail: currentRunOutput);
        }
        catch (Exception error)
        {
            if (script != null) PutItem(active.Id + edits, new(tool, "Failed", active.Project, script, error.Message));
            throw;
        }
        finally { fallbackActive = false; File.Delete(input); }
    }
}
