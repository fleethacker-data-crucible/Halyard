using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Halyard.Core.Llm
{
    /// <summary>
    /// Reads Halyard's Fairlead client key from Windows Credential Manager: the generic credential
    /// <c>Fairlead/Halyard</c> that Fairlead writes ("Install for this app"). The key is the only thing read; it is
    /// never written, logged or put in a response. Fairlead stores the blob as UTF-16 text, so UTF-16LE is tried first and
    /// UTF-8 second (what a tool such as <c>cmdkey</c> may leave).
    /// </summary>
    public static class CredentialReader
    {
        public const string TargetPrefix = "Fairlead/";

        /// <summary>Replaceable so tests never read the real Credential Manager. Returns the key text, or null when there is none.</summary>
        public static Func<string, string> Reader = ReadNative;

        public static string TargetFor(string app)
        {
            return TargetPrefix + app;
        }

        /// <summary>The key stored under <paramref name="target"/>, or null when missing or unreadable.</summary>
        public static string Read(string target)
        {
            try { return Reader(target); }
            catch (Exception) { return null; }
        }

        /// <summary>UTF-16LE when that gives printable text, else UTF-8 when that does, else null.</summary>
        public static string Decode(byte[] blob)
        {
            if (blob == null || blob.Length == 0) return null;
            var wide = blob.Length % 2 == 0 ? Encoding.Unicode.GetString(blob) : null;
            if (IsPrintable(wide)) return wide.Trim();
            var narrow = Encoding.UTF8.GetString(blob);
            return IsPrintable(narrow) ? narrow.Trim() : null;
        }

        private static bool IsPrintable(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (var ch in text.Trim())
                if (ch < 0x20 || ch > 0x7E) return false;
            return true;
        }

        private const int CRED_TYPE_GENERIC = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern void CredFree(IntPtr buffer);

        private static string ReadNative(string target)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
            IntPtr ptr;
            try
            {
                if (!CredRead(target, CRED_TYPE_GENERIC, 0, out ptr)) return null;
            }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
            byte[] blob = null;
            try
            {
                var cred = (CREDENTIAL)Marshal.PtrToStructure(ptr, typeof(CREDENTIAL));
                if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize <= 0) return null;
                blob = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, blob, 0, blob.Length);
                return Decode(blob);
            }
            finally
            {
                if (blob != null) Array.Clear(blob, 0, blob.Length);
                CredFree(ptr);
            }
        }
    }
}
