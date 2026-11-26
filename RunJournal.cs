using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Jarvis;

sealed partial class RunJournal(string home)
{
    readonly object gate = new();
    const int Limit = 2 * 1024 * 1024;
    string FileName => Path.Combine(home, "runs.jsonl");

    [GeneratedRegex(@"(?im)(authorization\s*[:=]\s*)(?:bearer\s+|basic\s+)?\S+|(?:sk-[\w-]{12,}|gh[pousr]_[\w]{20,}|github_pat_[\w_]+)|((?:api[_ -]?key|token|password|secret)\s*[:=]\s*)[^\s,;]+")]
    private static partial Regex Secrets();

    public static string Excerpt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var argumentSuffix = value.IndexOf("Arguments provided:", StringComparison.OrdinalIgnoreCase);
        if (argumentSuffix >= 0) value = value[..argumentSuffix];
        value = Secrets().Replace(value, "$1$2[redacted]");
        value = Regex.Replace(value, @"(?i)data:image/[^,]+,[a-z0-9+/=]+", "[image]");
        return value.Length <= 1400 ? value : value[..1400] + " …";
    }

    public void Write(string run, string stage, string? tool = null, string? state = null, string? detail = null)
    {
        lock (gate)
        try
        {
            if (File.Exists(FileName) && new FileInfo(FileName).Length >= Limit)
                File.Move(FileName, FileName + ".1", true);
            File.AppendAllText(FileName, JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.UtcNow, run, stage, tool, state, detail = Excerpt(detail)
            }) + "\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public JsonNode?[] Read(string? run = null, bool errors = false, int limit = 35)
    {
        lock (gate)
        {
            var results = new Queue<JsonNode?>();
            foreach (string path in new[] { FileName + ".1", FileName })
            try
            {
                foreach (string line in File.ReadLines(path))
                {
                    JsonNode? entry;
                    try { entry = JsonNode.Parse(line); } catch (JsonException) { continue; }
                    if (run != null && Backend.S(entry?["run"]) != run) continue;
                    if (errors && Backend.S(entry?["state"]) is not ("Failed" or "Rejected" or "Interrupted")) continue;
                    if (entry is JsonObject data)
                        data["detail"] = Backend.S(data["stage"]) == "execution" && Backend.S(data["state"]) == "Completed"
                            ? "" : Excerpt(Backend.S(data["detail"]));
                    results.Enqueue(entry);
                    if (results.Count > limit) results.Dequeue();
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return results.ToArray();
        }
    }
}
