using System.Security.Cryptography;
using AmwalECR.Example.Core.Data;

namespace AmwalECR.Example.Services;

/// <summary>
/// Windows DPAPI, current-user scope: the equivalent of the Android sample's
/// Keystore-backed <c>EncryptedSharedPreferences</c>. Only the Windows account that
/// saved the settings can read them back, and there is no key for the app to store.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    // Not a secret: it only keeps another DPAPI user of this account (any other
    // app run by the same person) from decrypting this file by accident.
    private static readonly byte[] Entropy = System.Text.Encoding.UTF8.GetBytes("AmwalECR-Example.settings.v1");

    public byte[] Protect(byte[] plain) =>
        ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedData) =>
        ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
}
