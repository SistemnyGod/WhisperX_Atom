using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WhisperX.Atom.Recorder.Host;

internal static class RecorderHostPipeSecurity
{
    public static NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is null) throw new InvalidOperationException("RECORDER_HOST_SID_UNAVAILABLE");
        AddRule(security, identity.User);
        var configuredSid = GetConfiguredUserSid();
        if (!string.IsNullOrWhiteSpace(configuredSid)
            && !string.Equals(configuredSid, identity.User.Value, StringComparison.OrdinalIgnoreCase))
        {
            try { AddRule(security, new SecurityIdentifier(configuredSid)); }
            catch (ArgumentException exception) { throw new InvalidOperationException("RECORDER_HOST_ALLOWED_SID_INVALID", exception); }
        }
        AddRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        return NamedPipeServerStreamAcl.Create(
            RecorderHostPipe.Name,
            PipeDirection.InOut,
            AgentIpcProtocol.MaxServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security);
    }

    private static void AddRule(PipeSecurity security, SecurityIdentifier sid)
        => security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));

    private static string? GetConfiguredUserSid()
    {
        var configured = Environment.GetEnvironmentVariable("ATOM_AGENT_ALLOWED_SID")
            ?? Environment.GetEnvironmentVariable("ATOM_AGENT_ALLOWED_SID", EnvironmentVariableTarget.Machine);
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

        var dataRoot = Environment.GetEnvironmentVariable("ATOM_AGENT_DATA_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhisperXAtom", "Agent");
        var sidPath = Path.Combine(dataRoot, "allowed-user.sid");
        try { return File.Exists(sidPath) ? File.ReadAllText(sidPath).Trim() : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
