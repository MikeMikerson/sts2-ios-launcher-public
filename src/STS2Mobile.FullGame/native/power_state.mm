// Heat, battery and render resolution: what iOS reports, and the phone's reduced render resolution.
//
// The managed host (STS2Mobile.Platform/DevicePower.cs) looks the sts2_power_* functions up in the
// app by name: the thermal state drives the "cool down when hot" choice, and the session log
// (SessionStats.cs) records the battery.
//
// Render resolution: the engine sizes its drawing surface, maps touches and computes the safe area
// from UIScreen.scale (Godot 4.5.1 drivers/apple_embedded and platform/ios). Reporting a smaller
// scale to the app before the engine starts therefore makes it draw fewer pixels with every one of
// those staying consistent, and iOS scales the picture up to the screen. The choice is read from
// Documents/phone-graphics.json (written by PhoneGraphicsSettings.cs, key "renderScale") once,
// before main, so a change takes effect at the next launch. Without the file nothing is changed.
#import <UIKit/UIKit.h>
#import <mach/mach.h>
#import <objc/runtime.h>
#import <sys/resource.h>

static CGFloat gRenderScale = 1.0;
static IMP gScreenScale = NULL;

static CGFloat STS2ReadRenderScale(void) {
	NSString *documents = NSSearchPathForDirectoriesInDomains(NSDocumentDirectory, NSUserDomainMask, YES).firstObject;
	NSData *data = [NSData dataWithContentsOfFile:[documents stringByAppendingPathComponent:@"phone-graphics.json"]];
	if (!data) {
		return 1.0;
	}
	id root = [NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
	id value = [root isKindOfClass:[NSDictionary class]] ? root[@"renderScale"] : nil;
	if (![value isKindOfClass:[NSNumber class]]) {
		return 1.0;
	}
	double scale = [value doubleValue];
	// Never below half the screen's resolution, never above it.
	return (scale >= 0.5 && scale < 1.0) ? scale : 1.0;
}

static CGFloat STS2ScreenScale(id self, SEL _cmd) {
	CGFloat scale = ((CGFloat (*)(id, SEL))gScreenScale)(self, _cmd);
	return scale * gRenderScale;
}

extern "C" __attribute__((visibility("default"))) int sts2_power_thermal_state(void) {
	return (int)[NSProcessInfo processInfo].thermalState;
}

extern "C" __attribute__((visibility("default"))) float sts2_power_render_scale(void) {
	return (float)gRenderScale;
}

// Battery, for the session log. UIDevice reports nothing until monitoring is turned on.
static void STS2EnableBatteryMonitoring(void) {
	static dispatch_once_t once;
	dispatch_once(&once, ^{
		[UIDevice currentDevice].batteryMonitoringEnabled = YES;
	});
}

// 0 to 1, or -1 when unknown (the simulator).
extern "C" __attribute__((visibility("default"))) float sts2_power_battery_level(void) {
	STS2EnableBatteryMonitoring();
	return [UIDevice currentDevice].batteryLevel;
}

// UIDeviceBatteryState: 0 unknown, 1 unplugged, 2 charging, 3 full.
extern "C" __attribute__((visibility("default"))) int sts2_power_battery_state(void) {
	STS2EnableBatteryMonitoring();
	return (int)[UIDevice currentDevice].batteryState;
}

// What iOS counts against this app since it started, for comparing settings in runs too short for the
// battery level to move. It leaves out the GPU (iOS reports its time as 0 here), the screen and the radios.
extern "C" __attribute__((visibility("default"))) int sts2_power_task_usage(double *cpuSeconds, double *energyJoules) {
	struct rusage usage;
	task_power_info_v2_data_t power;
	mach_msg_type_number_t count = TASK_POWER_INFO_V2_COUNT;
	if (getrusage(RUSAGE_SELF, &usage) != 0 || task_info(mach_task_self(), TASK_POWER_INFO_V2, (task_info_t)&power, &count) != KERN_SUCCESS) {
		return 0;
	}
	*cpuSeconds = usage.ru_utime.tv_sec + usage.ru_utime.tv_usec / 1e6 + usage.ru_stime.tv_sec + usage.ru_stime.tv_usec / 1e6;
	*energyJoules = power.task_energy / 1e9;
	return 1;
}

// Holding the functions here keeps the linker from removing them: nothing in the app calls them
// directly, the managed host finds them by name.
__attribute__((used)) static void *volatile gPowerExports[] = {
	(void *)&sts2_power_thermal_state, (void *)&sts2_power_render_scale,
	(void *)&sts2_power_battery_level, (void *)&sts2_power_battery_state,
	(void *)&sts2_power_task_usage
};

__attribute__((constructor)) static void STS2InstallRenderScale(void) {
	@autoreleasepool {
		CGFloat scale = STS2ReadRenderScale();
		if (scale >= 1.0) {
			return;
		}
		Method method = class_getInstanceMethod([UIScreen class], @selector(scale));
		if (!method) {
			return;
		}
		gRenderScale = scale;
		gScreenScale = method_setImplementation(method, (IMP)STS2ScreenScale);
	}
}
