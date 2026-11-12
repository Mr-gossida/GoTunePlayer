using System.Runtime.InteropServices;
using System.Text;

namespace TuneSync.App.Network;

/// <summary>Passwords for network locations, kept in Windows Credential Manager (encrypted for the
/// signed-in Windows user, visible under 제어판 › 자격 증명 관리자) rather than in the settings file.</summary>
public static class CredentialStore
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    private static string Target(string sourceId) => $"TuneSync:{sourceId}";

    public static void Save(string sourceId, string userName, string password)
    {
        var secret = Encoding.Unicode.GetBytes(password);
        var blob = Marshal.AllocCoTaskMem(Math.Max(1, secret.Length));
        try
        {
            Marshal.Copy(secret, 0, blob, secret.Length);
            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Target(sourceId),
                UserName = userName,
                CredentialBlob = blob,
                CredentialBlobSize = secret.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException("비밀번호를 저장하지 못했습니다.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public static string Load(string sourceId)
    {
        if (!CredRead(Target(sourceId), CRED_TYPE_GENERIC, 0, out var pointer)) return "";
        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(pointer);
            if (credential.CredentialBlobSize == 0) return "";
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static void Delete(string sourceId) => CredDelete(Target(sourceId), CRED_TYPE_GENERIC, 0);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
