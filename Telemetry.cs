using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Jarvis;

sealed class Telemetry : IDisposable
{
    readonly Dictionary<(int Pid, long Birth), (double Cpu, ulong? Writes)> previous = [];
    (ulong Idle, ulong Total)? cpu;
    long stamp;
    nint diskQuery, diskCounter, gpuCounter;

    public Telemetry()
    {
        if (PdhOpenQuery(null, 0, out diskQuery) != 0) return;
        PdhAddEnglishCounter(diskQuery, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", 0, out diskCounter);
        PdhAddEnglishCounter(diskQuery, @"\GPU Process Memory(*)\Dedicated Usage", 0, out gpuCounter);
    }

    public object Sample(int[] owned, int[] web, CancellationToken cancel)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = stamp == 0 ? 0 : Stopwatch.GetElapsedTime(stamp, now).TotalSeconds;
        stamp = now;
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        double? ramUsed = null, ramTotal = null;
        if (GlobalMemoryStatusEx(ref memory))
        {
            ramUsed = (memory.Total - memory.Available) / 1073741824.0;
            ramTotal = memory.Total / 1073741824.0;
        }
        double? load = null;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            ulong total = kernel + user;
            if (cpu is { } before && total > before.Total)
                load = 100 * (1 - (idle - before.Idle) / (double)(total - before.Total));
            cpu = (idle, total);
        }
        double? physicalWrites = null;
        bool countersReady = diskQuery != 0 && PdhCollectQueryData(diskQuery) == 0;
        if (diskCounter != 0 && countersReady &&
            PdhGetFormattedCounterValue(diskCounter, 0x200, 0, out var disk) == 0 && disk.Status <= 1 && elapsed > 0)
            physicalWrites = disk.Value;

