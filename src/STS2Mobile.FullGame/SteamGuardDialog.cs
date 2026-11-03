using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using STS2Mobile.Launcher;

namespace STS2Mobile.FullGame;

/// <summary>
/// Shows Steam Guard steps on the launcher view for one sign-in attempt. Steam calls it from a
/// background thread, so every view change is deferred to the main thread.
/// </summary>
public sealed class SteamGuardDialog : ISteamGuardPrompt, IDisposable
{
    private readonly LauncherView _view;
    private readonly CancellationToken _attempt;
    private TaskCompletionSource<string>? _pendingCode;
    private CancellationTokenRegistration _cancelPendingCode;

    public SteamGuardDialog(LauncherView view, CancellationToken attempt)
    {
        _view = view;
        _attempt = attempt;
    }

    public void AwaitingMobileApproval() => Callable.From(() => _view.ShowWorking(
        "Open the Steam app, approve this sign-in under Steam Guard, then come back here.", canGoBack: true)).CallDeferred();

    public Task<string> RequestCodeAsync(string? sentTo, bool previousCodeWasIncorrect)
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCode = pending;
        _cancelPendingCode.Dispose();
        // Backing out must release Steam's wait as well, or the attempt would never end.
        _cancelPendingCode = _attempt.Register(() => pending.TrySetCanceled(_attempt));
        string source = sentTo == null ? "Enter the Steam Guard code shown in the Steam app." : "Enter the Steam Guard code emailed to " + sentTo + ".";
        Callable.From(() => _view.ShowGuardCode(previousCodeWasIncorrect ? "That code was not accepted. " + source : source)).CallDeferred();
        return pending.Task;
    }

    /// <summary>Called on the main thread when the player submits a code.</summary>
    public void Submit(string code)
    {
        if (_pendingCode?.TrySetResult(code) == true) _view.ShowWorking("Checking the code with Steam...");
    }

    public void Dispose() => _cancelPendingCode.Dispose();
}
