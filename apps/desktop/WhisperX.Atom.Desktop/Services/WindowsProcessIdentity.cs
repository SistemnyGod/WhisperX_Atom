using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace WhisperX_Atom_Desktop.Services;

internal static class WindowsProcessIdentity
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;

    public static bool TryGetOwnerSid(int processId, out string? sid)
    {
        sid = null;
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid) return false;
        if (!OpenProcessToken(process, TokenQuery, out var token)) return false;
        using (token)
        {
            GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var length);
            if (length <= 0) return false;
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, TokenUser, buffer, length, out _)) return false;
                var user = Marshal.PtrToStructure<TOKEN_USER>(buffer);
                sid = new SecurityIdentifier(user.User.Sid).Value;
                return true;
            }
            catch (ArgumentException) { return false; }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    public static bool IsOwnedByCurrentUser(int processId)
    {
        var currentSid = WindowsIdentity.GetCurrent().User?.Value;
        return !string.IsNullOrWhiteSpace(currentSid)
            && TryGetOwnerSid(processId, out var ownerSid)
            && string.Equals(currentSid, ownerSid, StringComparison.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_USER
    {
        public SID_AND_ATTRIBUTES User;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle processHandle, uint desiredAccess, out SafeTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeTokenHandle tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    private sealed class SafeProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeProcessHandle(IntPtr handle, bool ownsHandle = true) : base(ownsHandle) => SetHandle(handle);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private sealed class SafeTokenHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeTokenHandle(IntPtr handle, bool ownsHandle = true) : base(ownsHandle) => SetHandle(handle);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
