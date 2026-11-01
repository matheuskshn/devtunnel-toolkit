using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DevTunnelToolkit.DevTunnels;

// ProcessStartInfo.CreateNoWindow=false may still inherit an invisible/pseudo
// console. Create a hidden real console for this explicit login only; the CLI
// finds its visible WAM parent in Desktop (or the standalone fallback window),
// retaining redirected output, an explicit executable and a restricted handle list.
[SupportedOSPlatform("windows")]
internal static class WindowsInteractiveCli
{
    internal static CliProcess Start(ProcessStartInfo start, bool killOnParentExit = false)
    {
        using var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var error = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        nint attributes = 0, handles = 0, environment = 0;
        var initialized = false;
        ProcessInformation info = default;
        SafeFileHandle? job = null;
        try
        {
            nuint size = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((nint)size));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
            initialized = true;
            nint[] inherited = [input.ClientSafePipeHandle.DangerousGetHandle(), output.ClientSafePipeHandle.DangerousGetHandle(), error.ClientSafePipeHandle.DangerousGetHandle()];
            handles = Marshal.AllocHGlobal(nint.Size * inherited.Length);
            Marshal.Copy(inherited, 0, handles, inherited.Length);
            if (!UpdateProcThreadAttribute(attributes, 0, 0x00020002, handles, (nuint)(nint.Size * inherited.Length), 0, 0)) throw new Win32Exception();
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = 0x00000101, // STARTF_USESHOWWINDOW | STARTF_USESTDHANDLES.
                    ShowWindow = 0, // SW_HIDE retains the console but never displays it.
                    StandardInput = inherited[0],
                    StandardOutput = inherited[1],
                    StandardError = inherited[2]
                },
                Attributes = attributes
            };
            var executable = ResolveExecutable(start.FileName);
            var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(start.ArgumentList).Select(QuoteArgument)));
            environment = Marshal.StringToHGlobalUni(string.Join('\0', start.Environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value)) + "\0\0");
            // CREATE_NEW_CONSOLE | CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT.
            if (killOnParentExit)
            {
                job = CreateJobObject(0, null);
                if (job.IsInvalid) throw new Win32Exception();
                var limits = new JobLimits { Basic = new BasicJobLimits { Flags = 0x00002000 } }; // KILL_ON_JOB_CLOSE.
                if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>())) throw new Win32Exception();
            }
            // Suspend Desktop-owned logins until the non-inherited job handle owns
            // them, so a UI crash cannot leave an interactive CLI child running.
            var flags = 0x00080410u | (killOnParentExit ? 0x00000004u : 0);
            if (!CreateProcess(executable, command, 0, 0, true, flags, environment, string.IsNullOrEmpty(start.WorkingDirectory) ? null : start.WorkingDirectory, ref startup, out info)) throw new Win32Exception();
            if (job is not null && (!AssignProcessToJobObject(job, info.Process) || ResumeThread(info.Thread) == uint.MaxValue)) throw new Win32Exception();
            var process = Process.GetProcessById(info.ProcessId);
            _ = process.SafeHandle; // Acquire a managed handle before closing the native one.
            input.DisposeLocalCopyOfClientHandle(); output.DisposeLocalCopyOfClientHandle(); error.DisposeLocalCopyOfClientHandle();
            return new(process, new StreamReader(output, Encoding.UTF8), new StreamReader(error, Encoding.UTF8), job);
        }
        catch
        {
            if (info.Process != 0) TerminateProcess(info.Process, 1);
            job?.Dispose();
            output.Dispose(); error.Dispose(); throw;
        }
        finally
        {
            if (info.Thread != 0) CloseHandle(info.Thread);
            if (info.Process != 0) CloseHandle(info.Process);
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != 0) Marshal.FreeHGlobal(attributes);
            if (handles != 0) Marshal.FreeHGlobal(handles);
            if (environment != 0) Marshal.FreeHGlobal(environment);
        }
    }

    private static string ResolveExecutable(string name)
    {
        if (Path.IsPathFullyQualified(name)) return name;
        // Do not add the working directory to executable search implicitly.
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = entry.Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new Win32Exception(2);
    }

    internal static string QuoteArgument(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0; result.Append(c);
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public nint ReservedBytes, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo Startup; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public nint Process, Thread; public int ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicJobLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        public BasicJobLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(nint thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnedSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder command, nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string? directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint handle, uint exitCode);
}

internal sealed class CliProcess(Process process, StreamReader output, StreamReader error, IDisposable? job = null) : IDisposable
{
    public Process Process { get; } = process;
    public StreamReader Output { get; } = output;
    public StreamReader Error { get; } = error;

    public static CliProcess Start(ProcessStartInfo start, bool killOnParentExit = false)
    {
        if (OperatingSystem.IsWindows() && !start.CreateNoWindow) return WindowsInteractiveCli.Start(start, killOnParentExit);
        var process = new Process { StartInfo = start };
        try
        {
            process.Start(); process.StandardInput.Close();
            return new(process, process.StandardOutput, process.StandardError);
        }
        catch { process.Dispose(); throw; }
    }

    public void Dispose() { job?.Dispose(); Output.Dispose(); Error.Dispose(); Process.Dispose(); }
}
