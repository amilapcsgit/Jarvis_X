using System.Diagnostics;
using System.Reflection;

namespace JarvisInstaller;

static class Program
{
    [STAThread]
    static int Main()
    {
        using var mutex = new Mutex(true, @"Local\Jarvis_X.Setup", out bool first);
        if (!first) return 1;
        var scratch = Directory.CreateTempSubdirectory("JarvisSetup-");
        try
        {
            string script = Extract("install.ps1");
            string payload = Extract("payload.zip");
            var info = new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
            { UseShellExecute = false, WorkingDirectory = scratch.FullName };
            foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-STA", "-ExecutionPolicy", "RemoteSigned",
                "-File", script, "-Payload", payload })
                info.ArgumentList.Add(argument);
            using var installer = Process.Start(info) ?? throw new InvalidOperationException("Windows PowerShell did not start.");
            installer.WaitForExit();
            return installer.ExitCode;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Jarvis_X Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            try { scratch.Delete(true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        string Extract(string name)
        {
            using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"The installer is missing {name}.");
            string path = Path.Combine(scratch.FullName, name);
            using var output = File.Create(path);
            source.CopyTo(output);
            return path;
        }
    }
}
