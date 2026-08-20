namespace Marco.Core.Update;

/// <summary>
/// Runs a file operation that can transiently fail with a sharing violation. On a portable install the exe and
/// updates directory often sit inside a sync folder (Nextcloud/OneDrive) whose engine — or an antivirus scanner —
/// briefly opens freshly written files without FILE_SHARE_DELETE, so a rename/replace fails even though a read
/// succeeds. Scans of a ~70 MB exe can hold it for seconds, so the backoff is exponential: with the defaults,
/// 5 attempts with waits of 200/400/800/1600 ms (~3 s total) before the final IOException propagates.
/// </summary>
public static class FileRetry
{
    public static void Run(Action operation, int attempts = 5, int firstDelayMs = 200)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { operation(); return; }
            catch (IOException) when (attempt < attempts - 1)
            {
                Thread.Sleep(firstDelayMs << attempt);
            }
        }
    }
}
