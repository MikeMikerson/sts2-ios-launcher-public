using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace STS2Mobile.GameHost;

/// <summary>
/// A settings row control that looks and pages exactly like the game's own (arrows, label animation,
/// controller focus), for the phone's battery options. It takes its parts from the game's
/// <c>paginator.tscn</c>; the value lives in <see cref="PhoneGraphics"/>.
/// </summary>
public sealed partial class PhoneOptionPaginator : NPaginator, IResettableSettingNode
{
    private const string TemplateScene = "res://scenes/screens/paginator.tscn";
    private Func<string[]> _choices = () => [];
    private Func<int> _read = () => 0;
    private Action<int> _write = _ => { };

    /// <summary>Builds the control from the game's paginator scene. Call before it enters the tree.</summary>
    /// <param name="choices">The labels offered now; read again whenever the row is refreshed.</param>
    public static PhoneOptionPaginator Create(Func<string[]> choices, Func<int> read, Action<int> write)
    {
        var paginator = new PhoneOptionPaginator { _choices = choices, _read = read, _write = write };
        using var scene = GD.Load<PackedScene>(TemplateScene);
        var template = scene.Instantiate<Control>();
        paginator.CustomMinimumSize = new Vector2(324, 64);
        paginator.SizeFlagsHorizontal = template.SizeFlagsHorizontal;
        paginator.FocusMode = template.FocusMode;
        paginator.MouseFilter = template.MouseFilter;
        // The game finds its parts as %Label and %VfxLabel, which resolve through the scene owner.
        // Moving a node clears an owner that is no longer its ancestor, so note the template's own
        // nodes first and hand them to the new root afterwards. Arrow sub-scene nodes keep theirs.
        var owned = new List<Node>();
        Collect(template, template, owned);
        foreach (Node child in template.GetChildren())
        {
            template.RemoveChild(child);
            paginator.AddChild(child);
        }
        foreach (Node node in owned) node.Owner = paginator;
        template.Free();
        return paginator;
    }

    public override void _Ready()
    {
        ConnectSignals();
        SetFromSettings();
    }

    public void SetFromSettings()
    {
        _options.Clear();
        _options.AddRange(_choices());
        _currentIndex = Math.Clamp(_read(), 0, _options.Count - 1);
        _label.SetTextAutoSize(_options[_currentIndex]);
    }

    protected override void OnIndexChanged(int index)
    {
        _currentIndex = index;
        _write(index);
        // The choice may have been adjusted (a preset can change other rows); show what is in force.
        SetFromSettings();
    }

    private static void Collect(Node node, Node owner, List<Node> owned)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child.Owner == owner) owned.Add(child);
            Collect(child, owner, owned);
        }
    }
}
