using System;
using Godot;

namespace STS2Mobile.FullGame;

/// <summary>
/// The launcher's look in one place: its colours, its three typefaces and a Godot theme built from them,
/// plus the few kinds of control it is made of. The fonts are under the SIL Open Font License, in <c>fonts/</c> with their licences.
/// No game art, font or icon is used.
/// </summary>
public static class LauncherTheme
{
    // The portrait canvas: 1080 units across, scaled to the phone, so the sizes below are in those units.
    public static readonly Vector2I PortraitCanvas = new(1080, 1920);

    /// <summary>The background: indigo ink, the colour of the sky behind the spire.</summary>
    public static readonly Color Dusk = new("1e1b2e");
    /// <summary>Surfaces: fields, quiet buttons, the empty part of a bar.</summary>
    public static readonly Color Stone = new("2a2640");
    /// <summary>Hairlines and the outline of what is still to come.</summary>
    public static readonly Color Line = new("4a4466");
    /// <summary>Text.</summary>
    public static readonly Color Bone = new("ede6d6");
    /// <summary>Secondary text: values, the wordmark, steps not yet reached.</summary>
    public static readonly Color Mist = new("a9a2bf");
    /// <summary>The one action each screen leads to, and the step under way. The game's gold.</summary>
    public static readonly Color Ember = new("e9b44c");
    /// <summary>Actions that delete something.</summary>
    public static readonly Color Blood = new("d0574e");
    /// <summary>Steps that are done.</summary>
    public static readonly Color Moss = new("8db580");

    public const int WordmarkSize = 46;
    public const int HeadingSize = 96;
    public const int BodySize = 48;
    public const int SmallSize = 40;
    public const int ButtonSize = 50;
    public const int StepSize = 44;
    public const float TouchHeight = 150;
    public const float RowHeight = 168;
    public const int SidePadding = 64;

    public const string PrimaryButton = "PrimaryButton";
    public const string DangerButton = "DangerButton";
    public const string QuietButton = "QuietButton";
    public const string RowButton = "RowButton";

    private static readonly Lazy<Fonts> Loaded = new(LoadFonts);

    public static Font Display => Loaded.Value.Display;
    public static Font Body => Loaded.Value.Body;
    public static Font BodyStrong => Loaded.Value.BodyStrong;
    public static Font Figures => Loaded.Value.Figures;

    /// <summary>The theme every launcher control inherits from its root.</summary>
    public static Theme Create()
    {
        var theme = new Theme { DefaultFont = Body, DefaultFontSize = BodySize };

        theme.SetColor("font_color", "Label", Bone);

        theme.SetTypeVariation(PrimaryButton, "Button");
        theme.SetTypeVariation(DangerButton, "Button");
        theme.SetTypeVariation(QuietButton, "Button");
        theme.SetTypeVariation(RowButton, "Button");
        StyleButton(theme, "Button", Stone, Line, Bone, ButtonSize);
        StyleButton(theme, PrimaryButton, Ember, Ember, Dusk, ButtonSize);
        StyleButton(theme, DangerButton, Stone, Blood, Blood, ButtonSize);
        StyleQuiet(theme);
        StyleRow(theme);

        var field = Box(Stone, Line, 2, 10);
        field.ContentMarginLeft = field.ContentMarginRight = 32;
        var focused = Box(Stone, Ember, 3, 10);
        focused.ContentMarginLeft = focused.ContentMarginRight = 32;
        theme.SetStylebox("normal", "LineEdit", field);
        theme.SetStylebox("focus", "LineEdit", focused);
        theme.SetStylebox("read_only", "LineEdit", field);
        theme.SetColor("font_color", "LineEdit", Bone);
        theme.SetColor("font_placeholder_color", "LineEdit", Mist);
        theme.SetColor("caret_color", "LineEdit", Ember);
        theme.SetColor("selection_color", "LineEdit", Line);
        theme.SetFontSize("font_size", "LineEdit", BodySize);

        theme.SetStylebox("background", "ProgressBar", Box(Stone, Stone, 0, 6));
        theme.SetStylebox("fill", "ProgressBar", Box(Ember, Ember, 0, 6));
        return theme;
    }

