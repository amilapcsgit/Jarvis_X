using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Jarvis.Backend;

namespace Jarvis;

sealed partial class Backend
{
    readonly Dictionary<string, (Process Worker, string Address)> workers = [];

    async Task<string> BuiltInEndpoint(string filename, bool embedding, CancellationToken cancel)
    {
        var (worker, address) = workers.GetValueOrDefault(filename);
        if (worker is not { HasExited: false })
        {
            var root = Path.Combine(AppContext.BaseDirectory, "builtin");
            string weights = Path.Combine(root, filename);
            if (!File.Exists(weights))
                throw new FileNotFoundException("This installation is missing its bundled Gemma weights.", weights);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            address = $"http://127.0.0.1:{port}";
            var arguments = new List<string> { "--host", "127.0.0.1", "--port", port.ToString(), "-m", weights, "-ngl", "0", "-c", "2048" };
            if (embedding) arguments.Add("--embedding");
            worker = Start(Path.Combine(root, "llama-server.exe"), arguments.ToArray());
            workers[filename] = (worker, address);
        }

        for (int attempt = 0; attempt < 80; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            if (worker.HasExited) throw new InvalidOperationException("Bundled Gemma stopped during startup. Check Runtime logs.");
            try
            {
                using var health = await local.GetAsync(address + "/health", cancel);
                if (health.IsSuccessStatusCode) break;
            }
            catch (System.Net.Http.HttpRequestException) { }
            if (attempt == 79) throw new TimeoutException("Bundled Gemma is still unavailable.");
            await Task.Delay(250, cancel);
        }
        return address!;
    }

    sealed record EmbeddingIndex(string Identity, double[][] Vectors);

    internal async Task<string> RetrieveBuiltIn(string query, string[] passages, CancellationToken cancel)
    {
        string endpoint = await BuiltInEndpoint("embeddinggemma-2-Q8_0.gguf", true, cancel);
        string modelPath = Path.Combine(AppContext.BaseDirectory, "builtin", "embeddinggemma-2-Q8_0.gguf");
        var modelFile = new FileInfo(modelPath);
        string content = System.Text.Json.JsonSerializer.Serialize(passages);
        string identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{modelFile.Length}:{modelFile.LastWriteTimeUtc.Ticks}:{content}")));
        string cache = Path.Combine(Home, "knowledge-index.json");
        double[][]? documents = null;
        if (File.Exists(cache))
        {
            try
            {
                var saved = System.Text.Json.JsonSerializer.Deserialize<EmbeddingIndex>(await File.ReadAllTextAsync(cache, cancel));
                if (saved?.Identity == identity && saved.Vectors.Length == passages.Length) documents = saved.Vectors;
            }
            catch (System.Text.Json.JsonException) { }
        }
        if (documents == null)
        {
            documents = await Embed(passages.Select(p => "title: Jarvis Help | text: " + p).ToArray());
            await File.WriteAllTextAsync(cache + ".tmp", System.Text.Json.JsonSerializer.Serialize(new EmbeddingIndex(identity, documents)), cancel);
            File.Move(cache + ".tmp", cache, true);
        }
        var q = (await Embed(["task: search result | query: " + query]))[0];

        async Task<double[][]> Embed(string[] input)
        {
            using var response = await local.PostAsJsonAsync(endpoint + "/v1/embeddings", new { input }, cancel);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: cancel);
            var vectors = Array(result?["data"]).OrderBy(item => item?["index"]?.GetValue<int>())
                .Select(item => Array(item?["embedding"]).Select(v => v!.GetValue<double>()).ToArray()).ToArray();
            if (vectors.Length != input.Length || vectors.Any(v => v.Length == 0))
                throw new InvalidDataException("EmbeddingGemma returned an unexpected result.");
            return vectors;
        }
        var best = documents.Select((v, index) => (index,
            score: q.Zip(v, (x, y) => x * y).Sum() /
                Math.Sqrt(q.Sum(x => x * x) * v.Sum(x => x * x))))
            .OrderByDescending(item => item.score).First();
        return best.score >= 0.35 ? passages[best.index] : "";
    }

    internal async Task<string> BuiltInChat(string text, string source, CancellationToken cancel)
    {
        string address = await BuiltInEndpoint("gemma-3-270m-it-q4_k_m.gguf", false, cancel);
        string prompt = source.Length == 0 ? text :
            $"Answer briefly using the following Jarvis help passage. If it lacks the answer, say so.\n\n{source}\n\nQuestion: {text}";
        using var response = await local.PostAsJsonAsync(address + "/v1/chat/completions", new
        {
            messages = new[] { new { role = "user", content = prompt } }, max_tokens = 220, temperature = 0.2, stream = false
        }, cancel);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: cancel);
        string reply = S(result?["choices"]?[0]?["message"]?["content"]);
        return reply.Length > 0 ? reply : throw new InvalidOperationException("Gemma returned an empty reply.");
    }
}

