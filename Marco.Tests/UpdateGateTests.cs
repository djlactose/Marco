using Marco.Core.Update;
using Xunit;

namespace Marco.Tests;

public class UpdateGateTests
{
    private static string UniqueRoot() => Path.Combine(@"X:\marco-gate-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Acquire_ThenSecondCallerTimesOut_ThenSucceedsAfterRelease()
    {
        var root = UniqueRoot();

        // Named mutexes are thread-affine: hold it on a dedicated thread and dispose it there.
        var held = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        UpdateGate? holder = null;
        var thread = new Thread(() =>
        {
            holder = UpdateGate.TryAcquire(root, TimeSpan.FromSeconds(1));
            held.Set();
            release.Wait();
            holder?.Dispose();
        });
        thread.Start();
        held.Wait();

        Assert.NotNull(holder);
        Assert.True(holder!.IsHeld);
        Assert.Null(UpdateGate.TryAcquire(root, TimeSpan.FromMilliseconds(200)));

        release.Set();
        thread.Join();

        using var second = UpdateGate.TryAcquire(root, TimeSpan.FromMilliseconds(200));
        Assert.NotNull(second);
        Assert.True(second!.IsHeld);
    }

    [Fact]
    public void DifferentRoots_DoNotSerialiseEachOther()
    {
        using var a = UpdateGate.TryAcquire(UniqueRoot(), TimeSpan.FromMilliseconds(200));
        using var b = UpdateGate.TryAcquire(UniqueRoot(), TimeSpan.FromMilliseconds(200));
        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var gate = UpdateGate.TryAcquire(UniqueRoot(), TimeSpan.FromMilliseconds(200));
        Assert.NotNull(gate);
        gate!.Dispose();
        gate.Dispose(); // second call must not throw
    }
}
