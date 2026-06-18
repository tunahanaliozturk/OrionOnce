namespace Moongazing.OrionOnce;

using Moongazing.OrionOnce.Storage;

/// <summary>
/// Thrown by <see cref="IdempotentExecutor"/> when an operation cannot run or replay under its
/// idempotency key: a concurrent caller still holds the key
/// (<see cref="IdempotencyOutcome.InProgress"/>) or the key was reused with a different fingerprint
/// (<see cref="IdempotencyOutcome.FingerprintMismatch"/>). The HTTP middleware maps these same
/// conditions to 409 and 422; outside HTTP they surface as this exception so the caller can decide
/// how to respond.
/// </summary>
public sealed class IdempotentExecutionException : Exception
{
    /// <summary>Create the exception for a non-runnable, non-replayable outcome.</summary>
    /// <param name="outcome">The outcome that prevented execution or replay.</param>
    public IdempotentExecutionException(IdempotencyOutcome outcome)
        : base(MessageFor(outcome))
    {
        Outcome = outcome;
    }

    /// <summary>The store outcome that prevented the operation from running or replaying.</summary>
    public IdempotencyOutcome Outcome { get; }

    private static string MessageFor(IdempotencyOutcome outcome) => outcome switch
    {
        IdempotencyOutcome.InProgress =>
            "Another execution holding this idempotency key is still in flight.",
        IdempotencyOutcome.FingerprintMismatch =>
            "The idempotency key was reused for an operation with a different fingerprint.",
        _ => $"The idempotency key could not be executed or replayed (outcome: {outcome}).",
    };
}
