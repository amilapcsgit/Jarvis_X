using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Jarvis;

sealed class WorkerJob : IDisposable
{
    readonly SafeFileHandle job = CreateJobObject(0, null);

    public WorkerJob()
    {
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (job.IsInvalid || !SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()))
        {
            var error = new Win32Exception();
            job.Dispose();
            throw error;
        }
    }

    public (Process Process, Process Launcher) Start(ProcessStartInfo requested)
    {
        var boot = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = requested.WorkingDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        boot.ArgumentList.Add("--jarvis-worker");
        foreach (var item in requested.Environment) boot.Environment[item.Key] = item.Value;
        var launcher = Process.Start(boot) ?? throw new InvalidOperationException("Could not start the worker supervisor.");
        try
        {
            if (!AssignProcessToJobObject(job, launcher.SafeHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not supervise the Jarvis worker.");
            var request = new Command(requested.FileName, requested.ArgumentList.ToArray(), requested.WorkingDirectory, requested.CreateNoWindow);
            launcher.StandardInput.WriteLine(JsonSerializer.Serialize(request));
            string? reply = launcher.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (reply == null || !int.TryParse(reply, out int pid))
                throw new InvalidOperationException(reply ?? "The Jarvis worker failed to start.");
            var process = Process.GetProcessById(pid);
            _ = process.Handle;
            launcher.StandardInput.Close();
            return (process, launcher);
        }
        catch
        {
            if (!launcher.HasExited) launcher.Kill(true);
            launcher.Dispose();
            throw;
        }
    }

    public string[] Terminate()
    {
        bool requested = TerminateJobObject(job, 1);
        for (int attempt = 0; attempt < 40; attempt++)
        {
            var remaining = Active();
            if (remaining.Length == 0) return [];
            if (!requested || attempt == 39) return remaining;
            Thread.Sleep(50);
        }
        return [];
    }

    string[] Active()
    {
        int size = 8 + IntPtr.Size * 4096;
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(job, 3, buffer, size, out _))
                return ["Worker enumeration unavailable"];
            int count = Marshal.ReadInt32(buffer, 4);
            var result = new List<string>();
            for (int index = 0; index < count; index++)
            {
                int pid = (int)Marshal.ReadIntPtr(buffer, 8 + index * IntPtr.Size);
                try { using var process = Process.GetProcessById(pid); if (!process.HasExited) result.Add($"{process.ProcessName} ({pid})"); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            return result.ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose() => job.Dispose();

    record Command(string Executable, string[] Arguments, string WorkingDirectory, bool Hidden);

    public static int Worker()
    {
        using var input = new StreamReader(Console.OpenStandardInput());
        using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        try
        {
            var command = JsonSerializer.Deserialize<Command>(input.ReadLine() ?? "");
            if (command == null) return 1;
            var info = new ProcessStartInfo(command.Executable)
            {
                WorkingDirectory = command.WorkingDirectory, UseShellExecute = false, CreateNoWindow = command.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
            using var child = Process.Start(info) ?? throw new InvalidOperationException("Worker process creation failed.");
            output.WriteLine(child.Id);
            using var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            child.OutputDataReceived += (_, e) => { if (e.Data != null) output.WriteLine(e.Data); };
            child.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.WriteLine(e.Data); };
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
            input.ReadLine(); // Keep the child handle alive until the host opens its own handle.
            child.WaitForExit();
            return child.ExitCode;
        }
        catch (Exception error)
        {
            output.WriteLine(error.Message.ReplaceLineEndings(" "));
            return 1;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveLimit;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimits info, int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryInformationJobObject(SafeFileHandle job, int type, nint buffer, int size, out int written);
}
