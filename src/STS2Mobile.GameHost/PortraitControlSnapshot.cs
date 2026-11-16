using System.Collections.Generic;
using Godot;

namespace STS2Mobile.GameHost;

/// <summary>
/// The desktop layout of one control (anchors, offsets, grow, scale, pivot, minimum size, size flags, visibility and
/// spacing theme constants), captured before the first portrait edit so turning Portrait Mode off puts it back exactly.
/// </summary>
internal sealed class PortraitControlSnapshot
{
    private static readonly string[] ThemeConstantNames =
        ["margin_left", "margin_top", "margin_right", "margin_bottom", "separation", "h_separation", "v_separation"];

    private readonly float _anchorLeft, _anchorTop, _anchorRight, _anchorBottom;
    private readonly float _offsetLeft, _offsetTop, _offsetRight, _offsetBottom;
    private readonly Control.GrowDirection _growHorizontal, _growVertical;
    private readonly Vector2 _scale, _pivotOffset, _customMinimumSize;
    private readonly Control.SizeFlags _sizeFlagsHorizontal, _sizeFlagsVertical;
    private readonly Dictionary<string, (bool HasOverride, int Value)> _themeConstants = new();
    private bool _visible;

    public bool CaptureVisible { get; private set; }
    public float InitialOffsetLeft => _offsetLeft;
    public float InitialOffsetRight => _offsetRight;
    public float InitialOffsetTop => _offsetTop;
    public float InitialOffsetBottom => _offsetBottom;
    public Vector2 InitialScale => _scale;
    public Vector2 InitialCustomMinimumSize => _customMinimumSize;

    public PortraitControlSnapshot(Control control, bool captureVisible)
    {
        _anchorLeft = control.AnchorLeft;
        _anchorTop = control.AnchorTop;
        _anchorRight = control.AnchorRight;
        _anchorBottom = control.AnchorBottom;
        _offsetLeft = control.OffsetLeft;
        _offsetTop = control.OffsetTop;
        _offsetRight = control.OffsetRight;
        _offsetBottom = control.OffsetBottom;
        _growHorizontal = control.GrowHorizontal;
        _growVertical = control.GrowVertical;
        _scale = control.Scale;
        _pivotOffset = control.PivotOffset;
        _customMinimumSize = control.CustomMinimumSize;
        _sizeFlagsHorizontal = control.SizeFlagsHorizontal;
        _sizeFlagsVertical = control.SizeFlagsVertical;
        CaptureVisible = captureVisible;
        _visible = control.Visible;
        foreach (string name in ThemeConstantNames)
        {
            bool has = control.HasThemeConstantOverride(name);
            _themeConstants[name] = (has, has ? control.GetThemeConstant(name) : 0);
        }
    }

    public void RecordInitialVisibility(bool visible)
    {
        CaptureVisible = true;
        _visible = visible;
    }

    public void Restore(Control control)
    {
        control.AnchorLeft = _anchorLeft;
        control.AnchorTop = _anchorTop;
        control.AnchorRight = _anchorRight;
        control.AnchorBottom = _anchorBottom;
        control.OffsetLeft = _offsetLeft;
        control.OffsetTop = _offsetTop;
        control.OffsetRight = _offsetRight;
        control.OffsetBottom = _offsetBottom;
        control.GrowHorizontal = _growHorizontal;
        control.GrowVertical = _growVertical;
        control.Scale = _scale;
        control.PivotOffset = _pivotOffset;
        control.CustomMinimumSize = _customMinimumSize;
        control.SizeFlagsHorizontal = _sizeFlagsHorizontal;
        control.SizeFlagsVertical = _sizeFlagsVertical;
        if (CaptureVisible) control.Visible = _visible;
        foreach ((string name, (bool has, int val)) in _themeConstants)
        {
            if (has) control.AddThemeConstantOverride(name, val);
            else control.RemoveThemeConstantOverride(name);
        }
    }
}
