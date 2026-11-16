using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;

namespace STS2Mobile.GameHost;

/// <summary>
/// Main menu submenus laid out in one column for portrait, drawn about 1.5 times as large as the
/// width fit alone draws them (<see cref="PortraitScreenFit"/>). The screen is fitted at a narrow design width
/// (<see cref="DesignWidths"/>), so its virtual canvas is narrow and tall; its parts, which stand side by side on a desktop,
/// are then placed one under another on that canvas, using the height the desktop layout left empty. Each part keeps its
/// own size and inner layout. Every edit is captured first (the fit's snapshots and <see cref="RestoreLandscape"/>), so
/// landscape gets the game's layout back. Run history, the profiles, stats and the card library's sidebar have
/// one file each (<c>PortraitSubmenuLayout.*.cs</c>); edits the snapshots do not hold go through <see cref="SetKept"/>
/// (any property, such as a grid's columns or a font size override) and <see cref="MoveKept"/> (a node moved to another
/// parent), which put the desktop's value and parent back.
/// </summary>
internal static partial class PortraitSubmenuLayout
{
    private const string Ns = "MegaCrit.Sts2.Core.Nodes.Screens.";
    public const string CustomRunTypeName = Ns + "CustomRun.NCustomRunScreen";
    public const string DailyRunTypeName = Ns + "DailyRun.NDailyRunScreen";

    /// <summary>
    /// The design widths: 900 draws the custom and daily runs at 1.2 (the width fit drew them at 0.78 and 0.82), so their
    /// 24 unit text reads at 10 points. Each screen with a file of its own keeps its width beside its layout. A width under the
    /// canvas's 1080 draws the screen larger (<see cref="PortraitScreenFit"/>'s Enlarged list holds every screen here).
    /// </summary>
    public static readonly Dictionary<string, float> DesignWidths = new(StringComparer.Ordinal)
    {
        [CustomRunTypeName] = 900f,
        [DailyRunTypeName] = 900f,
        [RunHistoryTypeName] = RunHistoryDesignWidth,
        [ProfileTypeName] = ProfileDesignWidth,
        [StatsTypeName] = StatsDesignWidth,
        [CardLibraryTypeName] = CardLibraryDesignWidth,
        [PatchNotesTypeName] = PatchNotesDesignWidth,
        [BestiaryTypeName] = BestiaryDesignWidth,
    };

    // The unlock screens are several types with their widths in their own file; a static constructor runs after every
    // field initializer, whichever file holds it.
    static PortraitSubmenuLayout()
    {
        foreach ((string typeName, float designWidth) in UnlockScreenWidths()) DesignWidths[typeName] = designWidth;
    }

    // The back and confirm buttons stand in the lower corners, 354 to 244 units above the lower edge.
    private const float CornerButtonsTop = 354f;
    private const float Gap = 16f;

    private static readonly Dictionary<ulong, TextServer.AutowrapMode> OriginalWrap = new();

    public static void Apply(Control screen, string typeName, Action<Control> capture)
    {
        float width = screen.Size.X;
        float height = screen.Size.Y;
        if (width < 100f || height < 100f) return;
        switch (typeName)
        {
            case CustomRunTypeName: LayoutCustomRun(screen, width, height, capture); break;
            case DailyRunTypeName: LayoutDailyRun(screen, width, height, capture); break;
            case RunHistoryTypeName: LayoutRunHistory(screen, width, height, capture); break;
            case ProfileTypeName: LayoutProfile(screen, width, height, capture); break;
            case StatsTypeName: LayoutStats(screen, width, height, capture); break;
            case CardLibraryTypeName: LayoutCardLibrary(screen, width, height, capture); break;
            case PatchNotesTypeName: LayoutPatchNotes(screen, width, height, capture); break;
            case BestiaryTypeName: LayoutBestiary(screen, width, height, capture); break;
            case TimelineTypeName: LayoutTimeline(screen, width, capture); break;
            default:
                if (IsUnlockScreen(typeName)) LayoutUnlock(screen, typeName, width, height, capture);
                break;
        }
        KeepCornerButtons(screen);
    }

