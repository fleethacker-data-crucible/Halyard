using System;
using System.Security.Cryptography;
using System.Text;

namespace Halyard.Core.Config
{
    /// <summary>
    /// Secrets at rest: Windows DPAPI bound to the current user (as the rest of the suite does it). A protected value is
    /// written as <c>dpapi:&lt;base64&gt;</c>; only the same Windows account on the same machine can read it back.
    /// </summary>
    public static class SecretProtector
    {
        public const string Prefix = "dpapi:";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Halyard.LlmToken.v1");

        /// <summary>True when DPAPI works here (Windows); false elsewhere, where secrets stay in memory only.</summary>
        public static bool IsAvailable
        {
            get
            {
                try { return Unprotect(Protect("probe")) == "probe"; }
                catch (PlatformNotSupportedException) { return false; }
                catch (CryptographicException) { return false; }
                catch (TypeLoadException) { return false; }
                catch (System.IO.FileNotFoundException) { return false; }
            }
        }

        public static bool IsProtected(string stored)
        {
            return stored != null && stored.StartsWith(Prefix, StringComparison.Ordinal);
        }

        /// <summary>Throws PlatformNotSupportedException where DPAPI does not exist; callers check <see cref="IsAvailable"/>.</summary>
        public static string Protect(string secret)
        {
            if (secret == null) throw new ArgumentNullException("secret");
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(data);
        }

        /// <summary>The secret, or null when the value cannot be read (another account's value, a damaged value, no DPAPI).</summary>
        public static string Unprotect(string stored)
        {
            if (!IsProtected(stored)) return null;
            try
            {
                var data = ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(Prefix.Length)), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (CryptographicException) { return null; }
            catch (FormatException) { return null; }
            catch (PlatformNotSupportedException) { return null; }
        }
    }
}
