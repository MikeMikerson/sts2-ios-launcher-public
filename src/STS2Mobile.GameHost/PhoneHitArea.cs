using Godot;

namespace STS2Mobile.GameHost;

/// <summary>
/// A larger touch area than a control's picture: an invisible child that reaches past the control's box and
/// passes what it gets to the control. The game's buttons follow the pointer entering and leaving and take presses
/// through gui input, both of which reach a parent through a child that passes them on, so the control behaves as if
/// it were that large. Use it on a plain control; a container would lay the child out as one of its own.
/// Whether a control is a container is asked of the engine (IsContainer): many of the game's buttons are container
/// nodes whose script class derives from a plain control, so a C# type test says no for them.
/// </summary>
internal static class PhoneHitArea
{
    public const string NodeName = "PhoneHitArea";

    /// <summary>The child a container's touch area hangs from (the container lays the holder out, not the area).</summary>
    public const string HolderName = "PortraitHitPadHolder";

    /// <summary>The control's larger touch area, when it has one that is shown.</summary>
    public static Control? Of(Control target) =>
        (target.GetNodeOrNull<Control>(NodeName) ?? target.GetNodeOrNull<Control>(HolderName + "/" + NodeName)) is { } area
        && area.IsVisibleInTree() ? area : null;

    /// <summary>Whether the engine lays this control's children out itself, whatever its script class derives from.</summary>
    public static bool IsContainer(Control control) => control.IsClass("Container");

    /// <summary>Makes the touch area reach this far past each side of the control's box, in the control's own units.</summary>
    public static void Grow(Control target, float left, float top, float right, float bottom)
    {
        if (IsContainer(target)) return;
        Control? area = target.GetNodeOrNull<Control>(NodeName);
        if (area == null)
        {
            area = new Control
            {
                Name = NodeName,
                MouseFilter = Control.MouseFilterEnum.Pass,
                FocusMode = Control.FocusModeEnum.None,
            };
            target.AddChild(area);
        }
        area.Visible = true;
        area.AnchorLeft = 0f;
        area.AnchorTop = 0f;
        area.AnchorRight = 1f;
        area.AnchorBottom = 1f;
        area.OffsetLeft = -left;
        area.OffsetTop = -top;
        area.OffsetRight = right;
        area.OffsetBottom = bottom;
    }

    /// <summary>Takes the larger touch area away again (landscape).</summary>
    public static void Remove(Control target)
    {
        if (target.GetNodeOrNull<Control>(NodeName) is { } area)
            area.Visible = false;
    }
}
