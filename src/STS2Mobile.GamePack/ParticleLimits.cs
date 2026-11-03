using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace STS2Mobile.GamePack;

/// <summary>
/// Limits how much particle pre-simulation a scene asks for in one frame, for the phone. It keeps the pre-simulated time, so
/// every effect starts in the same state, and lowers the simulation rate of only the emitters whose start would exceed the
/// limit. A port of tools/limit_particle_preprocess.py, which explains the measurements behind the numbers. Its output has
/// to stay byte for byte the same, so the arithmetic below follows Python's where the two differ.
/// </summary>
public static partial class ParticleLimits
{
    private const int StepLimit = 300;
    private const long SceneStepBudget = 3000;
    private const long DefaultRate = 30;
    private const string NoInterpolation = "interpolate = false";
    private static readonly int[] StepLimits = [StepLimit, 200, 150, 100, 60, 40, 30, 20];
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    [GeneratedRegex("^(\\[node name=\"([^\"\\n]*)\" type=\"GPUParticles(?:2D|3D)\"[^\\]\\n]*\\]\\n)(.*?)(?=^\\[|\\z)", RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Emitter();

    [GeneratedRegex("^preprocess = ([0-9.eE+-]+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Preprocess();

    [GeneratedRegex("^fixed_fps = (\\d+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex FixedFps();

    /// <summary>The scene's bytes with over-limit emitters slowed, or null if it has none.</summary>
    public static byte[]? AdjustScene(byte[] scene)
    {
        string text = StrictUtf8.GetString(scene);
        if (!text.Contains("type=\"GPUParticles", StringComparison.Ordinal)) return null;
        var (adjusted, changed) = Adjust(text, SceneLimit(text));
        return changed > 0 ? StrictUtf8.GetBytes(adjusted) : null;
    }

    /// <summary>The simulation rate that keeps this emitter's start within the limit, or null if it already is.</summary>
    public static long? LimitedRate(double preprocess, long rate, int stepLimit = StepLimit)
    {
        long effective = rate > 0 ? rate : DefaultRate;
        if (preprocess * effective <= stepLimit) return null;
        return Math.Max(1, (long)FloorDivide(stepLimit, preprocess));
    }

    /// <summary>The largest per-emitter limit that keeps the adjustable emitters within the scene budget, or the smallest.</summary>
    public static int SceneLimit(string text)
    {
        foreach (int limit in StepLimits)
            if (SceneSteps(text, limit, adjustableOnly: true) <= SceneStepBudget) return limit;
        return StepLimits[^1];
    }

    public static long SceneSteps(string text, int stepLimit, bool adjustableOnly = false)
    {
        long total = 0;
        foreach (Match emitter in Emitter().Matches(text))
        {
            string body = emitter.Groups[3].Value;
            var preprocess = Preprocess().Match(body);
            if (!preprocess.Success) continue;
            double seconds = ParseSeconds(preprocess);
            var stored = FixedFps().Match(body);
            long rate = stored.Success ? long.Parse(stored.Groups[1].Value, CultureInfo.InvariantCulture) : DefaultRate;
            if (!body.Contains(NoInterpolation, StringComparison.Ordinal)) rate = LimitedRate(seconds, rate, stepLimit) ?? rate;
            else if (adjustableOnly) continue;
            total += (long)(seconds * (rate > 0 ? rate : DefaultRate));
        }
        return total;
    }

    /// <returns>The scene with over-limit emitters slowed, and how many were.</returns>
    public static (string Text, int Changed) Adjust(string text, int stepLimit = StepLimit)
    {
        int changed = 0;
        string adjusted = Emitter().Replace(text, match =>
        {
            string header = match.Groups[1].Value, body = match.Groups[3].Value;
            if (body.Contains(NoInterpolation, StringComparison.Ordinal)) return match.Value; // a lower rate would be visible
            var preprocess = Preprocess().Match(body);
            if (!preprocess.Success) return match.Value;
            var stored = FixedFps().Match(body);
            long before = stored.Success ? long.Parse(stored.Groups[1].Value, CultureInfo.InvariantCulture) : DefaultRate;
            if (LimitedRate(ParseSeconds(preprocess), before, stepLimit) is not { } after) return match.Value;
            changed++;
            string rate = "fixed_fps = " + after.ToString(CultureInfo.InvariantCulture);
            return header + (stored.Success ? FixedFps().Replace(body, rate) : rate + "\n" + body);
        });
        return (adjusted, changed);
    }

    private static double ParseSeconds(Match preprocess) => double.Parse(preprocess.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>
    /// Python's <c>//</c> on positive floats. It can differ from <c>Math.Floor(a / b)</c>: 300 // 0.1 is 2999, while 300 / 0.1
    /// rounds to 3000.
    /// </summary>
    public static double FloorDivide(double dividend, double divisor)
    {
        double division = (dividend - dividend % divisor) / divisor;
        double floor = Math.Floor(division);
        return division - floor > 0.5 ? floor + 1 : floor;
    }
}