        var primary = owned.Append(Environment.ProcessId).ToHashSet();
        var runtime = Descendants(primary);
        runtime.UnionWith(web);
        var shared = Process.GetProcessesByName("ollama");
        foreach (var process in shared) runtime.Add(process.Id);
        foreach (var process in shared) process.Dispose();
        var gpu = countersReady ? ProcessVram() : new Dictionary<int, double>();
        var rows = new List<object>();
        var live = new HashSet<(int, long)>();
        foreach (int pid in runtime.Order())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                var key = (pid, process.StartTime.Ticks);
                double time = process.TotalProcessorTime.TotalSeconds;
                ulong? writes = GetProcessIoCounters(process.Handle, out var io) ? io.WriteBytes : null;
                double? cpuPercent = null, writeRate = null;
                if (elapsed > 0 && previous.TryGetValue(key, out var old))
                {
                    cpuPercent = Math.Clamp(100 * (time - old.Cpu) / elapsed / Environment.ProcessorCount, 0, 100);
                    if (writes is ulong bytes && old.Writes is ulong earlier && bytes >= earlier)
                        writeRate = (bytes - earlier) / elapsed;
                }
                rows.Add(new { pid, name = process.ProcessName, ram = process.WorkingSet64 / 1048576.0,
                    cpu = cpuPercent, writeRate, writes, vram = gpu.TryGetValue(pid, out double dedicated) ? dedicated : (double?)null });
                previous[key] = (time, writes);
                live.Add(key);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        foreach (var key in previous.Keys.Where(key => !live.Contains(key)).ToArray()) previous.Remove(key);
        var (vramUsed, vramTotal) = Gpu(cancel);
        return new { ramUsed, ramTotal, vramUsed, vramTotal, cpu = load, physicalWrites, processes = rows };
    }

    Dictionary<int, double> ProcessVram()
    {
        var result = new Dictionary<int, double>();
        if (gpuCounter == 0) return result;
        uint size = 0, count = 0;
        if (PdhGetFormattedCounterArray(gpuCounter, 0x200, ref size, ref count, 0) != 0x800007D2 || size > 4_000_000)
            return result;
        nint buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(gpuCounter, 0x200, ref size, ref count, buffer) != 0) return result;
            int stride = Marshal.SizeOf<NamedCounter>();
            for (int index = 0; index < count; index++)
            {
                var entry = Marshal.PtrToStructure<NamedCounter>(buffer + index * stride);
                string name = Marshal.PtrToStringUni(entry.Name) ?? "";
                string[] components = name.Split('_', 4);
                if (entry.Data.Status > 1 || entry.Data.Value < 0 || components.Length < 3 ||
                    components[0] != "pid" || !int.TryParse(components[1], out int pid)) continue;
                result[pid] = result.GetValueOrDefault(pid) + entry.Data.Value / 1048576.0;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }

    static HashSet<int> Descendants(HashSet<int> pids)
    {
        var found = new HashSet<int>(pids);
        var parents = new List<(int Pid, int Parent)>();
        nint snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == -1) return found;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (bool ok = Process32First(snapshot, ref entry); ok; ok = Process32Next(snapshot, ref entry))
                parents.Add(((int)entry.Pid, (int)entry.Parent));
        }
        finally { CloseHandle(snapshot); }
        int count;
        do
        {
            count = found.Count;
            foreach (var (pid, parent) in parents)
                if (found.Contains(parent)) found.Add(pid);
        } while (found.Count != count);
        return found;
    }

    static (double?, double?) Gpu(CancellationToken cancel)
    {
        try
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("--query-gpu=memory.used,memory.total");
            info.ArgumentList.Add("--format=csv,noheader,nounits");
            using var process = Process.Start(info);
            if (process == null) return (null, null);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(1200);
            try
            {
                string output = process.StandardOutput.ReadToEndAsync(timeout.Token).GetAwaiter().GetResult();
                double used = 0, total = 0;
                foreach (var row in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var values = row.Split(',');
                    if (values.Length == 2 && int.TryParse(values[0], out var a) && int.TryParse(values[1], out var b))
                    { used += a; total += b; }
                }
                return total == 0 ? (null, null) : (used, total);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill();
                return (null, null);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return (null, null); }
    }

    public static object Model(string model, IEnumerable<JsonNode?> assistantMessages)
    {
        long input = 0, output = 0, reasoning = 0, cached = 0;
        double seconds = 0;
        long timedTokens = 0;
        long? ttft = null;
        bool available = false;
        foreach (var entry in assistantMessages)
        {
            long? created = Number(entry?["time"]?["created"]);
            long? first = Number(entry?["time"]?["firstToken"]);
            long? streamed = Number(entry?["time"]?["streamed"]);
            if (created is long start && first is long token && token >= start) ttft ??= token - start;
            if (entry?["tokens"] is not JsonObject tokens) continue;
            available = true;
            input += Number(tokens["input"]) ?? 0;
            cached += Number(tokens["cache"]?["read"]) ?? 0;
            long generated = Number(tokens["output"]) ?? 0;
            output += generated;
            reasoning += Number(tokens["reasoning"]) ?? 0;
            if (first is long begin && streamed is long end && end > begin && generated > 1)
            {
                seconds += (end - begin) / 1000.0;
                timedTokens += generated - 1;
            }
        }
        return new { model, input = available ? input : (long?)null, output = available ? output : (long?)null,
            reasoning = available ? reasoning : (long?)null, cached = available ? cached : (long?)null, ttft, rate = seconds > 0 ? timedTokens / seconds : (double?)null };
    }

    static long? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    public void Dispose() { if (diskQuery != 0) PdhCloseQuery(diskQuery); }

    [StructLayout(LayoutKind.Sequential)] struct MemoryStatus
    {
        public uint Length, Load;
        public ulong Total, Available, Page, PageAvailable, Virtual, VirtualAvailable, Extended;
    }
    [StructLayout(LayoutKind.Sequential)] struct Io
    {
        public ulong Reads, Writes, Others, ReadBytes, WriteBytes, OtherBytes;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public nuint Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [StructLayout(LayoutKind.Explicit)] struct Counter
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Value;
    }

    [StructLayout(LayoutKind.Sequential)] struct NamedCounter { public nint Name; public Counter Data; }

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint size, ref uint count, nint buffer);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] static extern bool GetProcessIoCounters(nint handle, out Io counters);
    [DllImport("kernel32.dll")] static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW")] static extern bool Process32First(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW")] static extern bool Process32Next(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string? source, nint data, out nint query);
    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(nint query, string path, nint data, out nint counter);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(nint query);
    [DllImport("pdh.dll")] static extern int PdhGetFormattedCounterValue(nint counter, uint format, nint type, out Counter result);
    [DllImport("pdh.dll")] static extern int PdhCloseQuery(nint query);
}
