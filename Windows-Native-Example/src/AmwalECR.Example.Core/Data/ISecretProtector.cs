namespace AmwalECR.Example.Core.Data;

/// <summary>
/// Encrypts the settings file at rest. The app supplies Windows DPAPI
/// (current-user scope) — the counterpart of the Android sample's
/// <c>EncryptedSharedPreferences</c>; tests supply a reversible fake.
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plain);

    /// <exception cref="System.Security.Cryptography.CryptographicException">The data was not protected for this user, or is damaged.</exception>
    byte[] Unprotect(byte[] protectedData);
}
