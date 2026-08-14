namespace Marco.Core.Storage;

/// <summary>
/// Filesystem seams for <see cref="AppPaths"/>, so path resolution can be unit-tested against a fake without
/// touching disk. The real implementation resolves the exe directory via <c>Environment.ProcessPath</c> /
/// <c>AppContext.BaseDirectory</c> — never <c>Assembly.Location</c>, which is empty under a single-file publish.
/// </summary>
public interface IStorageProbe
{
    string ExeDirectory { get; }
    string LocalAppDataDirectory { get; }

    /// <summary>True if <paramref name="directory"/> exists-or-can-be-created and a probe file can be written
    /// and deleted there. This is an actual write test, not an ACL inspection (ACLs lie under virtualization).</summary>
    bool CanCreateAndWrite(string directory);

    void EnsureDirectory(string directory);
}

/// <summary>Real filesystem probe. BCL-only; safe in the net8.0 Core assembly.</summary>
public sealed class DefaultStorageProbe : IStorageProbe
{
    public string ExeDirectory
    {
        get
        {
            var exe = Environment.ProcessPath;
            var dir = exe is not null ? Path.GetDirectoryName(exe) : null;
            return dir ?? AppContext.BaseDirectory;
        }
    }

    public string LocalAppDataDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public bool CanCreateAndWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".marco-write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false; // read-only (Program Files), locked share, or denied
        }
    }

    public void EnsureDirectory(string directory) => Directory.CreateDirectory(directory);
}
