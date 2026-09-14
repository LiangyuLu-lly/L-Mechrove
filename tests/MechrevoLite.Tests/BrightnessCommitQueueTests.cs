using System.Collections.Concurrent;
using MechrevoLite.Display;

namespace MechrevoLite.Tests;

public class BrightnessCommitQueueTests
{
    [Fact]
    public async Task Submit_CoalescesRapidChangesToLatestValue()
    {
        var writes = new ConcurrentQueue<int>();
        using var queue = new BrightnessCommitQueue(
            (value, _) =>
            {
                writes.Enqueue(value);
                return Task.CompletedTask;
            },
            TimeSpan.FromMilliseconds(25));

        queue.Submit(40);
        queue.Submit(55);
        queue.Submit(70);
        queue.Flush();
        await queue.WaitForIdleAsync();

        Assert.Equal(new[] { 70 }, writes.ToArray());
    }

    [Fact]
    public async Task Flush_CommitsWithoutWaitingForDebounce()
    {
        var completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var queue = new BrightnessCommitQueue(
            (value, _) =>
            {
                completed.TrySetResult(value);
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(2));

        queue.Submit(82);
        queue.Flush();

        Task finished = await Task.WhenAny(completed.Task, Task.Delay(500));
        Assert.Same(completed.Task, finished);
        Assert.Equal(82, await completed.Task);
    }

    [Fact]
    public async Task Writes_AreSerializedAndContinueAfterWriterFailure()
    {
        int active = 0;
        int maximumActive = 0;
        var writes = new ConcurrentQueue<int>();
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var queue = new BrightnessCommitQueue(
            async (value, _) =>
            {
                int now = Interlocked.Increment(ref active);
                InterlockedMax(ref maximumActive, now);
                writes.Enqueue(value);
                if (value == 1)
                {
                    firstStarted.TrySetResult(true);
                    await releaseFirst.Task;
                }
                Interlocked.Decrement(ref active);
                if (value == 1) throw new InvalidOperationException("test writer failure");
            },
            TimeSpan.Zero);

        queue.Submit(1);
        queue.Flush();
        await firstStarted.Task;
        queue.Submit(2);
        queue.Flush();
        releaseFirst.TrySetResult(true);
        await queue.WaitForIdleAsync();

        Assert.Equal(1, maximumActive);
        Assert.Equal(new[] { 1, 2 }, writes.ToArray());
    }

    private static void InterlockedMax(ref int location, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref location);
            if (current >= value) return;
        }
        while (Interlocked.CompareExchange(ref location, value, current) != current);
    }
}
