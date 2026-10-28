using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Win10ray.TunService;

internal static class ProtectedStorage
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    public static void CreateContainer(string path, bool allowRead)
    {
        // Inno Setup creates the application parent with normal inherited Program Files ACLs.
        // DirectoryInfo.Create(security) leaves an existing directory's ACL unchanged.
        // Validate this shared parent without rewriting permissions of the installed client.
        if (!Directory.Exists(path)) CreateDirectory(path, allowRead);
        using SafeFileHandle guard = OpenDirectory(path);
        ValidateContainerSecurity(new DirectoryInfo(path).GetAccessControl());
    }

    internal static void ValidateContainerSecurity(DirectorySecurity actual)
    {
        IdentityReference owner = actual.GetOwner(typeof(SecurityIdentifier)) ?? throw new UnauthorizedAccessException();
        if (!Trusted(owner)) throw new UnauthorizedAccessException("Service container has an untrusted owner.");
        const FileSystemRights genericReadExecute = unchecked((FileSystemRights)0xa0000000);
        FileSystemRights readOnly = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize | genericReadExecute;
        foreach (FileSystemAccessRule rule in actual.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0 || Trusted(rule.IdentityReference)) continue;
            if ((rule.FileSystemRights & ~readOnly) != 0)
                throw new UnauthorizedAccessException("Service container is writable by a non-administrator.");
        }
    }

    private static bool Trusted(IdentityReference sid) => sid.Equals(Administrators) || sid.Equals(SystemAccount) || sid.Equals(TrustedInstaller);

    private static SafeFileHandle OpenDirectory(string path)
    {
        SafeFileHandle guard = CreateFile(path, 0x20080, FileShare.Read | FileShare.Write, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        try
        {
            if (guard.IsInvalid || !GetFileInformationByHandleEx(guard, 9, out AttributeInfo attributes, 8)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((attributes.Attributes & 0x400) != 0) throw new InvalidDataException("Reparse points are not allowed in service storage.");
            return guard;
        }
        catch { guard.Dispose(); throw; }
    }

    public static void CreateDirectory(string path, bool allowRead)
    {
        var permissions = new DirectorySecurity();
        permissions.SetAccessRuleProtection(true, false); permissions.SetOwner(Administrators);
        foreach (SecurityIdentifier sid in new[] { SystemAccount, Administrators })
            permissions.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (allowRead) permissions.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(path);
        directory.Create(permissions); // Apply ownership and DACL atomically on creation.
        using SafeFileHandle guard = OpenDirectory(path);
        DirectorySecurity actual = directory.GetAccessControl();
        IdentityReference owner = actual.GetOwner(typeof(SecurityIdentifier)) ?? throw new UnauthorizedAccessException();
        if ((!owner.Equals(Administrators) && !owner.Equals(SystemAccount)) || !actual.AreAccessRulesProtected) throw new UnauthorizedAccessException();
        foreach (FileSystemAccessRule rule in actual.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.IdentityReference.Equals(Administrators) || rule.IdentityReference.Equals(SystemAccount)) continue;
            if (allowRead && rule.IdentityReference.Equals(Users) && (rule.FileSystemRights & ~(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize)) == 0) continue;
            throw new UnauthorizedAccessException();
        }
    }

    public static async Task CopyAsync(string source, string destination)
    {
        await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        await WriteAsync(destination, stream => input.CopyToAsync(stream));
    }
    public static Task WriteTextAsync(string destination, string text) => WriteAsync(destination, async stream =>
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text); await stream.WriteAsync(bytes);
    });
    private static async Task WriteAsync(string destination, Func<FileStream, Task> writer)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, Guid.NewGuid().ToString("N") + ".tmp");
        var security = new FileSecurity(); security.SetAccessRuleProtection(true, false); security.SetOwner(Administrators);
        foreach (SecurityIdentifier sid in new[] { SystemAccount, Administrators }) security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        try
        {
            await using (FileStream stream = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.Read | FileSystemRights.Write, FileShare.None, 65536, FileOptions.Asynchronous, security))
            {
                await writer(stream); await stream.FlushAsync();
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeInfo { public uint Attributes, ReparseTag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr attributes, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int info, out AttributeInfo result, uint size);
}
