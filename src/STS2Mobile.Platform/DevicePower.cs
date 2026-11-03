namespace STS2Mobile.Platform;

/// <summary>
/// The thermal state, render scale and battery iOS reports, read from the C functions in the app's
/// <c>native/power_state.mm</c>. Off the phone (tests, the Mac) those functions do not exist and
/// every reading is "unknown".
/// </summary>
public static unsafe class DevicePower
{
    public const int UnknownThermalState = -1;
    /// <summary>NSProcessInfoThermalState values.</summary>
    public const int Nominal = 0, Fair = 1, Serious = 2, Critical = 3;
    public const int UnknownBatteryPercent = -1;

    private static readonly delegate* unmanaged<int> ThermalFunction = (delegate* unmanaged<int>)NativeExports.Find("sts2_power_thermal_state");
    private static readonly delegate* unmanaged<float> ScaleFunction = (delegate* unmanaged<float>)NativeExports.Find("sts2_power_render_scale");
    private static readonly delegate* unmanaged<float> BatteryLevelFunction = (delegate* unmanaged<float>)NativeExports.Find("sts2_power_battery_level");
    private static readonly delegate* unmanaged<int> BatteryStateFunction = (delegate* unmanaged<int>)NativeExports.Find("sts2_power_battery_state");

    private static readonly delegate* unmanaged<double*, double*, int> TaskUsageFunction =
        (delegate* unmanaged<double*, double*, int>)NativeExports.Find("sts2_power_task_usage");

    /// <summary>What iOS counts against this app since it started, or -1 for each when unknown. It leaves out the GPU, the screen and the radios.</summary>
    public static (double CpuSeconds, double EnergyJoules) TaskUsage()
    {
        double cpu = -1, energy = -1;
        if (TaskUsageFunction == null || TaskUsageFunction(&cpu, &energy) == 0) return (-1, -1);
        return (cpu, energy);
    }

    /// <summary>0 to 100 in the steps iOS reports, or -1 when unknown (off the phone, and on the simulator).</summary>
    public static int BatteryPercent()
    {
        float level = BatteryLevelFunction != null ? BatteryLevelFunction() : -1;
        return level < 0 ? UnknownBatteryPercent : (int)Math.Round(level * 100);
    }

    /// <summary>The one word the session log uses for UIDeviceBatteryState.</summary>
    public static string BatteryState() => (BatteryStateFunction != null ? BatteryStateFunction() : 0) switch
    {
        1 => "unplugged",
        2 => "charging",
        3 => "full",
        _ => "unknown",
    };

    /// <summary>0 nominal, 1 fair, 2 serious, 3 critical, or -1 when unknown.</summary>
    public static int ThermalState() => ThermalFunction != null ? ThermalFunction() : UnknownThermalState;

    /// <summary>The fraction of the screen's resolution the app was started with, 1 when not reduced or unknown.</summary>
    public static double RenderScale() => ScaleFunction != null ? ScaleFunction() : 1;
}
