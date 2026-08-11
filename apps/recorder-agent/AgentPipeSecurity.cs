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

        var configuredSid = GetConfiguredUserSid();
        if (!string.IsNullOrWhiteSpace(configuredSid))
        {
            try { AddRule(security, new SecurityIdentifier(configuredSid), ClientPipeAccess); }
            catch (ArgumentException exception) { throw new InvalidOperationException("agent_ipc_allowed_sid_invalid", exception); }
        }
        else if (Environment.UserInteractive)
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is not null) AddRule(security, identity.User, ClientPipeAccess);
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

    // The pipe client asks Windows for the full generic pipe access mask.
    // Grant it only to the exact user selected by the installer.
    private const PipeAccessRights ClientPipeAccess = PipeAccessRights.FullControl;

    private static string? GetConfiguredUserSid()
    {
        var configured = Environment.GetEnvironmentVariable("ATOM_AGENT_ALLOWED_SID")
            ?? Environment.GetEnvironmentVariable("ATOM_AGENT_ALLOWED_SID", EnvironmentVariableTarget.Machine);
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        var dataRoot = Environment.GetEnvironmentVariable("ATOM_AGENT_DATA_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "Agent");
        var sidPath = Path.Combine(dataRoot, "allowed-user.sid");
        try { return File.Exists(sidPath) ? File.ReadAllText(sidPath).Trim() : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void AddRule(PipeSecurity security, SecurityIdentifier identity, PipeAccessRights rights) =>
        security.AddAccessRule(new PipeAccessRule(identity, rights, AccessControlType.Allow));
}
