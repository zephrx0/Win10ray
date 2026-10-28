using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Win10ray.TunService;

internal static class ProcessSecurity
{
    // An ordinary client only needs to verify the service's image path, never read its memory or terminate it.
    public static void AllowQuery(SecurityIdentifier user)
    {
        IntPtr descriptor = IntPtr.Zero, newAcl = IntPtr.Zero, sid = IntPtr.Zero;
        try
        {
            uint result = GetSecurityInfo(GetCurrentProcess(), 6, 4, IntPtr.Zero, IntPtr.Zero, out IntPtr oldAcl, IntPtr.Zero, out descriptor);
            if (result != 0) throw new Win32Exception((int)result);
            if (oldAcl == IntPtr.Zero) return;
            byte[] bytes = new byte[user.BinaryLength]; user.GetBinaryForm(bytes, 0);
            sid = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, sid, bytes.Length);
            var entry = new ExplicitAccess { Permissions = 0x1000, Mode = 1, Trustee = new Trustee { Form = 0, Type = 1, Name = sid } };
            result = SetEntriesInAcl(1, ref entry, oldAcl, out newAcl);
            if (result != 0) throw new Win32Exception((int)result);
            result = SetSecurityInfo(GetCurrentProcess(), 6, 4, IntPtr.Zero, IntPtr.Zero, newAcl, IntPtr.Zero);
            if (result != 0) throw new Win32Exception((int)result);
        }
        finally
        {
            if (sid != IntPtr.Zero) Marshal.FreeHGlobal(sid);
            if (newAcl != IntPtr.Zero) _ = LocalFree(newAcl);
            if (descriptor != IntPtr.Zero) _ = LocalFree(descriptor);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Trustee { public IntPtr Multiple; public int Operation, Form, Type; public IntPtr Name; }
    [StructLayout(LayoutKind.Sequential)] private struct ExplicitAccess { public uint Permissions; public int Mode; public uint Inheritance; public Trustee Trustee; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr handle);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(IntPtr handle, int type, uint info, IntPtr owner, IntPtr group, out IntPtr dacl, IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint SetSecurityInfo(IntPtr handle, int type, uint info, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("advapi32.dll", EntryPoint = "SetEntriesInAclW", CharSet = CharSet.Unicode)] private static extern uint SetEntriesInAcl(uint count, ref ExplicitAccess entries, IntPtr oldAcl, out IntPtr newAcl);
}
