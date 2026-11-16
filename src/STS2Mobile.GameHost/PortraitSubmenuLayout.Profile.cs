using System;
using Godot;

namespace STS2Mobile.GameHost;

internal static partial class PortraitSubmenuLayout
{
    public const string ProfileTypeName = Ns + "ProfileScreen.NProfileScreen";

    /// <summary>
    /// The profiles at 920, drawn at 1.17 (the width fit drew them at 0.78), so a profile's 24 unit
    /// title reads at about 13 points.
    /// </summary>
    public const float ProfileDesignWidth = 920f;

    private const float ProfileButtonWidth = 440f;
    private const float ProfileButtonHeight = 560f;
    private const float ProfileDeleteSize = 80f;
    private const float ProfileDeleteGap = 12f;
    private const float ProfileColumnGap = 16f;
    private const float ProfileRowGap = 24f;

    /// <summary>
    /// The profiles: two buttons side by side with the third centred under them, each with its delete button under it as on
    /// the desktop, below the line asking to choose one and above the back button. The buttons stand in a row container on
    /// a desktop, which cannot be made two rows, so they move to the screen itself while in portrait
    /// (<see cref="MoveKept"/>). A button keeps its full width and close to its height (540 of 560 on the 11 Pro),
    /// so its panel art, drawn at its own aspect, holds the profile icon and title; an earlier layout of one column of three left
    /// each 420 tall, its art 305 wide inside the 440 button with the icon sticking out on the left.
    /// </summary>
    private static void LayoutProfile(Control screen, float w, float h, Action<Control> capture)
    {
        Place(screen.GetNodeOrNull<Control>("ChooseProfileMessage"), 16f, 40f, w - 32f, 97f, capture);

        if (screen.GetNodeOrNull<Control>("HBoxContainer") is not { } row) return;
        float top = 160f;
        float bottom = h - CornerButtonsTop - Gap;
        float under = ProfileDeleteGap + ProfileDeleteSize;
        float height = Math.Clamp((bottom - top - 2f * under - ProfileRowGap) / 2f, 300f, ProfileButtonHeight);
        float pairLeft = (w - 2f * ProfileButtonWidth - ProfileColumnGap) / 2f;
        for (int i = 0; i < 3; i++)
        {
            float x = i switch
            {
                0 => pairLeft,
                1 => pairLeft + ProfileButtonWidth + ProfileColumnGap,
                _ => (w - ProfileButtonWidth) / 2f,
            };
            float y = i < 2 ? top : top + height + under + ProfileRowGap;
            if (screen.GetNodeOrNull<Control>($"DeleteProfileButton{i + 1}") is { } delete)
                Place(delete, x + (ProfileButtonWidth - ProfileDeleteSize) / 2f, y + height + ProfileDeleteGap, ProfileDeleteSize, ProfileDeleteSize, capture);
            Control? button = row.GetNodeOrNull<Control>($"ProfileButton{i + 1}") ?? screen.GetNodeOrNull<Control>($"ProfileButton{i + 1}");
            if (button == null) continue;
            capture(button);
            MoveKept(button, screen, row.GetIndex() + 1 + i);
            var minimum = new Vector2(ProfileButtonWidth, height);
            if (button.CustomMinimumSize.DistanceSquaredTo(minimum) > 0.25f) button.CustomMinimumSize = minimum;
            var pivot = new Vector2(ProfileButtonWidth / 2f, height / 2f);
            if (button.PivotOffset.DistanceSquaredTo(pivot) > 0.25f) button.PivotOffset = pivot;
            Place(button, x, y, ProfileButtonWidth, height, capture);
        }
    }
}
