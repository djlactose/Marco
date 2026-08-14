using Marco.Core.Storage;
using Xunit;

namespace Marco.Tests;

public class AppPathsTests
{
    private sealed class FakeProbe : IStorageProbe
    {
        public string ExeDirectory { get; init; } = @"X:\app";
        public string LocalAppDataDirectory { get; init; } = @"C:\Users\me\AppData\Local";
        public bool ExeDirWritable { get; init; } = true;

        public readonly HashSet<string> Created = new(StringComparer.OrdinalIgnoreCase);

        public bool CanCreateAndWrite(string directory)
            => ExeDirWritable && directory.StartsWith(ExeDirectory, StringComparison.OrdinalIgnoreCase);

        public void EnsureDirectory(string directory) => Created.Add(directory);
    }

    [Fact]
    public void PrefersBesideExe_WhenWritable()
    {
        var probe = new FakeProbe { ExeDirWritable = true };
        var paths = AppPaths.Resolve(probe);

        Assert.True(paths.IsPortable);
        Assert.Equal(Path.Combine(@"X:\app", "Marco.Data"), paths.Root);
        Assert.Contains(paths.Root, probe.Created);
        Assert.Contains("Portable", paths.Reason);
    }

    [Fact]
    public void FallsBackToLocalAppData_WhenExeDirReadOnly()
    {
        var probe = new FakeProbe { ExeDirWritable = false };
        var paths = AppPaths.Resolve(probe);

        Assert.False(paths.IsPortable);
        Assert.Equal(Path.Combine(@"C:\Users\me\AppData\Local", "Marco"), paths.Root);
        Assert.Contains(paths.Root, probe.Created);
        Assert.Contains("not writable", paths.Reason);
    }

    [Fact]
    public void SubDirectories_AreCreatedUnderRoot()
    {
        var probe = new FakeProbe { ExeDirWritable = true };
        var paths = AppPaths.Resolve(probe);

        var logs = paths.LogsDirectory;
        var scans = paths.ScansDirectory;

        Assert.StartsWith(paths.Root, logs);
        Assert.StartsWith(paths.Root, scans);
        Assert.Contains(logs, probe.Created);
        Assert.Contains(scans, probe.Created);
        Assert.Equal(Path.Combine(logs, "marco.log"), paths.LogFile);
    }
}