    /// <summary>
    /// The custom run: the title, the mode and seed, the ascension and the characters (the desktop's left half) above the
    /// modifiers (its right half), whose list scrolls down to the corner buttons.
    /// </summary>
    private static void LayoutCustomRun(Control screen, float w, float h, Action<Control> capture)
    {
        Fill(screen.GetNodeOrNull<Control>("LeftContainer"), w, h, capture);
        Fill(screen.GetNodeOrNull<Control>("RightContainer"), w, h, capture);

        float y = 0f;
        Place(screen.GetNodeOrNull<Control>("LeftContainer/CustomModeTitle"), 32f, y, w - 64f, 120f, capture);
        y += 120f;
        if (screen.GetNodeOrNull<Control>("LeftContainer/RemotePlayerContainer") is { Visible: true } remote)
        {
            Place(remote, (w - 625f) / 2f, y, 625f, 265f, capture);
            y += 265f + Gap;
        }
        // The mode dropdown stands over the row's first slot, 10 units in.
        float rowLeft = (w - 700f) / 2f;
        Place(screen.GetNodeOrNull<Control>("LeftContainer/HBoxContainer"), rowLeft, y, 700f, 64f, capture);
        Place(screen.GetNodeOrNull<Control>("LeftContainer/GameModeDropdown"), rowLeft + 10f, y + 1f, 320f, 64f, capture);
        y += 64f + Gap;
        if (screen.GetNodeOrNull<Control>("LeftContainer/AscensionPanel") is { Visible: true } ascension)
        {
            Place(ascension, (w - 634f) / 2f, y, 634f, 117f, capture);
            y += 117f + Gap;
        }
        // The character buttons hang from a 40 unit anchor: 218 units left of it to 257 right, 169 to 15 units above it.
        Place(screen.GetNodeOrNull<Control>("LeftContainer/CharSelectButtons"), w / 2f - 20f, y + 169f, 40f, 40f, capture);
        y += 154f + 2f * Gap;

        Place(screen.GetNodeOrNull<Control>("RightContainer/HBoxContainer"), 24f, y, w - 48f, 51f, capture);
        y += 55f;
        // The list's scrollbar stands at its right edge: 48 units, kept inside the screen.
        Place(screen.GetNodeOrNull<Control>("RightContainer/ModifiersList"), 16f, y, w - 64f, Math.Max(200f, h - CornerButtonsTop - Gap - y), capture);

        Place(screen.GetNodeOrNull<Control>("ReadyAndWaitingPanel"), (w - 736f) / 2f, h * 0.4f, 736f, 122f, capture);
        PlaceDisclaimer(screen, w, h, capture);
    }

    /// <summary>
    /// The daily run: the challenge (title, date, character, modifiers; the desktop's left half) above the leaderboard (its
    /// right half), which keeps its own size, centred.
    /// </summary>
    private static void LayoutDailyRun(Control screen, float w, float h, Action<Control> capture)
    {
        Fill(screen.GetNodeOrNull<Control>("ChallengeContainer"), w, h, capture);

        float y = 0f;
        Place(screen.GetNodeOrNull<Control>("ChallengeContainer/Title"), 0f, y, w, 120f, capture);
        y += 110f;
        Place(screen.GetNodeOrNull<Control>("ChallengeContainer/Date"), 0f, y, w, 36f, capture);
        y += 37f;
        Place(screen.GetNodeOrNull<Control>("ChallengeContainer/TimeLeft"), (w - 465f) / 2f, y, 465f, 31f, capture);
        y += 31f + Gap;
        if (screen.GetNodeOrNull<Control>("ChallengeContainer/CenterContainer") is { } center)
        {
            float centerHeight = Math.Max(64f, center.GetCombinedMinimumSize().Y);
            Place(center, (w - 844f) / 2f, y, 844f, centerHeight, capture);
            y += centerHeight + Gap;
        }
        float modifiersLeft = (w - 576f) / 2f;
        Place(screen.GetNodeOrNull<Control>("ChallengeContainer/ModifiersLabel"), modifiersLeft - 19f, y, 300f, 40f, capture);
        y += 48f;
        Place(screen.GetNodeOrNull<Control>("ChallengeContainer/ModifiersContainer"), modifiersLeft, y, 576f, 216f, capture);
        y += 216f + Gap;

        // The leaderboard is laid out on the desktop's right half (960 wide). Here it is the screen's width: its panel (224
        // to 736) stays near the middle and its title inside the edges.
        Place(screen.GetNodeOrNull<Control>("Leaderboards"), 0f, y, w, 953f, capture);
        Place(screen.GetNodeOrNull<Control>("ScoreWarning"), w - 110f, y + 50f, 94f, 79f, capture);
        Place(screen.GetNodeOrNull<Control>("ReadyAndWaitingPanel"), (w - 736f) / 2f, y + 400f, 736f, 122f, capture);
        PlaceDisclaimer(screen, w, h, capture);
    }

