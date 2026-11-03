namespace STS2Mobile.Launcher;

/// <summary>The steps from a new install to the game, in the order the player goes through them.</summary>
public enum SetupStep
{
    SignIn,
    Ownership,
    Download,
    Prepare,
    Effects,
    Reopen,
    Play,
}

public enum SetupStepState
{
    Done,
    Current,
    Waiting,
}

public readonly record struct SetupStepView(string Label, SetupStepState State);

/// <summary>What the row of steps on the setup screens says, so the view only draws it.</summary>
public static class SetupSteps
{
    private static readonly (SetupStep Step, string Label)[] Labels =
    [
        (SetupStep.SignIn, "Sign in"),
        (SetupStep.Ownership, "Ownership"),
        (SetupStep.Download, "Download"),
        (SetupStep.Prepare, "Prepare"),
        (SetupStep.Effects, "Effects"),
        (SetupStep.Reopen, "Reopen"),
        (SetupStep.Play, "Play"),
    ];

    /// <summary>Everything before <paramref name="current"/> is done, even a step this run skipped because its result was already here.</summary>
    public static IReadOnlyList<SetupStepView> For(SetupStep current) =>
        Labels.Select(entry => new SetupStepView(entry.Label,
            entry.Step < current ? SetupStepState.Done : entry.Step == current ? SetupStepState.Current : SetupStepState.Waiting)).ToList();
}
