using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace STS2Mobile.FullGame;

public static class TaskExtensions
{
    /// <summary>Lets a task run unawaited from a Godot callback, logging a failure instead of losing it.</summary>
    public static void FireAndForget(this Task task, string operation) =>
        task.ContinueWith(failed => GD.PushError(operation + " failed: " + failed.Exception?.GetBaseException()),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