    /// <summary>
    /// The corner buttons place themselves from the game window's size in the screen's own units: in the corners on a
    /// desktop, inside the screen under the width fit (a screen drawn smaller), past the corners on a screen drawn larger.
    /// Their places come from the screen's size instead, and the window's next resize puts the game's back (landscape).
    /// The confirm button's picture, which runs 40 units past the corner on a desktop, stays inside. A back button waiting
    /// out of sight (the custom run's Unready) waits its whole width past the edge: the game's 220 units are less than
    /// its width on a screen drawn larger.
    /// </summary>
    private static void KeepCornerButtons(Control screen)
    {
        if (screen.GetNodeOrNull<Control>("ConfirmButton") is { } confirm && Get<Vector2>(confirm, "_posOffset") is Vector2 offset)
        {
            Vector2 show = screen.Size - offset;
            show.X = Math.Min(show.X, screen.Size.X - confirm.Size.X);
            SetPlaces(confirm, show, show + new Vector2(180f, 0f));
        }
        if (screen.GetNodeOrNull<Control>("UnreadyButton") is { } unready && Get<Vector2>(unready, "_posOffset") is Vector2 backOffset)
        {
            var show = new Vector2(-backOffset.X, screen.Size.Y - backOffset.Y);
            SetPlaces(unready, show, new Vector2(Math.Min(show.X - 180f, -unready.Size.X - 20f), show.Y));
        }
    }

    /// <summary>
    /// Gives a corner button its places, and puts it at the one it belongs at unless it is sliding: a slide started before
    /// the places changed ends at the old one.
    /// </summary>
    private static void SetPlaces(Control button, Vector2 show, Vector2 hide)
    {
        Field(button, "_showPos")?.SetValue(button, show);
        Field(button, "_hidePos")?.SetValue(button, hide);
        if (Get<Tween>(button, "_moveTween") is Tween tween && tween.IsValid() && tween.IsRunning()) return;
        Vector2 target = Get<bool>(button, "_isEnabled") is true ? show : hide;
        if (button.Position.DistanceSquaredTo(target) > 1f) button.Position = target;
    }

    private static readonly Dictionary<(Type, string), FieldInfo?> Fields = new();

    private static FieldInfo? Field(object target, string name)
    {
        Type type = target.GetType();
        if (!Fields.TryGetValue((type, name), out FieldInfo? field))
            Fields[(type, name)] = field = FindField(type, name);
        return field;
    }

    private static object? Get<T>(object target, string name) => Field(target, name)?.GetValue(target) is T value ? value : null;

