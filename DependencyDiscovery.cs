using System.IO;

namespace Jarvis;

static class DependencyDiscovery
{
    internal static void Apply()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string programs = Path.Combine(local, "Programs");
        string blender = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation");
        var blenderPaths = Directory.Exists(blender)
            ? Directory.EnumerateDirectories(blender).OrderDescending().Select(dir => Path.Combine(dir, "blender.exe"))
            : [];
        Find("JARVIS_BLENDER", "blender.exe", blenderPaths);
        Find("JARVIS_OLLAMA", "ollama.exe", [Path.Combine(programs, "Ollama", "ollama.exe")]);
        Find("JARVIS_OLLAMA_DESKTOP", "ollama app.exe", [Path.Combine(programs, "Ollama", "ollama app.exe")]);
        Find("JARVIS_UVX", "uvx.exe", [Path.Combine(user, ".local", "bin", "uvx.exe")]);
        Find("JARVIS_OPENCODE", "opencode.exe", [Path.Combine(local, "Jarvis", "runtimes", "OpenCode", "node_modules", "@opencode", "cli", "bin", "opencode.exe")]);
        Find("JARVIS_PINOKIO", "Pinokio.exe", [Path.Combine(programs, "Pinokio", "Pinokio.exe")]);
    }

    static void Find(string key, string executable, IEnumerable<string> candidates)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key))) return;
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var match = candidates.Concat(paths.Select(dir => Path.Combine(dir.Trim().Trim('"'), executable))).FirstOrDefault(File.Exists);
        if (match != null) Environment.SetEnvironmentVariable(key, match);
    }
}
