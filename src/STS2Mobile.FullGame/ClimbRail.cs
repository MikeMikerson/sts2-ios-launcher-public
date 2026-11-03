using System.Collections.Generic;
using Godot;
using STS2Mobile.Launcher;

namespace STS2Mobile.FullGame;

/// <summary>
/// The setup steps as a climb: one node per step on a line rising from the first step at the bottom to Play at the top, the way the game's
/// map is read. Done steps are filled, the current one is ringed in gold, the rest are outlines on a dashed line. The steps are a real
/// sequence, so the order carries meaning. Drawn on a plain control through its draw signal, so no script class is needed.
/// </summary>
public sealed class ClimbRail
{
    private const float StepHeight = 88;
    private const float NodeRadius = 17;
    private const float RailX = 26;
    private const float LabelX = 86;
    private IReadOnlyList<SetupStepView> _steps = [];

    public Control Control { get; } = new() { MouseFilter = Control.MouseFilterEnum.Ignore };

    public ClimbRail() => Control.Draw += Draw;

    public void Show(IReadOnlyList<SetupStepView> steps)
    {
        _steps = steps;
        Control.CustomMinimumSize = new Vector2(0, steps.Count * StepHeight);
        Control.QueueRedraw();
    }

    private Vector2 Centre(int index) => new(RailX, (_steps.Count - 1 - index) * StepHeight + StepHeight / 2);

    private void Draw()
    {
        for (int index = 0; index + 1 < _steps.Count; index++)
        {
            // The stretch above a done step is climbed once the next one is reached.
            Vector2 from = Centre(index) + new Vector2(0, -NodeRadius), to = Centre(index + 1) + new Vector2(0, NodeRadius);
            if (_steps[index].State == SetupStepState.Done) Control.DrawLine(from, to, LauncherTheme.Moss, 4);
            else Control.DrawDashedLine(from, to, LauncherTheme.Line, 3, 10);
        }
        for (int index = 0; index < _steps.Count; index++)
        {
            var step = _steps[index];
            Vector2 centre = Centre(index);
            Color text;
            Font font = LauncherTheme.Body;
            switch (step.State)
            {
                case SetupStepState.Done:
                    Control.DrawCircle(centre, NodeRadius, LauncherTheme.Moss);
                    text = LauncherTheme.Mist;
                    break;
                case SetupStepState.Current:
                    Control.DrawCircle(centre, NodeRadius + 6, LauncherTheme.Dusk);
                    Control.DrawArc(centre, NodeRadius + 2, 0, Mathf.Tau, 48, LauncherTheme.Ember, 5, antialiased: true);
                    Control.DrawCircle(centre, NodeRadius - 8, LauncherTheme.Ember);
                    text = LauncherTheme.Bone;
                    font = LauncherTheme.BodyStrong;
                    break;
                default:
                    Control.DrawCircle(centre, NodeRadius, LauncherTheme.Dusk);
                    Control.DrawArc(centre, NodeRadius, 0, Mathf.Tau, 40, LauncherTheme.Line, 3, antialiased: true);
                    text = LauncherTheme.Line.Lerp(LauncherTheme.Mist, 0.5f);
                    break;
            }
            float baseline = centre.Y + font.GetAscent(LauncherTheme.StepSize) / 2 - font.GetDescent(LauncherTheme.StepSize) / 2;
            Control.DrawString(font, new Vector2(LabelX, baseline), step.Label, HorizontalAlignment.Left, -1, LauncherTheme.StepSize, text);
        }
    }
}
