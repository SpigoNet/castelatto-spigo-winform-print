using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using ZplPrintManager.Contracts;
namespace ZplPrintManager.Infrastructure.Windows;

public sealed class DpapiSecrets(string directory)
{
    private string FileName(Guid id) => Path.Combine(directory, $"{id:N}.secret");
    public string Read(Guid id) => File.Exists(FileName(id)) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FileName(id)), null, DataProtectionScope.CurrentUser)) : "";
    public void Write(Guid id, string password)
    {
        Directory.CreateDirectory(directory);
        byte[] bytes = Encoding.UTF8.GetBytes(password);
        try
        {
            byte[] protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            string temp = FileName(id) + ".tmp";
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { output.Write(protectedBytes); output.Flush(true); }
            File.Move(temp, FileName(id), true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void SelfTest()
    {
        byte[] value = RandomNumberGenerator.GetBytes(32);
        byte[] roundtrip = ProtectedData.Unprotect(ProtectedData.Protect(value, null, DataProtectionScope.CurrentUser), null, DataProtectionScope.CurrentUser);
        if (!CryptographicOperations.FixedTimeEquals(value, roundtrip)) throw new CryptographicException("DPAPI indisponível para a identidade do serviço.");
    }
}
public static class PipeSecurityPolicy
{
    public const string Readers = "ZplPrintManager Readers";
    public const string Operators = "ZplPrintManager Operators";
    public const string Administrators = "ZplPrintManager Administrators";
    public static NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        foreach (string group in new[] { Readers, Operators, Administrators })
            security.AddAccessRule(new PipeAccessRule(new NTAccount(Environment.MachineName, group), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(Protocol.PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, security);
    }
    public static (string Actor, Role Role) Identify(NamedPipeServerStream pipe)
    {
        (string Actor, Role Role)? result = null;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(true) ?? throw new UnauthorizedAccessException();
            if (!identity.IsAuthenticated) throw new UnauthorizedAccessException();
            var principal = new WindowsPrincipal(identity);
            bool Member(string group) => principal.IsInRole($"{Environment.MachineName}\\{group}");
            Role role = principal.IsInRole(WindowsBuiltInRole.Administrator) || Member(Administrators) ? Role.Administrator : Member(Operators) ? Role.Operator : Member(Readers) ? Role.Reader : throw new UnauthorizedAccessException();
            result = (identity.Name, role);
        });
        return result ?? throw new UnauthorizedAccessException();
    }
}
