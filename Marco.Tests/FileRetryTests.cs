using Marco.Core.Update;
using Xunit;

namespace Marco.Tests;

public class FileRetryTests
{
    [Fact]
    public void Run_FirstAttemptSucceeds_InvokesOperationOnce()
    {
        int calls = 0;

        FileRetry.Run(() => calls++, firstDelayMs: 1);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Run_RetriesIOException_UntilItSucceeds()
    {
        int calls = 0;

        FileRetry.Run(() => { if (++calls < 3) throw new IOException("locked"); }, firstDelayMs: 1);

        Assert.Equal(3, calls);
    }

    [Fact]
    public void Run_ExhaustsAttempts_ThrowsTheLastIOException()
    {
        int calls = 0;

        Assert.Throws<IOException>(() =>
            FileRetry.Run(() => { calls++; throw new IOException("locked"); }, attempts: 3, firstDelayMs: 1));

        Assert.Equal(3, calls);
    }

    [Fact]
    public void Run_NonIOException_PropagatesWithoutRetry()
    {
        int calls = 0;

        Assert.Throws<InvalidOperationException>(() =>
            FileRetry.Run(() => { calls++; throw new InvalidOperationException(); }, firstDelayMs: 1));

        Assert.Equal(1, calls);
    }
}
