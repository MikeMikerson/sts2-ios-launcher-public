// Lets the device app's renderer run on the simulator's Metal. Only the simulator app loads this (tools/mono/simulator_app.py).
//
// The simulator's GPU answers as Apple2 and Metal's own checks stop there, but it passes the work to the Mac's GPU, which can do it.
// So this answers as Apple6, the iPhone 11 Pro's family, and turns off the checks. If the simulator's GPU cannot do something,
// the renderer fails or draws wrongly here and not on the phone.
#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#import <objc/runtime.h>

static IMP original_supports_family;

static BOOL supports_family(id self, SEL command, MTLGPUFamily family) {
    if (family >= MTLGPUFamilyApple1 && family <= MTLGPUFamilyApple6) {
        return YES;
    }
    return ((BOOL (*)(id, SEL, MTLGPUFamily))original_supports_family)(self, command, family);
}

static BOOL support_for_feature(id self, SEL command, unsigned long long feature) {
    return YES;
}

// Godot calls this to pace frames, and the simulator's command buffer does not have it.
static void present_after_minimum_duration(id self, SEL command, id drawable, CFTimeInterval duration) {
    [(id<MTLCommandBuffer>)self presentDrawable:drawable];
}

__attribute__((constructor)) static void install(void) {
    id device = MTLCreateSystemDefaultDevice();
    original_supports_family = method_setImplementation(class_getInstanceMethod([device class], @selector(supportsFamily:)), (IMP)supports_family);

    // Metal's MTLValidateFeatureSupport asks this private object, and aborts on the first cube array texture.
    id queries = [device valueForKey:@"featureQueries"];
    Method feature = class_getInstanceMethod([queries class], NSSelectorFromString(@"supportForFeature:"));
    if (feature) {
        method_setImplementation(feature, (IMP)support_for_feature);
    }

    id buffer = [[device newCommandQueue] commandBuffer];
    class_addMethod([buffer class], NSSelectorFromString(@"presentDrawable:afterMinimumDuration:"), (IMP)present_after_minimum_duration, "v@:@d");
}
