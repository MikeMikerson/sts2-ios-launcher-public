using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace STS2Mobile.GameHost;

/// <summary>
/// Finds what does not fit the portrait canvas on the screen shown now: every visible text, button or clickable control
/// whose drawn rectangle reaches past the canvas edges, after clipping by any scroll or clipping ancestor. Proof runs log it at
/// each step, so "fits" is a measured result rather than only a look at a screenshot.
/// </summary>
internal static class PortraitAudit
{
    private const float Tolerance = 3f;
    private const float EdgeButtonOverhang = 45f;

    private static bool IsEdgeButton(Control control) =>
        control.GetType().Name is "NBackButton" or "NConfirmButton" or "NProceedButton";

    private static bool InScroller(Control control)
    {
        for (Node? node = control.GetParent(); node != null; node = node.GetParent())
        {
            if (node is ScrollContainer) return true;
            string name = node.GetType().Name;
            if (name.Contains("Grid", StringComparison.Ordinal) || name.Contains("Scroll", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    public static string Describe(Window root, int limit = 10)
    {
        Rect2 canvas = root.GetVisibleRect();
        var partial = new List<(float Amount, string Text)>();
        var outside = new List<string>();
        foreach (Node node in Descendants(root))
        {
            if (node is not Control control || !IsContent(control) || !control.IsVisibleInTree()) continue;
            if (EffectiveAlpha(control) < 0.05f) continue;
            if (!DrawnRect(control, out Rect2 rect)) continue;
            if (rect.Size.X < 2f || rect.Size.Y < 2f) continue;
            // Content in a scrolling grid may run past the top or bottom edge; only its sideways overflow counts.
            Rect2 bounds = InScroller(control) ? new Rect2(canvas.Position.X, rect.Position.Y, canvas.Size.X, rect.Size.Y) : canvas;
            float amount = PortraitFitMathOverflow(rect, bounds);
            // The game's edge buttons (back, confirm, proceed) hang their art 40 units past the screen edge by design.
            if (IsEdgeButton(control) && amount <= EdgeButtonOverhang && rect.Position.Y >= canvas.Position.Y && rect.End.Y <= canvas.End.Y) continue;
            if (amount <= Tolerance) continue;
            string label = $"{control.GetType().Name}:{control.Name}{TextOf(control)} [{rect.Position.X:F0},{rect.Position.Y:F0} {rect.Size.X:F0}x{rect.Size.Y:F0}]";
            if (!rect.Intersects(canvas)) outside.Add(label);
            else partial.Add((amount, label));
        }
        string partialText = string.Join("; ", partial.OrderByDescending(p => p.Amount).Take(limit).Select(p => $"{p.Amount:F0}px {p.Text}"));
        string outsideText = string.Join("; ", outside.Take(Math.Max(3, limit / 2)));
        return $"overflow={partial.Count} offscreen={outside.Count} canvas={canvas.Size.X:F0}x{canvas.Size.Y:F0}{DescribeHand(root, canvas)} | partial: {partialText} | off: {outsideText}";
    }

    private static readonly HashSet<string> CardGlowParts = new(StringComparer.Ordinal)
    {
        "Shadow", "Highlight", "Flash", "CardVfxContainer", "CardSparkles", "HandIndex",
    };

    /// <summary>
    /// The hand's cards are drawn art, not text or buttons, so the content check above missed a card cut off at the
    /// side edge. Each card in the hand counts by the union of its drawn parts (glow and shadow left out), against the canvas.
    /// </summary>
    private static string DescribeHand(Window root, Rect2 canvas)
    {
        var holders = Descendants(root).OfType<Control>()
            .Where(c => c.GetType().Name == "NHandCardHolder" && c.IsVisibleInTree() && !c.IsQueuedForDeletion()).ToList();
        if (holders.Count == 0) return "";
        int outside = 0;
        float worst = 0f;
        string worstLabel = "";
        foreach (Control holder in holders)
        {
            Rect2? union = null;
            foreach (Control part in CardParts(holder))
            {
                if (part.Size.X < 2f || part.Size.Y < 2f || EffectiveAlpha(part) < 0.05f) continue;
                Rect2 rect = Bounds(part);
                union = union is { } u ? u.Merge(rect) : rect;
            }
            if (union is not { } r) continue;
            float amount = PortraitFitMathOverflow(r, canvas);
            if (amount <= Tolerance) continue;
            outside++;
            if (amount > worst) { worst = amount; worstLabel = $"{holder.Name} [{r.Position.X:F0},{r.Position.Y:F0} {r.Size.X:F0}x{r.Size.Y:F0}]"; }
        }
        return outside == 0 ? $" hand={holders.Count} handout=0" : $" hand={holders.Count} handout={outside} worst={worst:F0}px {worstLabel}";
    }

    private static IEnumerable<Control> CardParts(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is not Control control || !control.Visible || CardGlowParts.Contains(control.Name.ToString())) continue;
            if (control.GetType().Name == "NCardHolderHitbox") continue;
            if (control is TextureRect or NinePatchRect or Label or RichTextLabel) yield return control;
            foreach (Control grandchild in CardParts(control)) yield return grandchild;
        }
    }

    // An iPhone 11 Pro is 375 points across the 1080-unit side of the canvas; Apple's guidelines ask for touch targets of at
    // least 44 points and text of at least 11.
    private const float PointsPerUnit = 375f / 1080f;
    private const float MinTargetPoints = 44f;
    private const float MinTextPoints = 11f;

    /// <summary>
    /// What is small for a finger or an eye on the screen shown now: visible touch targets whose shorter side is under 44
    /// points and visible text drawn under 11 points, each with its size in points, smallest first. Notes for later phone UI
    /// work, not failures: most of it is the desktop game's own sizing.
    /// </summary>
    public static string DescribeSmall(Window root, int limit = 24)
    {
        Rect2 canvas = root.GetVisibleRect();
        var targets = new Dictionary<string, (float Size, int Count)>();
        var texts = new Dictionary<string, (float Size, int Count)>();
        var targetSizes = new Dictionary<string, string>();
        void Note(Dictionary<string, (float Size, int Count)> into, string key, float size)
        {
            into[key] = into.TryGetValue(key, out (float Size, int Count) known) ? (Math.Min(known.Size, size), known.Count + 1) : (size, 1);
        }
        // Nodes the game makes at run time are named "@Control@123": the type alone names those.
        string Key(Control c) => c.Name.ToString().Contains('@') ? c.GetType().Name : $"{c.GetType().Name}:{c.Name}";
        foreach (Node node in Descendants(root))
        {
            if (node is not Control control || !IsContent(control) || !control.IsVisibleInTree()) continue;
            if (EffectiveAlpha(control) < 0.05f) continue;
            if (!DrawnRect(control, out Rect2 rect) || !rect.Intersects(canvas)) continue;
            if (rect.Size.X < 2f || rect.Size.Y < 2f) continue;
            if (control is Label or RichTextLabel)
            {
                int fontSize = control is Label ? control.GetThemeFontSize("font_size") : control.GetThemeFontSize("normal_font_size");
                float points = fontSize * Math.Abs(control.GetGlobalTransformWithCanvas().Scale.Y) * PointsPerUnit;
                if (points < MinTextPoints) Note(texts, $"{Key(control)}{TextOf(control)}", points);
            }
            else if (control.MouseFilter != Control.MouseFilterEnum.Ignore)
            {
                // A control with a larger touch area than its picture (PhoneHitArea) is as large as that area to a finger.
                if (PhoneHitArea.Of(control) is { } area && DrawnRect(area, out Rect2 areaRect))
                    rect = rect.Merge(areaRect);
                float points = Math.Min(rect.Size.X, rect.Size.Y) * PointsPerUnit;
                if (points < MinTargetPoints)
                {
                    string key = Key(control);
                    if (!targets.TryGetValue(key, out (float Size, int Count) known) || points < known.Size)
                        targetSizes[key] = $"{rect.Size.X * PointsPerUnit:F0}x{rect.Size.Y * PointsPerUnit:F0}";
                    Note(targets, key, points);
                }
            }
        }
        string List(Dictionary<string, (float Size, int Count)> from) => string.Join("; ", from.OrderBy(e => e.Value.Size).Take(limit)
            .Select(e => $"{e.Value.Size:F1}pt {e.Key}{(targetSizes.TryGetValue(e.Key, out string? box) && from == targets ? " " + box : "")}{(e.Value.Count > 1 ? $" x{e.Value.Count}" : "")}"));
        return $"targets={targets.Count} text={texts.Count} | targets: {List(targets)} | text: {List(texts)}";
    }

    private static float PortraitFitMathOverflow(Rect2 rect, Rect2 canvas) =>
        STS2Mobile.Platform.PortraitFitMath.Overflow(
            rect.Position.X, rect.Position.Y, rect.End.X, rect.End.Y, canvas.Position.X, canvas.Position.Y, canvas.End.X, canvas.End.Y);

    /// <summary>A control the small-target audit counts as something to touch (a button of the game's or the engine's).</summary>
    internal static bool IsTouchTarget(Control control) =>
        control is not (Label or RichTextLabel) && control.MouseFilter != Control.MouseFilterEnum.Ignore && IsContent(control);

    private static bool IsContent(Control control)
    {
        if (control is Label label) return label.Text.Trim().Length > 0;
        if (control is RichTextLabel rich) return rich.GetParsedText().Trim().Length > 0;
        if (control is BaseButton) return true;
        for (Type? type = control.GetType(); type != null && type != typeof(Control); type = type.BaseType)
            if (type.Name is "NClickableControl" or "NButton") return true;
        return false;
    }

    private static string TextOf(Control control)
    {
        string text = control switch
        {
            Label label => label.Text,
            RichTextLabel rich => rich.GetParsedText(),
            Button button => button.Text,
            _ => "",
        };
        text = text.Replace('\n', ' ').Trim();
        return text.Length == 0 ? "" : $" \"{(text.Length > 24 ? text[..24] : text)}\"";
    }

    private static float EffectiveAlpha(CanvasItem item)
    {
        float alpha = 1f;
        for (Node? node = item; node is CanvasItem canvasItem; node = node.GetParent())
        {
            alpha *= canvasItem.Modulate.A * canvasItem.SelfModulate.A;
            if (alpha < 0.05f) break;
        }
        return alpha;
    }

    /// <summary>The control's bounding box on the canvas, clipped by scroll containers and clipping ancestors.</summary>
    private static bool DrawnRect(Control control, out Rect2 rect)
    {
        rect = Bounds(control);
        for (Node? node = control.GetParent(); node is Control ancestor; node = node.GetParent())
        {
            if (ancestor is ScrollContainer || ancestor.ClipContents)
            {
                Rect2 clip = Bounds(ancestor);
                if (!rect.Intersects(clip)) return false;
                rect = rect.Intersection(clip);
            }
        }
        return true;
    }

    private static Rect2 Bounds(Control control)
    {
        Transform2D xform = control.GetGlobalTransformWithCanvas();
        Vector2 size = control.Size;
        Vector2[] corners = [xform * Vector2.Zero, xform * new Vector2(size.X, 0f), xform * size, xform * new Vector2(0f, size.Y)];
        float minX = corners.Min(c => c.X), minY = corners.Min(c => c.Y);
        float maxX = corners.Max(c => c.X), maxY = corners.Max(c => c.Y);
        return new Rect2(minX, minY, maxX - minX, maxY - minY);
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            yield return child;
            foreach (Node grandchild in Descendants(child)) yield return grandchild;
        }
    }
}
