using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jarvis;

sealed record OllamaModel(string Name, string InferenceTag, bool Vision, bool Tools, bool Thinking, int Context);

sealed partial class Backend
{
    const string Ollama = "http://127.0.0.1:11434";
    public const string PilotModel = "qwen3.5-pilot:4b";
    public const string DefaultModel = "qwen3.5:4b";
    const string ContextAliasPrefix = "jarvis-context-";

    readonly SemaphoreSlim connectGate = new(1);
    readonly Dictionary<string, OllamaModel> registeredModels = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsLocalTag(string tag) =>
        tag.Length <= 200 && !tag.StartsWith(ContextAliasPrefix, StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(tag, @"\A(?:[a-zA-Z0-9][a-zA-Z0-9_-]*/)?[a-zA-Z0-9][a-zA-Z0-9_.-]*(?::[a-zA-Z0-9][a-zA-Z0-9_.-]*)?\z")
        && !tag.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase)
        && !tag.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase);

    static bool IsRemote(JsonNode? model) =>
        !string.IsNullOrWhiteSpace(S(model?["remote_model"])) || !string.IsNullOrWhiteSpace(S(model?["remote_host"]));

    public async Task<JsonNode?> OllamaJson(string endpoint, CancellationToken cancel, object? payload = null)
    {
        using var response = payload == null
            ? await local.GetAsync(Ollama + endpoint, cancel)
            : await local.PostAsJsonAsync(Ollama + endpoint, payload, cancel);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama {endpoint} returned HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: cancel);
    }

    public async Task<string[]> InstalledModels(CancellationToken cancel)
    {
        var tags = await OllamaJson("/api/tags", cancel);
        return Array(tags?["models"]).Where(item => !IsRemote(item))
            .Select(item => S(item?["name"])).Where(IsLocalTag).ToArray();
    }

    public async Task<OllamaModel> DescribeModel(string tag, CancellationToken cancel)
    {
        if (!IsLocalTag(tag)) throw new InvalidOperationException("Choose a local Ollama model.");
        var info = await OllamaJson("/api/show", cancel, new { model = tag });
        if (IsRemote(info) || !string.IsNullOrEmpty(S(info?["details"]?["remote_model"])))
            throw new InvalidOperationException("Cloud models are disabled in Jarvis.");
        var capabilities = Array(info?["capabilities"]).Select(S).ToHashSet();
        if (!capabilities.Contains("completion"))
            throw new InvalidOperationException($"{tag} does not advertise chat completion.");
        var configured = Regex.Match(S(info?["parameters"]), @"(?m)^num_ctx\s+(\d+)");
        int cap = tag is PilotModel or DefaultModel ? 8192 : 4096;
        int configuredContext = int.TryParse(configured.Groups[1].Value, out int value) ? value : 0;
        int context = configuredContext > 0 ? Math.Min(cap, configuredContext) : cap;
        string inferenceTag = tag;
        if (configuredContext != context)
        {
            var models = await OllamaJson("/api/tags", cancel);
            var entry = Array(models?["models"]).FirstOrDefault(item => S(item?["name"]) == tag && !IsRemote(item));
            string digest = S(entry?["digest"]);
            if (digest.Length == 0) throw new InvalidOperationException($"{tag} is missing from local Ollama.");
            byte[] identity = SHA256.HashData(Encoding.UTF8.GetBytes($"{tag}:{digest}:{context}"));
            inferenceTag = ContextAliasPrefix + Convert.ToHexString(identity.AsSpan(0, 12)).ToLowerInvariant() + ":latest";
        }
        return new(tag, inferenceTag, capabilities.Contains("vision"), capabilities.Contains("tools"),
            capabilities.Contains("thinking"), context);
    }

    public async Task EnsureInferenceModel(OllamaModel model, CancellationToken cancel)
    {
        if (model.InferenceTag == model.Name) return;
        var tags = await OllamaJson("/api/tags", cancel);
        if (Array(tags?["models"]).Any(item => !IsRemote(item) && S(item?["name"]) == model.InferenceTag)) return;

        // Ollama's OpenAI-compatible endpoint uses the context set in the model manifest.
        // Creating an alias from a local model shares its weights and leaves the source tag intact.
        var outcome = await OllamaJson("/api/create", cancel, new
        {
            model = model.InferenceTag, from = model.Name, parameters = new { num_ctx = model.Context }, stream = false
        });
        if (S(outcome?["status"]) != "success")
            throw new InvalidOperationException($"Ollama could not prepare the {model.Context}-token context for {model.Name}.");
    }

    public async Task PullModel(string tag, Action<string> progress, CancellationToken cancel)
    {
        if (!IsLocalTag(tag)) throw new InvalidOperationException("Enter a public Ollama model tag, such as qwen3.5:4b.");
        using var request = new HttpRequestMessage(HttpMethod.Post, Ollama + "/api/pull")
        { Content = JsonContent.Create(new { model = tag, stream = true }) };
        using var response = await local.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        using var stream = await response.Content.ReadAsStreamAsync(cancel);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancel) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var value = JsonNode.Parse(line);
            string error = S(value?["error"]), status = S(value?["status"]);
            if (error.Length > 0) throw new InvalidOperationException(error);
            if (value?["total"]?.GetValue<long>() is > 0 and var total &&
                value?["completed"]?.GetValue<long>() is long completed)
                status += $" · {Math.Clamp(100.0 * completed / total, 0, 100):0}%";
            progress(status);
            if (status == "success") return;
        }
        throw new InvalidOperationException($"Ollama did not confirm the download (HTTP {(int)response.StatusCode}).");
    }

    static JsonNode ModelConfig(OllamaModel model) => JsonSerializer.SerializeToNode(new
    {
        name = model.Name,
        limit = new { context = model.Context, output = 2048 },
        capabilities = new { input = model.Vision ? new[] { "text", "image" } : new[] { "text" },
            output = new[] { "text" }, tools = model.Tools },
        body = model.Thinking ? new { think = false } : null
    }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull })!;

    async Task StopOpenCodeForModelChange()
    {
        if (opencode is not { HasExited: false }) return;
        opencode.Kill(true);
        await opencode.WaitForExitAsync();
    }
}
