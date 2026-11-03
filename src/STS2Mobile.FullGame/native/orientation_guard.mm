// Keeps the app the way up the managed side asked for: the launcher in portrait, the game in landscape.
//
// The exported Info.plist lists portrait and both landscapes, and the engine's view controller allows them all, so this file decides.
// On a physical iPhone a landscape-only app was still laid out as 1125 x 2436 at launch and again after app switches, so
// a declaration alone is not enough. This answers on the one level the engine leaves unanswered (the app delegate) and, whenever the
// active scene is found the other way up, asks iOS to turn it.
//
// The managed host (STS2Mobile.Platform/ScreenOrientation.cs) calls sts2_orientation_set by name, on the main thread.
#import <UIKit/UIKit.h>
#import <objc/runtime.h>

static NSString *const kEngineDelegateClass = @"GDTApplicationDelegate";

// The launcher comes first, so the app starts in portrait.
static UIInterfaceOrientationMask gMask = UIInterfaceOrientationMaskPortrait;

static UIInterfaceOrientationMask STS2SupportedOrientations(id self, SEL _cmd, UIApplication *application, UIWindow *window) {
	return gMask;
}

static BOOL STS2SceneIsTheWayAsked(UIWindowScene *scene) {
	CGSize size = scene.coordinateSpace.bounds.size;
	if (gMask == UIInterfaceOrientationMaskLandscape) {
		return UIInterfaceOrientationIsLandscape(scene.interfaceOrientation) && size.width >= size.height;
	}
	return UIInterfaceOrientationIsPortrait(scene.interfaceOrientation) && size.height >= size.width;
}

#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
// Asks for the wanted orientation while the app is in front and a scene is laid out the other way.
static void STS2KeepOrientation(void) {
	UIApplication *application = UIApplication.sharedApplication;
	if (application.applicationState != UIApplicationStateActive) {
		return;
	}
	for (UIScene *scene in application.connectedScenes) {
		if (![scene isKindOfClass:UIWindowScene.class]) {
			continue;
		}
		UIWindowScene *windowScene = (UIWindowScene *)scene;
		if (STS2SceneIsTheWayAsked(windowScene)) {
			continue;
		}
		if (@available(iOS 16.0, *)) {
			for (UIWindow *window in windowScene.windows) {
				[window.rootViewController setNeedsUpdateOfSupportedInterfaceOrientations];
			}
			UIWindowSceneGeometryPreferencesIOS *preferences = [[UIWindowSceneGeometryPreferencesIOS alloc] initWithInterfaceOrientations:gMask];
			[windowScene requestGeometryUpdateWithPreferences:preferences
										 errorHandler:^(NSError *error) {
											 NSLog(@"Orientation request refused: %@", error);
										 }];
		} else {
			[UIViewController attemptRotationToDeviceOrientation];
		}
	}
}
#pragma clang diagnostic pop

// 1 for landscape (the game), 0 for portrait (the launcher). Takes effect at once, and is kept from then on.
extern "C" __attribute__((visibility("default"))) void sts2_orientation_set(int landscape) {
	gMask = landscape ? UIInterfaceOrientationMaskLandscape : UIInterfaceOrientationMaskPortrait;
	STS2KeepOrientation();
}

// Holding the function here keeps the linker from removing it: nothing in the app calls it directly, the managed host finds it by name.
__attribute__((used)) static void *volatile gOrientationExports[] = {(void *)&sts2_orientation_set};

@interface STS2OrientationGuard : NSObject
@end

@implementation STS2OrientationGuard

// Runs when the app image is loaded, before UIKit creates the engine's delegate and asks it anything.
+ (void)load {
	Class delegate = NSClassFromString(kEngineDelegateClass);
	if (delegate) {
		// Type encoding of -application:supportedInterfaceOrientationsForWindow: (returns NSUInteger).
		class_addMethod(delegate, @selector(application:supportedInterfaceOrientationsForWindow:), (IMP)STS2SupportedOrientations, "Q@:@@");
	}
	NSNotificationCenter *center = NSNotificationCenter.defaultCenter;
	for (NSNotificationName name in @[ UIApplicationDidFinishLaunchingNotification, UIApplicationDidBecomeActiveNotification,
				 UIApplicationWillResignActiveNotification, UIDeviceOrientationDidChangeNotification ]) {
		[center addObserverForName:name
							object:nil
							 queue:NSOperationQueue.mainQueue
						usingBlock:^(NSNotification *notification) {
							STS2KeepOrientation();
						}];
	}
	// A scene can also be resized with no notification the app sees, so look once a second as well.
	[center addObserverForName:UIApplicationDidFinishLaunchingNotification
						object:nil
						 queue:NSOperationQueue.mainQueue
					usingBlock:^(NSNotification *notification) {
						[NSTimer scheduledTimerWithTimeInterval:1.0
														repeats:YES
														  block:^(NSTimer *timer) {
															  STS2KeepOrientation();
														  }];
					}];
}

@end
