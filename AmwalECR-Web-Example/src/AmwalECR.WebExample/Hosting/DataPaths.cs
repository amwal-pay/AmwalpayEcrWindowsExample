namespace AmwalECR.WebExample.Hosting;

/// <summary>
/// Where the app keeps its files: <c>terminals.json</c>, <c>settings.dat</c> (the
/// encrypted signing keys) and the Data Protection key ring.
/// <para>
/// Configuration key <c>DataDir</c>; blank means a per-user app-data folder. It is
/// resolved lazily from <see cref="IConfiguration"/> so a test host can point it at
/// a temp folder.
/// </para>
/// </summary>
public sealed class DataPaths
{
    public const string DefaultFolderName = "AmwalECR-WebExample";

    public DataPaths(IConfiguration configuration)
    {
        var configured = configuration["DataDir"];
        Root = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DefaultFolderName)
            : configured.Trim());
    }

    public string Root { get; }

    public string Terminals => Path.Combine(Root, "terminals.json");

    public string Settings => Path.Combine(Root, "settings.dat");

    /// <summary>The Data Protection key ring that encrypts <c>settings.dat</c>.</summary>
    public string Keys => Path.Combine(Root, "keys");

    /// <summary>
    /// Creates the folders. On Unix they are private to the account: the key ring
    /// is only as safe as its file permissions (there is no DPAPI here to wrap it).
    /// <para>
    /// Permissions are only tightened on folders this call creates. <c>DataDir</c> may point at
    /// a folder that already exists and is shared on purpose (a mounted volume, a service
    /// account's home); changing its mode would be a surprise. The key ring, in its own
    /// <c>keys</c> subfolder, is always made private, which is what protects the secrets.
    /// </para>
    /// </summary>
    public void Ensure()
    {
        var rootIsNew = !Directory.Exists(Root);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Keys);
        if (OperatingSystem.IsWindows()) return;

        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (rootIsNew) File.SetUnixFileMode(Root, Private);
        // Always the key ring: an existing keys folder from an earlier, looser setup is exactly
        // the one that must not stay readable.
        File.SetUnixFileMode(Keys, Private);
    }
}
