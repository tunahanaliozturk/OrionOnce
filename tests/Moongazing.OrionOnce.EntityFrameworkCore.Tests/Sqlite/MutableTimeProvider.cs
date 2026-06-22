namespace Moongazing.OrionOnce.EntityFrameworkCore.Tests.Sqlite;

/// <summary>
/// A <see cref="TimeProvider"/> whose current time only moves when the test asks, so retention and
/// sweep behavior is exercised deterministically without wall-clock waits. Mirrors the hand-rolled
/// test clock used by the in-memory store's tests rather than taking a dependency on a testing
/// package.
/// </summary>
internal sealed class MutableTimeProvider : TimeProvider
{
    private readonly object gate = new();
    private DateTimeOffset now;

    public MutableTimeProvider(DateTimeOffset start) => now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return now;
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (gate)
        {
            now += by;
        }
    }
}