    private static FieldInfo? FindField(Type? type, string name)
    {
        for (; type != null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } field)
                return field;
        return null;
    }

    /// <summary>The line under a custom or daily run, word wrapped across the narrow screen, under the corner buttons.</summary>
    private static void PlaceDisclaimer(Control screen, float w, float h, Action<Control> capture)
    {
        if (screen.GetNodeOrNull<Label>("Disclaimer") is not { } disclaimer) return;
        ulong id = disclaimer.GetInstanceId();
        if (!OriginalWrap.ContainsKey(id)) OriginalWrap[id] = disclaimer.AutowrapMode;
        if (disclaimer.AutowrapMode != TextServer.AutowrapMode.WordSmart) disclaimer.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        Place(disclaimer, 24f, h - 120f, w - 48f, 110f, capture);
    }

    /// <summary>A desktop half spread over the whole virtual canvas, so its children are placed in canvas units.</summary>
    private static void Fill(Control? control, float w, float h, Action<Control> capture) => Place(control, 0f, 0f, w, h, capture);

    /// <summary>Puts a control at a rectangle of its parent (the screen, or a half filling it), changing only what differs.</summary>
    private static void Place(Control? control, float x, float y, float w, float h, Action<Control> capture)
    {
        if (control == null) return;
        capture(control);
        if (control.AnchorLeft != 0f || control.AnchorTop != 0f || control.AnchorRight != 0f || control.AnchorBottom != 0f)
        {
            control.AnchorLeft = 0f;
            control.AnchorTop = 0f;
            control.AnchorRight = 0f;
            control.AnchorBottom = 0f;
        }
        if (MathF.Abs(control.OffsetLeft - x) > 0.5f) control.OffsetLeft = x;
        if (MathF.Abs(control.OffsetTop - y) > 0.5f) control.OffsetTop = y;
        if (MathF.Abs(control.OffsetRight - (x + w)) > 0.5f) control.OffsetRight = x + w;
        if (MathF.Abs(control.OffsetBottom - (y + h)) > 0.5f) control.OffsetBottom = y + h;
    }

    private static readonly Dictionary<(ulong Id, string Property), Variant> OriginalProperties = new();
    private static readonly List<(ulong Child, ulong Parent, int Index)> OriginalParents = new();

    /// <summary>
    /// Sets any property of a node (a grid's <c>columns</c>, a label's <c>autowrap_mode</c>, a font size override such as
    /// <c>theme_override_font_sizes/font_size</c>), keeping its desktop value for landscape, and changing only what
    /// differs, so it can run every frame. An override the desktop did not have is removed on the way back.
    /// </summary>
    private static void SetKept(GodotObject? target, string property, Variant value)
    {
        if (target == null) return;
        var key = (target.GetInstanceId(), property);
        Variant current = target.Get(property);
        if (!OriginalProperties.ContainsKey(key)) OriginalProperties[key] = current;
        if (!SameValue(current, value)) target.Set(property, value);
    }

    private static bool SameValue(Variant a, Variant b)
    {
        if (a.VariantType != b.VariantType) return false;
        return a.VariantType switch
        {
            Variant.Type.Nil => true,
            Variant.Type.Bool => a.AsBool() == b.AsBool(),
            Variant.Type.Int => a.AsInt64() == b.AsInt64(),
            Variant.Type.Float => Math.Abs(a.AsDouble() - b.AsDouble()) < 0.01,
            Variant.Type.Vector2 => a.AsVector2().DistanceSquaredTo(b.AsVector2()) < 0.01f,
            Variant.Type.Vector2I => a.AsVector2I() == b.AsVector2I(),
            Variant.Type.String => a.AsString() == b.AsString(),
            Variant.Type.Color => a.AsColor().IsEqualApprox(b.AsColor()),
            _ => false,
        };
    }

    /// <summary>
    /// Moves a node under another parent (at <paramref name="index"/>, or last), keeping its desktop parent and place
    /// among its siblings for landscape. Does nothing when it is already there, so it can run every frame. A container
    /// lays out its children, so this is how a row's items become free to stand in a column.
    /// </summary>
    private static void MoveKept(Node? child, Node? parent, int index = -1)
    {
        if (child == null || parent == null) return;
        Node? from = child.GetParent();
        if (from == parent || from == null) return;
        ulong id = child.GetInstanceId();
        if (!OriginalParents.Exists(entry => entry.Child == id))
            OriginalParents.Add((id, from.GetInstanceId(), child.GetIndex()));
        from.RemoveChild(child);
        parent.AddChild(child);
        if (index >= 0) parent.MoveChild(child, Math.Min(index, parent.GetChildCount() - 1));
    }

    public static void RestoreLandscape()
    {
        foreach ((ulong id, TextServer.AutowrapMode mode) in OriginalWrap)
            if (GodotObject.InstanceFromId(id) is Label label && GodotObject.IsInstanceValid(label)) label.AutowrapMode = mode;
        OriginalWrap.Clear();
        // Parents first, in reverse, so every child lands at its own index again.
        for (int i = OriginalParents.Count - 1; i >= 0; i--)
        {
            (ulong childId, ulong parentId, int index) = OriginalParents[i];
            if (GodotObject.InstanceFromId(childId) is not Node child || !GodotObject.IsInstanceValid(child)) continue;
            if (GodotObject.InstanceFromId(parentId) is not Node parent || !GodotObject.IsInstanceValid(parent)) continue;
            child.GetParent()?.RemoveChild(child);
            parent.AddChild(child);
            parent.MoveChild(child, Math.Min(index, parent.GetChildCount() - 1));
        }
        OriginalParents.Clear();
        foreach (((ulong id, string property), Variant value) in OriginalProperties)
        {
            if (GodotObject.InstanceFromId(id) is not GodotObject target || !GodotObject.IsInstanceValid(target)) continue;
            target.Set(property, value);
            // A game label sizes its text only when its box changes, and its box went back first: it sizes again now.
            if (property == "MaxFontSize")
            {
                target.Set("_needsResize", true);
                target.Call("AdjustFontSize");
            }
        }
        OriginalProperties.Clear();
    }

    /// <summary>Forgets kept values of nodes that are gone (a screen closed and freed).</summary>
    public static void PruneDead()
    {
        foreach (var key in OriginalProperties.Keys.Where(k => !GodotObject.IsInstanceIdValid(k.Id)).ToList()) OriginalProperties.Remove(key);
        OriginalParents.RemoveAll(entry => !GodotObject.IsInstanceIdValid(entry.Child));
        foreach (ulong id in OriginalWrap.Keys.Where(k => !GodotObject.IsInstanceIdValid(k)).ToList()) OriginalWrap.Remove(id);
    }
}
