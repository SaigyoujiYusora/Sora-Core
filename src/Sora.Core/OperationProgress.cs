namespace Sora.Core;

public sealed record OperationUpdate(string Stage, long? Completed = null, long? Total = null, string? Detail = null);

/// <summary>Request scoped, synchronous checkpoints; no elapsed-time percentages.</summary>
public static class OperationProgress
{
    private static readonly AsyncLocal<Action<OperationUpdate>?> Current = new();
    private static readonly AsyncLocal<Action<Action>?> CommitScope = new();
    public static Action<Action>? Commit { get => CommitScope.Value; set => CommitScope.Value = value; }
    public static Action<OperationUpdate>? Sink { get => Current.Value; set => Current.Value = value; }
    public static void Report(string stage, long? completed = null, long? total = null, string? detail = null)
        => Current.Value?.Invoke(new(stage, completed, total, detail));
    public static void CommitAtomic(Action replace)
    {
        if (CommitScope.Value is {} commit) commit(replace); else replace();
    }
}
