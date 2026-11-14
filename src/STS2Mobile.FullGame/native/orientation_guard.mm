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
static NSString *const kEngineViewControllerClass = @"GDTViewController";

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

// Cards are dragged up from the bottom edge, which is also the iOS gesture for leaving an app. While the game is up, every
// edge is held back: a first swipe goes to the game and only a second one leaves. The engine answers this from a project
// setting, but a single swipe still left the game on the phone, so the answer is given here and iOS is asked to read it
// again whenever it may have read the old one. The launcher, in portrait, leaves the gestures to iOS.
static UIRectEdge STS2DeferredEdges(id self, SEL _cmd) {
	return gMask == UIInterfaceOrientationMaskLandscape ? UIRectEdgeAll : UIRectEdgeNone;
}

// The home indicator stays visible: with it hidden, iOS does not hold the bottom edge back.
static BOOL STS2HidesHomeIndicator(id self, SEL _cmd) {
	return NO;
}

static void STS2RefreshSystemGestures(void) {
	for (UIScene *scene in UIApplication.sharedApplication.connectedScenes) {
		if (![scene isKindOfClass:UIWindowScene.class]) {
			continue;
		}
		for (UIWindow *window in ((UIWindowScene *)scene).windows) {
			UIViewController *controller = window.rootViewController;
			[controller setNeedsUpdateOfScreenEdgesDeferringSystemGestures];
			[controller setNeedsUpdateOfHomeIndicatorAutoHidden];
		}
	}
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
	STS2RefreshSystemGestures();
}

// What iOS is told about the edge gestures, for the game's log: the key window's root controller, the edges it holds back
// (15 is all four) and whether it hides the home indicator. Called on the main thread; the text lasts until the next call.
extern "C" __attribute__((visibility("default"))) const char *sts2_gestures_state(void) {
	static char text[160];
	UIViewController *controller = nil;
	for (UIScene *scene in UIApplication.sharedApplication.connectedScenes) {
		if (![scene isKindOfClass:UIWindowScene.class]) {
			continue;
		}
		for (UIWindow *window in ((UIWindowScene *)scene).windows) {
			if (window.isKeyWindow) {
				controller = window.rootViewController;
			}
		}
	}
	if (!controller) {
		snprintf(text, sizeof text, "no key window");
		return text;
	}
	snprintf(text, sizeof text, "controller %s, deferred edges %lu, home indicator hidden %d",
			 class_getName(object_getClass(controller)), (unsigned long)controller.preferredScreenEdgesDeferringSystemGestures,
			 (int)controller.prefersHomeIndicatorAutoHidden);
	return text;
}

// The game's side margin in landscape (STS2Mobile.Platform/SafeAreaCanvas.cs): the larger of the key window's left and right
// safe-area insets, as a fraction of its width. iOS reports the same inset on both sides of a phone with a notch or an island,
// and it also clears the rounded corners. 0 in portrait, before there is a window, and on a screen with no inset. Called on the
// main thread, where the engine runs the game.
extern "C" __attribute__((visibility("default"))) double sts2_screen_side_inset(void) {
	for (UIScene *scene in UIApplication.sharedApplication.connectedScenes) {
		if (![scene isKindOfClass:UIWindowScene.class]) {
			continue;
		}
		for (UIWindow *window in ((UIWindowScene *)scene).windows) {
			CGSize size = window.bounds.size;
			if (!window.isKeyWindow || size.width <= size.height) {
				continue;
			}
			UIEdgeInsets insets = window.safeAreaInsets;
			return MAX(insets.left, insets.right) / size.width;
		}
	}
	return 0;
}

// Holding the functions here keeps the linker from removing them: nothing in the app calls them directly, the managed side finds them by name.
__attribute__((used)) static void *volatile gOrientationExports[] = {(void *)&sts2_orientation_set, (void *)&sts2_screen_side_inset,
																	  (void *)&sts2_gestures_state};

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
	Class controller = NSClassFromString(kEngineViewControllerClass);
	if (controller) {
		// Replaces the engine's own answers (or adds them if it has none). Both return NSUInteger / BOOL.
		class_replaceMethod(controller, @selector(preferredScreenEdgesDeferringSystemGestures), (IMP)STS2DeferredEdges, "Q@:");
		class_replaceMethod(controller, @selector(prefersHomeIndicatorAutoHidden), (IMP)STS2HidesHomeIndicator, "B@:");
	}
	NSNotificationCenter *center = NSNotificationCenter.defaultCenter;
	for (NSNotificationName name in @[ UIApplicationDidFinishLaunchingNotification, UIApplicationDidBecomeActiveNotification,
				 UIApplicationWillResignActiveNotification, UIDeviceOrientationDidChangeNotification ]) {
		[center addObserverForName:name
							object:nil
							 queue:NSOperationQueue.mainQueue
						usingBlock:^(NSNotification *notification) {
							STS2KeepOrientation();
							STS2RefreshSystemGestures();
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
