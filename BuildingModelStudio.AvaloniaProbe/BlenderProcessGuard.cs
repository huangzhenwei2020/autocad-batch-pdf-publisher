using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BuildingModelStudio.AvaloniaProbe;

internal sealed class BlenderProcessGuard : IDisposable
{
    private IntPtr _job;

    public BlenderProcessGuard(Process process, long memoryBytes)
    {
        if (!OperatingSystem.IsWindows()) return;
        _job = CreateJobObject(IntPtr.Zero, null);
        try
        {
            if (_job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            SetLimits(0x2100, memoryBytes);
            if (!AssignProcessToJobObject(_job, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { Dispose(); throw; }
    }

    // After the first frame, Blender belongs to the user, not to Studio's lifetime.
    public void Release() { if (_job != IntPtr.Zero) SetLimits(0, 0); }

    private void SetLimits(uint flags, long bytes)
    {
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = flags }, ProcessMemory = (UIntPtr)(ulong)bytes };
        if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        if (_job == IntPtr.Zero) return;
        CloseHandle(_job);
        _job = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
