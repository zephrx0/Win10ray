using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace XrayMetro.Services;

internal sealed class OwnedProcessJob : IDisposable
{
    private readonly SafeFileHandle _handle = CreateJobObject(IntPtr.Zero, null);
    public OwnedProcessJob()
    {
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        Limits limits = new() { Basic = new BasicLimits { Flags = 0x2000 } };
        IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<Limits>());
        try { Marshal.StructureToPtr(limits, pointer, false); if (!SetInformationJobObject(_handle, 9, pointer, (uint)Marshal.SizeOf<Limits>())) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        catch { _handle.Dispose(); throw; }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    public void Assign(Process process) { if (!AssignProcessToJobObject(_handle, process.Handle) && !process.HasExited) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    public void Dispose() => _handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint Flags; public UIntPtr Min, Max; public uint Active; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct Io { public ulong Read, Write, Other, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct Limits { public BasicLimits Basic; public Io Io; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attrs, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int info, IntPtr buffer, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
