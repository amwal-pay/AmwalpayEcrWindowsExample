using System.Security.Cryptography;
using AmwalECR.Example.Core.Data;
using Microsoft.AspNetCore.DataProtection;

namespace AmwalECR.WebExample.Hosting;

/// <summary>
/// The cross-platform counterpart of the WPF app's DPAPI protector: the same
/// <see cref="ISecretProtector"/> seam, backed by ASP.NET Core Data Protection.
/// <para>
/// The key ring is persisted under the data folder, so settings survive a restart.
/// A ring that is lost or copied to another machine makes the file unreadable, and
/// <see cref="EcrExampleSettings"/> reports that as a load warning rather than
/// failing.
/// </para>
/// </summary>
public sealed class DataProtectionSecretProtector : ISecretProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionSecretProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("AmwalECR.Example.settings.v1");

    public byte[] Protect(byte[] plain) => _protector.Protect(plain);

    /// <exception cref="CryptographicException">Not protected by this key ring, or damaged.</exception>
    public byte[] Unprotect(byte[] protectedData) => _protector.Unprotect(protectedData);
}