public partial class MainWindow
{
    string KnowledgeFolder => Path.Combine(backend.Home, "knowledge");

    async Task<bool> TryBuiltInCommand(string draft, string text)
    {
        var model = Regex.Match(text, @"^(?:select|use|switch to) (?:model )?([^\s]+)$", RegexOptions.IgnoreCase);
        var download = Regex.Match(text, @"^(?:download|pull) (?:model )?([^\s]+)$", RegexOptions.IgnoreCase);
        bool list = Regex.IsMatch(text, @"^(?:list|refresh|show) (?:installed |local )?(?:ollama )?models[.!?]?$", RegexOptions.IgnoreCase);
        bool launch = Regex.IsMatch(text, @"^(?:open|start|launch) ollama(?: desktop)?[.!?]?$", RegexOptions.IgnoreCase);
        if (!list && !launch && !model.Success && !download.Success) return false;
        var id = Guid.NewGuid().ToString("N");
        try
        {
            string reply;
            if (launch)
            {
                backend.OpenOllamaDesktop();
                reply = "Ollama Desktop launch requested.";
            }
            else if (list)
            {
                await ModelCommand("models-refresh", "");
                reply = modelError ?? (installedTags.Length == 0 ? "No local models are installed." : string.Join("\n", installedTags));
            }
            else
            {
                string tag = (model.Success ? model : download).Groups[1].Value;
                if (!Backend.IsLocalTag(tag)) throw new InvalidOperationException("Enter an exact local Ollama tag.");
                if (System.Windows.MessageBox.Show(this, $"{(model.Success ? "Select" : "Download")} {tag}?",
                    "Jarvis model action", System.Windows.MessageBoxButton.YesNo) != System.Windows.MessageBoxResult.Yes)
                    return true;
                if (model.Success) await ModelCommand("models-refresh", "");
                await ModelCommand(model.Success ? "model" : "model-pull", tag);
                reply = modelError ?? (model.Success && SelectedTag != tag ? "That model was not selected." : $"Model action completed for {tag}.");
            }
            PublishRow(id + "-user", "You", text);
            PublishRow(id + "-reply", "Jarvis Built-in", reply);
            Post("sent", draft);
        }
        catch (Exception error) { if (!hardStopped) SetStatus(error.Message); }
        return true;
    }

    async Task SendBuiltIn(string draft, string text)
    {
        if (await TryBuiltInCommand(draft, text)) return;
        using var current = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        run = current;
        SetStatus("Jarvis Built-in · Gemma");
        try
        {
            string help = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "builtin", "JarvisHelp.md"), current.Token);
            var passages = help.Split("\n## ", StringSplitOptions.TrimEntries).ToList();
            if (Directory.Exists(KnowledgeFolder))
            {
                foreach (var file in Directory.EnumerateFiles(KnowledgeFolder).Take(32))
                {
                    if (new FileInfo(file).Length > 256_000) continue;
                    string document = await File.ReadAllTextAsync(file, current.Token);
                    foreach (string paragraph in document.Split("\n\n", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(50))
                        passages.Add(Path.GetFileName(file) + "\n" + paragraph[..Math.Min(1000, paragraph.Length)]);
                }
            }
            var words = Regex.Matches(text, @"[\p{L}]{4,}").Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase);
            var candidates = passages.Select(section => (section, hits: words.Count(word => section.Contains(word, StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(item => item.hits).Take(12).ToArray();
            string source = candidates.FirstOrDefault().hits > 0 ? candidates[0].section : "";
            try { source = await backend.RetrieveBuiltIn(text, candidates.Select(item => item.section).ToArray(), current.Token); }
            catch (Exception) when (!current.IsCancellationRequested) { }
            string reply = await backend.BuiltInChat(text, source, current.Token);
            string id = Guid.NewGuid().ToString("N");
            PublishRow(id + "-user", "You", text);
            Post("row", new { id = id + "-reply", author = "Jarvis Built-in", body = reply, source });
            Post("sent", draft);
            SetStatus("Ready · Jarvis Built-in");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!hardStopped) SetStatus(error.Message); }
        finally { run = null; PostState(); }
    }
}
