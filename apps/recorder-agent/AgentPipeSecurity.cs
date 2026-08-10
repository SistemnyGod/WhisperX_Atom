using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WhisperX.Atom.Recorder;

internal static class AgentPipeSecurity
{
    public static NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddRule(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl);
        AddRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl);

        var configuredSid = Environment.GetEnvironmentVariable("ATOM_AGENT_ALLOWED_SID");
        if (!string.IsNullOrWhiteSpace(configuredSid))
        {
            try { AddRule(security, new SecurityIdentifier(configuredSid), PipeAccessRights.ReadWrite); }
            catch (ArgumentException exception) { throw new InvalidOperationException("agent_ipc_allowed_sid_invalid", exception); }
        }
        else if (Environment.UserInteractive)
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is not null) AddRule(security, identity.User, PipeAccessRights.ReadWrite);
        }

        return NamedPipeServerStreamAcl.Create(
            AgentIpcProtocol.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security);
    }

    private static void AddRule(PipeSecurity security, SecurityIdentifier identity, PipeAccessRights rights) =>
        security.AddAccessRule(new PipeAccessRule(identity, rights, AccessControlType.Allow));
}