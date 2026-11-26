using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace Jarvis;

public partial class MainWindow
{
    string? preferredTag;
    string SelectedTag => preferredTag ?? Backend.DefaultModel;
    string ModelFile => Path.Combine(backend.Home, "ollama-selection.txt");
    CancellationTokenSource? modelPull;
    bool refreshingModels;
    string[] installedTags = [];
    string? pendingModel;
    string? modelError;
    OllamaModel? selectedInfo;

    void PublishModels() => Post("models", new
    {
        tags = installedTags, selected = inkling ? "inkling" : SelectedTag,
        vision = inkling || selectedInfo?.Vision == true,
        tools = inkling || selectedInfo?.Tools == true,
        known = inkling || selectedInfo != null, pending = pendingModel,
        inklingAvailable = backend.InklingConfigured,
        pulling = modelPull != null, refreshing = refreshingModels, error = modelError
    });

    async Task RefreshModels(CancellationToken cancel)
    {
        installedTags = await backend.InstalledModels(cancel);
        if (run == null && preferredTag == null && installedTags.Contains(Backend.PilotModel))
            preferredTag = Backend.PilotModel;
        selectedInfo = !inkling && installedTags.Contains(SelectedTag)
            ? await backend.DescribeModel(SelectedTag, cancel) : null;
    }

    async Task ModelCommand(string command, string value)
    {
        if (command == "model-cancel") { modelPull?.Cancel(); return; }
        if (command == "model" && (run != null || stopping || modelPull != null))
        {
            if (value == "inkling" || installedTags.Contains(value)) pendingModel = value;
            PublishModels();
            return;
        }
        if (refreshingModels || modelPull != null) return;
        using var token = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try
        {
            modelError = null;
            if (command == "model-pull")
            {
                modelPull = token;
                PublishModels();
                await backend.PullModel(value.Trim(), message => Post("model-progress", message), token.Token);
            }
            else
            {
                refreshingModels = true;
                PublishModels();
                PostState();
            }
            if (command == "model")
            {
                if (value == "inkling" && backend.InklingConfigured) inkling = true;
                else
                {
                    if (!installedTags.Contains(value))
                        throw new InvalidOperationException("Refresh and choose an installed local model.");
                    var info = await backend.DescribeModel(value, token.Token);
                    File.WriteAllText(ModelFile, info.Name);
                    preferredTag = info.Name;
                    inkling = false;
                    selectedInfo = info;
                }
                Post("model", Telemetry.Model(modelName, []));
                SetStatus("Ready · " + modelName);
            }
            else await RefreshModels(token.Token);
            if (command == "model-pull")
            {
                string requested = value.Trim();
                if (!requested.Contains(':')) requested += ":latest";
                if (!installedTags.Contains(requested, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Ollama did not list the downloaded tag as a local model.");
                Post("model-progress", "Download complete");
            }
        }
        catch (OperationCanceledException) { Post("model-progress", "Download canceled"); }
        catch (Exception ex)
        {
            modelError = ex is HttpRequestException ? "Ollama is unavailable. Start Ollama for Windows and retry." : ex.Message;
            if (command == "model-pull") Post("model-progress", "");
        }
        finally
        {
            refreshingModels = false;
            modelPull = null;
            PublishModels();
            PostState();
            if (pendingModel is { } next && run == null && !hardStopped && !closing && !stopping)
            {
                pendingModel = null;
                await ModelCommand("model", next);
            }
            StartNextCodexTask();
        }
    }
}
