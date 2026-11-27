using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Owned by the launcher; the player is deliberately not in this job.
public sealed class StudyModelProcess : IDisposable
{
    private readonly SafeFileHandle job;
    public Process Process { get; private set; }

    public StudyModelProcess(string executable, string arguments, string directory, string stdout, string stderr)
    {
        job = CreateJobObject(IntPtr.Zero, null);
        try
        {
            if (job.IsInvalid) throw Error("CreateJobObject");
            var limits = new ExtendedLimits();
            limits.Basic.Flags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf(typeof(ExtendedLimits))))
                throw Error("SetInformationJobObject");

            var security = new SecurityAttributes { Size = Marshal.SizeOf(typeof(SecurityAttributes)), Inherit = true };
            using (var input = CreateFile("NUL", 0x80000000, 3, ref security, 3, 0, IntPtr.Zero))
            using (var output = CreateFile(stdout, 0x40000000, 1, ref security, 1, 0, IntPtr.Zero))
            {
                if (input.IsInvalid) throw new InvalidOperationException("Cannot open model standard input.");
                if (output.IsInvalid) throw Error("Create model stdout log");
                using (var errors = CreateFile(stderr, 0x40000000, 1, ref security, 1, 0, IntPtr.Zero))
                {
                    if (errors.IsInvalid) throw Error("Create model stderr log");
                    Process = Start(executable, arguments, directory, input, output, errors);
                }
            }
        }
        catch { job.Dispose(); throw; }
    }

    private Process Start(string executable, string arguments, string directory, SafeFileHandle input, SafeFileHandle output, SafeFileHandle errors)
    {
        var bytes = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref bytes);
        if (bytes == IntPtr.Zero) throw Error("Size process attributes");
        var attributes = Marshal.AllocHGlobal(bytes);
        var handles = IntPtr.Zero;
        var jobs = IntPtr.Zero;
        var initialized = false;
        try
        {
            initialized = InitializeProcThreadAttributeList(attributes, 2, 0, ref bytes);
            if (!initialized) throw Error("Initialize process attributes");
            handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
            jobs = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(handles, 0, input.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, output.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, errors.DangerousGetHandle());
            Marshal.WriteIntPtr(jobs, job.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x20002), handles, new IntPtr(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw Error("Limit inherited model handles");
            // Assign on creation, not after launch, so a forced exit cannot leave an unowned child.
            if (!UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x2000d), jobs, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw Error("Bind model to launcher job");
            var startup = new StartupEx { Attributes = attributes };
            startup.Info.Size = Marshal.SizeOf(typeof(StartupEx));
            startup.Info.Flags = 0x100; // STARTF_USESTDHANDLES
            startup.Info.Input = input.DangerousGetHandle();
            startup.Info.Output = output.DangerousGetHandle();
            startup.Info.Error = errors.DangerousGetHandle();
            ProcessInfo child;
            var command = new System.Text.StringBuilder("\"" + executable + "\" " + arguments);
            // EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x08080000, IntPtr.Zero, directory, ref startup, out child))
                throw Error("Start owned model");
            using (var processHandle = new SafeFileHandle(child.ProcessHandle, true))
            using (var threadHandle = new SafeFileHandle(child.ThreadHandle, true))
            {
                var process = Process.GetProcessById(child.ProcessId);
                try { GC.KeepAlive(process.Handle); return process; }
                catch { process.Dispose(); throw; }
            }
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(jobs);
            Marshal.FreeHGlobal(handles);
            Marshal.FreeHGlobal(attributes);
        }
    }

    public void Dispose() { job.Dispose(); }

    private static Win32Exception Error(string operation)
    {
        var code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, operation + ": " + new Win32Exception(code).Message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Size;
        public IntPtr Descriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool Inherit;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort ShowWindow, ReservedBytes;
        public IntPtr ReservedPointer, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupEx { public StartupInfo Info; public IntPtr Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr ProcessHandle, ThreadHandle; public int ProcessId, ThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, ref SecurityAttributes security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, int flags, ref IntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, IntPtr attribute, IntPtr value, IntPtr bytes, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string executable, System.Text.StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupEx startup, out ProcessInfo process);
}
