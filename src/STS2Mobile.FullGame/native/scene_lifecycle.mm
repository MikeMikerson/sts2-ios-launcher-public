// Gives the engine's window a UIKit scene, which iOS 27 requires.
//
// An app built with the iOS 27 SDK must use the scene-based life cycle, or iOS stops it during launch
// (`_UIApplicationEvaluateRuntimeIssueForNoSceneLifecycleAdoption`, seen on iOS 27.0.1). Godot 4.5.1
// predates that: its app delegate service creates the window itself in
// application:didFinishLaunchingWithOptions: and reads it back through the delegate's window
// property. The Info.plist names this class as the scene delegate (tools/prepare_full_game_xcode.py).
// When iOS connects the scene, the window the engine already made is moved onto it, so the engine
// keeps the window it knows.
//
// Under the scene life cycle iOS no longer calls the app delegate's activation and background
// methods, and the engine's focus, pause and audio handling hangs off them. The scene callbacks
// below forward to those methods on the app delegate, which hands them to every engine service.
#import <UIKit/UIKit.h>

// Calls an optional UIApplicationDelegate method that takes the application, if the delegate has it.
static void STS2ForwardToAppDelegate(SEL selector) {
	UIApplication *application = UIApplication.sharedApplication;
	id<UIApplicationDelegate> delegate = application.delegate;
	if ([delegate respondsToSelector:selector]) {
		((void (*)(id, SEL, UIApplication *))[(id)delegate methodForSelector:selector])(delegate, selector, application);
	}
}

@interface STS2SceneDelegate : UIResponder <UIWindowSceneDelegate>
@property(strong, nonatomic) UIWindow *window;
@end

@implementation STS2SceneDelegate

- (void)scene:(UIScene *)scene willConnectToSession:(UISceneSession *)session options:(UISceneConnectionOptions *)connectionOptions {
	if (![scene isKindOfClass:UIWindowScene.class]) {
		return;
	}
	UIWindowScene *windowScene = (UIWindowScene *)scene;
	id<UIApplicationDelegate> delegate = UIApplication.sharedApplication.delegate;
	UIWindow *window = [delegate respondsToSelector:@selector(window)] ? delegate.window : nil;
	if (!window) {
		// The engine makes its window before any scene connects; without it there is nothing to show.
		return;
	}
	window.windowScene = windowScene;
	window.frame = windowScene.coordinateSpace.bounds;
	[window makeKeyAndVisible];
	self.window = window;
}

- (void)sceneDidBecomeActive:(UIScene *)scene {
	STS2ForwardToAppDelegate(@selector(applicationDidBecomeActive:));
}

- (void)sceneWillResignActive:(UIScene *)scene {
	STS2ForwardToAppDelegate(@selector(applicationWillResignActive:));
}

- (void)sceneDidEnterBackground:(UIScene *)scene {
	STS2ForwardToAppDelegate(@selector(applicationDidEnterBackground:));
}

- (void)sceneWillEnterForeground:(UIScene *)scene {
	STS2ForwardToAppDelegate(@selector(applicationWillEnterForeground:));
}

@end
