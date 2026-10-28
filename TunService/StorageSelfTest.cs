using System.Security.AccessControl;
using System.Security.Principal;
using XrayMetro.Services;

namespace Win10ray.TunService;

internal static class StorageSelfTest
{
    public static void Run()
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var security = new DirectorySecurity(); security.SetOwner(admins);
        security.AddAccessRule(new(admins, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier("S-1-15-2-1"), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        if (security.AreAccessRulesProtected) throw new InvalidOperationException("Fixture must model inherited installer permissions.");
        ProtectedStorage.ValidateContainerSecurity(security);
        security.AddAccessRule(new(users, FileSystemRights.Modify, AccessControlType.Allow));
        ExpectDenied(() => ProtectedStorage.ValidateContainerSecurity(security));
        security = new DirectorySecurity(); security.SetOwner(users);
        ExpectDenied(() => ProtectedStorage.ValidateContainerSecurity(security));
        security = new DirectorySecurity(); security.SetSecurityDescriptorSddlForm("O:BAG:BAD:(A;;FA;;;BA)(A;;GRGX;;;BU)");
        ProtectedStorage.ValidateContainerSecurity(security);
        security.SetSecurityDescriptorSddlForm("O:BAG:BAD:(A;;FA;;;BA)(A;;GW;;;BU)");
        ExpectDenied(() => ProtectedStorage.ValidateContainerSecurity(security));
        foreach (TunInstallStage stage in Enum.GetValues<TunInstallStage>())
        {
            int exit = TunInstallDiagnostics.Encode(stage, 5);
            if (!TunInstallDiagnostics.TryDecode(exit, out TunInstallStage actual, out int native) || actual != stage || native != 5)
                throw new InvalidOperationException("Installation stage was lost.");
        }
    }

    public static async Task RunElevatedAsync()
    {
        Run();
        string programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        string root = Path.GetFullPath(Path.Combine(programFiles, "Win10ray-StorageTest-" + Guid.NewGuid().ToString("N")));
        if (!root.StartsWith(programFiles + Path.DirectorySeparatorChar + "Win10ray-StorageTest-", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        Directory.CreateDirectory(root);
        try
        {
            var directory = new DirectoryInfo(root);
            DirectorySecurity before = directory.GetAccessControl();
            if (before.AreAccessRulesProtected) throw new InvalidOperationException("Could not reproduce a normal inherited Program Files directory.");
            // This is the operation in 1.3.1: it rejects the ordinary installer-created parent.
            ExpectDenied(() => ProtectedStorage.CreateDirectory(root, true));
            string descriptor = before.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
            ProtectedStorage.CreateContainer(root, true);
            if (directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access) != descriptor)
                throw new InvalidOperationException("Shared application permissions changed.");
            string service = Path.Combine(root, "TunService");
            ProtectedStorage.CreateDirectory(service, true);
            await ProtectedStorage.WriteTextAsync(Path.Combine(service, "test.txt"), "protected storage regression test");
            if (!new DirectoryInfo(service).GetAccessControl().AreAccessRulesProtected) throw new InvalidOperationException("Service directory is not protected.");
            string data = Path.Combine(root, "Data");
            ProtectedStorage.CreateDirectory(data, false);
            foreach (FileSystemAccessRule rule in new DirectoryInfo(data).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)) && rule.AccessControlType == AccessControlType.Allow)
                    throw new InvalidOperationException("Private service data became readable by ordinary users.");
            string writable = Path.Combine(root, "Writable"); Directory.CreateDirectory(writable);
            var writableDirectory = new DirectoryInfo(writable);
            DirectorySecurity unsafeAcl = writableDirectory.GetAccessControl();
            unsafeAcl.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Modify, AccessControlType.Allow));
            writableDirectory.SetAccessControl(unsafeAcl);
            ExpectDenied(() => ProtectedStorage.CreateContainer(writable, true));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ExpectDenied(Action action)
    {
        try { action(); } catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Untrusted or incompatible storage was accepted.");
    }
}
