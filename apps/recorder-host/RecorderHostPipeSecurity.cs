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
        AddRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        return NamedPipeServerStreamAcl.Create(
            RecorderHostPipe.Name,
            PipeDirection.InOut,
            4,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security);
    }

    private static void AddRule(PipeSecurity security, SecurityIdentifier sid)
        => security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
}
