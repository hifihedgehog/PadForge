namespace PadForge.Engine.Tablets;

internal static class TabletCaptureQueue
{
    private static readonly object gate = new();
    private static readonly Dictionary<string, Task> tails = new(StringComparer.OrdinalIgnoreCase);

    internal static Turn Reserve(string instance)
    {
        lock (gate)
        {
            var turn = new Turn(instance, tails.GetValueOrDefault(instance) ?? Task.CompletedTask);
            tails[instance] = turn.Completion.Task;
            return turn;
        }
    }

    internal sealed class Turn
    {
        private readonly string instance;
        internal readonly Task Predecessor;
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Turn(string instance, Task predecessor)
        {
            this.instance = instance;
            Predecessor = predecessor;
        }

        internal void Complete()
        {
            lock (gate)
            {
                Completion.TrySetResult();
                if (tails.TryGetValue(instance, out var current) && ReferenceEquals(current, Completion.Task))
                    tails.Remove(instance);
            }
        }
    }
}