    public static Label Text(string text, Font font, int size, Color color)
    {
        var label = new Label { Text = text };
        label.AddThemeFontOverride("font", font);
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    public static Godot.Button MakeButton(string text, string variation, Action pressed)
    {
        var button = new Godot.Button { Text = text, ThemeTypeVariation = variation, CustomMinimumSize = new Vector2(0, TouchHeight) };
        button.Pressed += pressed;
        return button;
    }

    /// <summary>The engine's own font, for a build whose fonts did not import: the launcher must still work.</summary>
    private static Fonts LoadFonts()
    {
        Font Load(string name)
        {
            var font = ResourceLoader.Exists(name) ? ResourceLoader.Load<FontFile>(name) : null;
            if (font == null) GD.PushWarning("Launcher font missing, using the engine's: " + name);
            return font ?? ThemeDB.FallbackFont;
        }
        Font display = Load("res://fonts/Grenze.ttf"), body = Load("res://fonts/AtkinsonHyperlegibleNext.ttf"), figures = Load("res://fonts/AtkinsonHyperlegibleMono.ttf");
        return new Fonts(Weight(display, 600), Weight(body, 400), Weight(body, 650), Weight(figures, 450));
    }

    /// <summary>One weight of a variable font.</summary>
    private static Font Weight(Font font, int weight)
    {
        if (font is not FontFile) return font;
        long tag = TextServerManager.GetPrimaryInterface().NameToTag("wght");
        return new FontVariation { BaseFont = font, VariationOpentype = new Godot.Collections.Dictionary { { tag, weight } } };
    }

    private static void StyleButton(Theme theme, string type, Color fill, Color border, Color text, int size)
    {
        var normal = Box(fill, border, 2, 10);
        var pressed = Box(fill.Lerp(Dusk, 0.25f), border, 2, 10);
        theme.SetStylebox("normal", type, normal);
        theme.SetStylebox("hover", type, normal);
        theme.SetStylebox("pressed", type, pressed);
        theme.SetStylebox("hover_pressed", type, pressed);
        theme.SetStylebox("disabled", type, Box(Stone, Stone, 2, 10));
        theme.SetStylebox("focus", type, new StyleBoxEmpty());
        foreach (string state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color", "font_focus_color" })
            theme.SetColor(state, type, text);
        theme.SetColor("font_disabled_color", type, Mist);
        theme.SetFont("font", type, BodyStrong);
        theme.SetFontSize("font_size", type, size);
    }

    /// <summary>Back: text only, so it reads as a way out and not as a choice.</summary>
    private static void StyleQuiet(Theme theme)
    {
        var none = new StyleBoxEmpty();
        foreach (string state in new[] { "normal", "hover", "pressed", "hover_pressed", "focus", "disabled" }) theme.SetStylebox(state, QuietButton, none);
        foreach (string state in new[] { "font_color", "font_hover_color", "font_focus_color", "font_disabled_color" }) theme.SetColor(state, QuietButton, Mist);
        theme.SetColor("font_pressed_color", QuietButton, Bone);
        theme.SetColor("font_hover_pressed_color", QuietButton, Bone);
        theme.SetFont("font", QuietButton, Body);
        theme.SetFontSize("font_size", QuietButton, ButtonSize);
    }

    /// <summary>A home row: the label on the left, a hairline under it, nothing boxed.</summary>
    private static void StyleRow(Theme theme)
    {
        StyleBoxFlat Row(Color fill)
        {
            var box = new StyleBoxFlat { BgColor = fill, BorderColor = Line, BorderWidthBottom = 2, ContentMarginLeft = 8, ContentMarginRight = 8 };
            return box;
        }
        theme.SetStylebox("normal", RowButton, Row(Dusk));
        theme.SetStylebox("hover", RowButton, Row(Dusk));
        theme.SetStylebox("pressed", RowButton, Row(Stone));
        theme.SetStylebox("hover_pressed", RowButton, Row(Stone));
        theme.SetStylebox("focus", RowButton, new StyleBoxEmpty());
        foreach (string state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color", "font_focus_color" })
            theme.SetColor(state, RowButton, Bone);
        theme.SetFont("font", RowButton, BodyStrong);
        theme.SetFontSize("font_size", RowButton, ButtonSize);
        theme.SetConstant("h_separation", RowButton, 0);
    }

    private static StyleBoxFlat Box(Color fill, Color border, int borderWidth, int radius) => new()
    {
        BgColor = fill,
        BorderColor = border,
        BorderWidthLeft = borderWidth,
        BorderWidthTop = borderWidth,
        BorderWidthRight = borderWidth,
        BorderWidthBottom = borderWidth,
        CornerRadiusTopLeft = radius,
        CornerRadiusTopRight = radius,
        CornerRadiusBottomLeft = radius,
        CornerRadiusBottomRight = radius,
        ContentMarginLeft = 24,
        ContentMarginRight = 24,
        ContentMarginTop = 12,
        ContentMarginBottom = 12,
    };

    private sealed record Fonts(Font Display, Font Body, Font BodyStrong, Font Figures);
}
