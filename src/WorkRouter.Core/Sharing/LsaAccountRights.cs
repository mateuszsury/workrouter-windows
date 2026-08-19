using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace WorkRouter.Sharing;

internal static class LsaAccountRights
{
    private const uint PolicyLookupNames = 0x00000800;
    private const uint PolicyCreateAccount = 0x00000010;

    public static void AddDenyInteractiveRights(SecurityIdentifier accountSid)
    {
        var attributes = new LsaObjectAttributes { Length = (uint)Marshal.SizeOf<LsaObjectAttributes>() };
        var status = LsaOpenPolicy(IntPtr.Zero, ref attributes, PolicyLookupNames | PolicyCreateAccount, out var policy);
        ThrowIfError(status, "LsaOpenPolicy");
        try
        {
            var sidBytes = new byte[accountSid.BinaryLength];
            accountSid.GetBinaryForm(sidBytes, 0);
            var sidHandle = GCHandle.Alloc(sidBytes, GCHandleType.Pinned);
            try
            {
                var rights = new[]
                {
                    LsaUnicodeString.Create("SeDenyInteractiveLogonRight"),
                    LsaUnicodeString.Create("SeDenyRemoteInteractiveLogonRight"),
                    LsaUnicodeString.Create("SeDenyBatchLogonRight"),
                    LsaUnicodeString.Create("SeDenyServiceLogonRight")
                };

                try
                {
                    status = LsaAddAccountRights(policy, sidHandle.AddrOfPinnedObject(), rights, (uint)rights.Length);
                    ThrowIfError(status, "LsaAddAccountRights");
                }
                finally
                {
                    foreach (var right in rights)
                    {
                        right.Dispose();
                    }
                }
            }
            finally
            {
                sidHandle.Free();
            }
        }
        finally
        {
            _ = LsaClose(policy);
        }
    }

    private static void ThrowIfError(uint status, string operation)
    {
        if (status == 0)
        {
            return;
        }

        var error = LsaNtStatusToWinError(status);
        throw new Win32Exception((int)error, $"{operation} nie powiodło się.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaUnicodeString : IDisposable
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;

        public static LsaUnicodeString Create(string value)
        {
            var buffer = Marshal.StringToHGlobalUni(value);
            return new LsaUnicodeString
            {
                Buffer = buffer,
                Length = checked((ushort)(value.Length * sizeof(char))),
                MaximumLength = checked((ushort)((value.Length + 1) * sizeof(char)))
            };
        }

        public void Dispose()
        {
            if (Buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Buffer);
                Buffer = IntPtr.Zero;
            }
        }
    }

    [DllImport("advapi32.dll")]
    private static extern uint LsaOpenPolicy(
        IntPtr systemName,
        ref LsaObjectAttributes objectAttributes,
        uint desiredAccess,
        out IntPtr policyHandle);

    [DllImport("advapi32.dll")]
    private static extern uint LsaAddAccountRights(
        IntPtr policyHandle,
        IntPtr accountSid,
        [In] LsaUnicodeString[] userRights,
        uint countOfRights);

    [DllImport("advapi32.dll")]
    private static extern uint LsaClose(IntPtr objectHandle);

    [DllImport("advapi32.dll")]
    private static extern uint LsaNtStatusToWinError(uint status);
}
