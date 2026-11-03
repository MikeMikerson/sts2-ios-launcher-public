using System;
using System.Collections.Generic;
using Godot;
using STS2Mobile.Platform;

namespace STS2Mobile.FullGame;

/// <summary>
/// The cloud saves screen: Get and Send, each asked on a screen that shows the saves on this iPhone beside Steam Cloud's, as a
/// table of plain figures from <see cref="CloudSaveComparison"/>. Nothing here talks to Steam; the flows behind it ask before anything changes.
/// </summary>
public sealed partial class LauncherView
{
    public const string CloudSendLabel = "Send to Steam Cloud";
    private Button _cloudSend = null!;
    private PanelContainer _cloudTable = null!;
    private readonly Dictionary<string, (Label Here, Label Cloud)> _cloudCells = new(StringComparer.Ordinal);
    private Label _cloudSame = null!;

    /// <summary>The player asked to send this iPhone's saves to Steam Cloud, from the cloud saves screen.</summary>
    public event Action? CloudSendRequested;

    /// <summary>Getting the Steam Cloud's saves, or sending this iPhone's, on request. The flows behind each ask before anything is replaced.</summary>
    public void ShowCloudSaves() => Show("Cloud saves",
        "Get your saves from Steam Cloud, for example after playing on a computer, or send the saves on this iPhone to it. Each one shows what would change and asks first, and nothing is synced on its own. What either one replaces is kept in a backup on this iPhone."
        + (CloudSaveSendFlow.DryRun ? "\n" + CloudSaveSendFlow.DryRunLine : ""),
        form: false, _cloudGet, _cloudSend, _back);

    /// <summary>A question about Get or Send, under the table of what each side holds. The answer arrives through <see cref="ChoiceMade"/>.</summary>
    public void ShowCloudChoice(CloudSaveComparison comparison, string question, string first, string second)
    {
        FillCloudTable(comparison);
        _choiceFirst.Text = first;
        _choiceFirst.ThemeTypeVariation = LauncherTheme.PrimaryButton;
        _choiceSecond.Text = second;
        Show("Cloud saves", question, form: false, _cloudTable, _choiceFirst, _choiceSecond, _back);
    }

    /// <summary>Adds the Send button beside Get and the comparison table under the status, from the constructor.</summary>
    private void AddCloudSaves(VBoxContainer page, VBoxContainer actions)
    {
        _cloudSend = LauncherTheme.MakeButton(CloudSendLabel, "Button", () => CloudSendRequested?.Invoke());
        _cloudSend.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        actions.AddChild(_cloudSend);
        actions.MoveChild(_cloudSend, _cloudGet.GetIndex() + 1);

        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 36);
        grid.AddThemeConstantOverride("v_separation", 14);
        grid.AddChild(new Control { MouseFilter = Control.MouseFilterEnum.Ignore });
        grid.AddChild(ColumnHeading("This iPhone"));
        grid.AddChild(ColumnHeading("Steam Cloud"));
        foreach (string row in new[] { "Save files", "Last change", "Newer copy", "Only on this side" })
        {
            var label = LauncherTheme.Text(row, LauncherTheme.Body, LauncherTheme.SmallSize, LauncherTheme.Mist);
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            grid.AddChild(label);
            var here = LauncherTheme.Text("", LauncherTheme.Figures, LauncherTheme.SmallSize, LauncherTheme.Bone);
            var cloud = LauncherTheme.Text("", LauncherTheme.Figures, LauncherTheme.SmallSize, LauncherTheme.Bone);
            grid.AddChild(here);
            grid.AddChild(cloud);
            _cloudCells[row] = (here, cloud);
        }
        _cloudSame = LauncherTheme.Text("", LauncherTheme.Body, LauncherTheme.SmallSize, LauncherTheme.Mist);
        _cloudSame.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        var lines = new VBoxContainer();
        lines.AddThemeConstantOverride("separation", 18);
        lines.AddChild(grid);
        lines.AddChild(_cloudSame);
        // A ruled ledger rather than a card: a line above and below, the figures in the monospaced face so the columns line up.
        _cloudTable = new PanelContainer { Visible = false };
        _cloudTable.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = Colors.Transparent, BorderColor = LauncherTheme.Line, BorderWidthTop = 2, BorderWidthBottom = 2,
            ContentMarginTop = 26, ContentMarginBottom = 26
        });
        _cloudTable.AddChild(lines);
        page.AddChild(_cloudTable);
        page.MoveChild(_cloudTable, _status.GetIndex() + 1);
    }

    /// <summary>Shows or hides the cloud screen's own controls with the rest, from <see cref="Show"/>.</summary>
    private void SwitchCloudControls(Control[] visible)
    {
        _cloudSend.Visible = Array.IndexOf(visible, _cloudSend) >= 0;
        _cloudTable.Visible = Array.IndexOf(visible, _cloudTable) >= 0;
    }

    private void FillCloudTable(CloudSaveComparison comparison)
    {
        Set("Save files", comparison.LocalFiles.ToString(), comparison.CloudFiles.ToString());
        // Date and time on two lines: on one, the two columns of figures made the page wider than the screen.
        Set("Last change", When(comparison.LocalNewest).Replace(' ', '\n'), When(comparison.CloudNewest).Replace(' ', '\n'));
        Set("Newer copy", comparison.NewerHere.ToString(), comparison.NewerInCloud.ToString());
        Set("Only on this side", comparison.OnlyHere.ToString(), comparison.OnlyInCloud.ToString());
        _cloudSame.Text = comparison.InSync ? "Both sides hold exactly the same saves." : $"The same on both sides: {comparison.Same} files.";
        GD.Print($"Cloud saves compared: here {comparison.LocalFiles} files, newest {When(comparison.LocalNewest)}; Steam Cloud {comparison.CloudFiles} files, newest {When(comparison.CloudNewest)}; "
            + $"same {comparison.Same}, newer here {comparison.NewerHere}, newer in the cloud {comparison.NewerInCloud}, only here {comparison.OnlyHere}, only in the cloud {comparison.OnlyInCloud}.");

        void Set(string row, string here, string cloud)
        {
            _cloudCells[row].Here.Text = here;
            _cloudCells[row].Cloud.Text = cloud;
        }
    }

    /// <summary>A last change in the phone's own time, to the minute; a dash for a side with no saves.</summary>
    private static string When(DateTimeOffset? time) => time is { } at ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "-";

    private static Label ColumnHeading(string text)
    {
        var label = LauncherTheme.Text(text, LauncherTheme.BodyStrong, LauncherTheme.SmallSize, LauncherTheme.Bone);
        label.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        return label;
    }
}
