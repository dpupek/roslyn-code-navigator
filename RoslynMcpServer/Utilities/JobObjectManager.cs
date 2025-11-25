using Microsoft.Win32.SafeHandles;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RoslynMcpServer.Utilities;

/// <summary>
/// Assigns child processes to a Windows Job Object so they are terminated if the parent dies.
/// No-ops on non-Windows hosts.
/// </summary>
public sealed class JobObjectManager : IDisposable
{
    private readonly bool _enabled;
    private readonly SafeFileHandle? _jobHandle;

    public JobObjectManager()
    {
        _enabled = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        if (!_enabled)
        {
            return;
        }

        _jobHandle = CreateJobObject(IntPtr.Zero, null);
        if (_jobHandle is null || _jobHandle.IsInvalid)
        {
            _enabled = false;
            return;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOBOBJECTLIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };

        int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr infoPtr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, infoPtr, false);
            if (!SetInformationJobObject(_jobHandle, JobObjectInfoType.ExtendedLimitInformation, infoPtr, (uint)length))
            {
                _enabled = false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(infoPtr);
        }
    }

    public void Add(Process process)
    {
        if (!_enabled || _jobHandle is null || _jobHandle.IsInvalid)
        {
            return;
        }

        try
        {
            AssignProcessToJobObject(_jobHandle, process.Handle);
        }
        catch
        {
            // best-effort; ignore failures
        }
    }

    public void Dispose()
    {
        _jobHandle?.Dispose();
    }

    #region Win32
    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9
    }

    [Flags]
    private enum JOBOBJECTLIMIT : uint
    {
        JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public JOBOBJECTLIMIT LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public long Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle hJob, JobObjectInfoType infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr processHandle);
    #endregion
}
