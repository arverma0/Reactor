using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Reactor.Processes;

/// <summary>
/// A Windows Job Object that holds a process tree. Processes started by a process in the job join it automatically, so
/// terminating the job reaches every descendant, including orphans whose parent already exited (which a parent-PID walk
/// such as <c>Process.Kill(entireProcessTree: true)</c> cannot find). The job is created with
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: if the owning process dies, or the job is disposed without
/// <see cref="TryDetach"/>, the system terminates everything in it. The job allows no breakaway, so descendants cannot
/// leave it (nested jobs of their own, such as libuv's or a browser sandbox's, stay inside it).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsJobObject : IDisposable
{
    private readonly SafeKernelHandle _handle;

    private WindowsJobObject(SafeKernelHandle handle) => _handle = handle;

    /// <summary>Creates an unnamed kill-on-close job, or returns null when the system refuses.</summary>
    public static WindowsJobObject? TryCreate()
    {
        var handle = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        var limits = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!Native.SetInformationJobObject(handle, Native.JobObjectExtendedLimitInformation, ref limits, Native.ExtendedLimitSize))
        {
            handle.Dispose();
            return null;
        }

        return new WindowsJobObject(handle);
    }

    /// <summary>
    /// Adds a process to the job. Fails (false) when the process cannot join, e.g. it belongs to a job that cannot be
    /// nested (one with UI restrictions).
    /// </summary>
    public bool TryAssign(SafeProcessHandle process) => !process.IsInvalid && Native.AssignProcessToJobObject(_handle, process);

    /// <summary>
    /// Adds the live descendants of <paramref name="root"/> that are not in the job yet: processes it started before it
    /// was assigned (<c>System.Diagnostics.Process</c> cannot start a process suspended or directly inside a job), and
    /// their descendants. Repeats while new processes are found, so children started during the sweep are caught too.
    /// Returns how many processes were added.
    /// </summary>
    public int AdoptDescendants(ProcessIdentity root)
    {
        var added = 0;
        for (var pass = 0; pass < 4; pass++)
        {
            var addedThisPass = 0;
            foreach (var descendant in FindDescendants(root))
            {
                using var process = Native.OpenProcess(
                    Native.PROCESS_SET_QUOTA | Native.PROCESS_TERMINATE | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)descendant.Id);
                if (process.IsInvalid || !IsSameProcess(process, descendant))
                {
                    continue;
                }

                if (Native.IsProcessInJob(process, _handle, out var inJob) && !inJob && Native.AssignProcessToJobObject(_handle, process))
                {
                    addedThisPass++;
                }
            }

            added += addedThisPass;
            if (addedThisPass == 0)
            {
                break;
            }
        }

        return added;
    }

    /// <summary>Terminates every process in the job (and in jobs nested inside it).</summary>
    public bool Terminate() => Native.TerminateJobObject(_handle, unchecked((uint)-1));

    /// <summary>The ids of the processes currently in the job (including nested jobs), or null when the query fails.</summary>
    public IReadOnlyList<int>? GetProcessIds()
    {
        var capacity = 64;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            // JOBOBJECT_BASIC_PROCESS_ID_LIST: two DWORD counts, then ULONG_PTR ids (at offset 8 on 32- and 64-bit).
            var size = 8 + (capacity * IntPtr.Size);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (Native.QueryInformationJobObject(_handle, Native.JobObjectBasicProcessIdList, buffer, (uint)size, out _))
                {
                    var count = Marshal.ReadInt32(buffer, 4);
                    var ids = new List<int>(count);
                    for (var i = 0; i < count; i++)
                    {
                        ids.Add((int)Marshal.ReadIntPtr(buffer, 8 + (i * IntPtr.Size)));
                    }

                    return ids;
                }

                if (Marshal.GetLastPInvokeError() != Native.ERROR_MORE_DATA)
                {
                    return null;
                }

                capacity = Math.Max(capacity * 2, Marshal.ReadInt32(buffer, 0) + 16);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    /// <summary>
    /// Clears <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>, so processes still in the job keep running after the job is
    /// disposed. Returns false when the limit could not be cleared.
    /// </summary>
    public bool TryDetach()
    {
        if (!Native.QueryInformationJobObject(_handle, Native.JobObjectExtendedLimitInformation, out var limits, Native.ExtendedLimitSize, IntPtr.Zero))
        {
            return false;
        }

        limits.BasicLimitInformation.LimitFlags &= ~Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        return Native.SetInformationJobObject(_handle, Native.JobObjectExtendedLimitInformation, ref limits, Native.ExtendedLimitSize);
    }

    /// <summary>
    /// Gives up the job handle without closing it, so a kill-on-close job that could not be detached does not terminate
    /// its processes now; they end when the owning process exits.
    /// </summary>
    public void Abandon() => _handle.SetHandleAsInvalid();

    public void Dispose() => _handle.Dispose();

    /// <summary>The identity of a process (its id and creation time), or null when it cannot be queried.</summary>
    public static ProcessIdentity? Identify(SafeProcessHandle process, int id) =>
        !process.IsInvalid && Native.GetProcessTimes(process, out var created, out _, out _, out _) ? new ProcessIdentity(id, created) : null;

    /// <summary>
    /// The live descendants of <paramref name="root"/> found through parent-process links. A link is only followed to a
    /// process created after its parent, so a reused process id never pulls in an unrelated tree.
    /// </summary>
    public static IReadOnlyList<ProcessIdentity> FindDescendants(ProcessIdentity root)
    {
        var children = new Dictionary<int, List<int>>();
        foreach (var (id, parent) in Snapshot())
        {
            if (id == parent)
            {
                continue;
            }

            if (!children.TryGetValue(parent, out var list))
            {
                children[parent] = list = [];
            }

            list.Add(id);
        }

        var result = new List<ProcessIdentity>();
        var seen = new HashSet<int> { root.Id };
        var queue = new Queue<ProcessIdentity>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            if (!children.TryGetValue(parent.Id, out var ids))
            {
                continue;
            }

            foreach (var id in ids)
            {
                if (!seen.Add(id))
                {
                    continue;
                }

                using var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)id);
                if (Identify(process, id) is not { } child || child.CreationTime < parent.CreationTime)
                {
                    continue;
                }

                result.Add(child);
                queue.Enqueue(child);
            }
        }

        return result;
    }

    /// <summary>True when <paramref name="identity"/> still refers to a process that has not exited.</summary>
    public static bool IsAlive(ProcessIdentity identity)
    {
        using var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.SYNCHRONIZE, false, (uint)identity.Id);
        return !process.IsInvalid && IsSameProcess(process, identity) && Native.WaitForSingleObject(process, 0) == Native.WAIT_TIMEOUT;
    }

    /// <summary>The identity of process <paramref name="id"/>, or null when it does not exist or cannot be opened.</summary>
    public static ProcessIdentity? Identify(int id)
    {
        using var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)id);
        return Identify(process, id);
    }

    /// <summary>Terminates the process <paramref name="identity"/> refers to, if it still exists.</summary>
    public static void TryTerminate(ProcessIdentity identity)
    {
        using var process = Native.OpenProcess(Native.PROCESS_TERMINATE | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)identity.Id);
        if (!process.IsInvalid && IsSameProcess(process, identity))
        {
            Native.TerminateProcess(process, unchecked((uint)-1));
        }
    }

    private static bool IsSameProcess(SafeProcessHandle process, ProcessIdentity identity) =>
        Native.GetProcessTimes(process, out var created, out _, out _, out _) && created == identity.CreationTime;

    private static List<(int Id, int Parent)> Snapshot()
    {
        var result = new List<(int, int)>();
        using var snapshot = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsInvalid)
        {
            return result;
        }

        var entry = new Native.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Native.PROCESSENTRY32W>() };
        if (!Native.Process32FirstW(snapshot, ref entry))
        {
            return result;
        }

        do
        {
            result.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID));
        }
        while (Native.Process32NextW(snapshot, ref entry));
        return result;
    }

    private sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeKernelHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => Native.CloseHandle(handle);
    }

    // kernel32.dll is a known DLL, always loaded from the system directory.
    private static class Native
    {
        public const int JobObjectBasicProcessIdList = 3;
        public const int JobObjectExtendedLimitInformation = 9;
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        public const uint PROCESS_TERMINATE = 0x0001;
        public const uint PROCESS_SET_QUOTA = 0x0100;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint SYNCHRONIZE = 0x00100000;
        public const uint TH32CS_SNAPPROCESS = 0x00000002;
        public const uint WAIT_TIMEOUT = 0x00000102;
        public const int ERROR_MORE_DATA = 234;

        public static readonly uint ExtendedLimitSize = (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeKernelHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(SafeKernelHandle hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryInformationJobObject(SafeKernelHandle hJob, int infoClass, out JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length, IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryInformationJobObject(SafeKernelHandle hJob, int infoClass, IntPtr info, uint length, out uint returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(SafeKernelHandle hJob, SafeProcessHandle hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TerminateJobObject(SafeKernelHandle hJob, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsProcessInJob(SafeProcessHandle process, SafeKernelHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetProcessTimes(SafeProcessHandle process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeKernelHandle CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32FirstW(SafeKernelHandle snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Process32NextW(SafeKernelHandle snapshot, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }
    }
}

/// <summary>A process id plus the process's creation time (a Windows FILETIME), which together survive id reuse.</summary>
internal readonly record struct ProcessIdentity(int Id, long CreationTime);
