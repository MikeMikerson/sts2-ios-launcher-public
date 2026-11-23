#import <UIKit/UIKit.h>

extern "C" __attribute__((used, visibility("default"))) int sts2_request_steam_profile(uint64_t steamId) {
	if (!NSThread.isMainThread) {
		return 0;
	}
	for (UIScene *scene in UIApplication.sharedApplication.connectedScenes) {
		if (![scene isKindOfClass:UIWindowScene.class] || scene.activationState != UISceneActivationStateForegroundActive) {
			continue;
		}
		for (UIWindow *window in ((UIWindowScene *)scene).windows) {
			UIViewController *presenter = window.rootViewController;
			if (!window.isKeyWindow || !presenter || presenter.presentedViewController) {
				continue;
			}
			NSString *address = [NSString stringWithFormat:@"https://steamcommunity.com/profiles/%llu", (unsigned long long)steamId];
			UIAlertController *prompt = [UIAlertController alertControllerWithTitle:@"Open Steam profile?"
					message:address preferredStyle:UIAlertControllerStyleAlert];
			[prompt addAction:[UIAlertAction actionWithTitle:@"Cancel" style:UIAlertActionStyleCancel handler:nil]];
			[prompt addAction:[UIAlertAction actionWithTitle:@"Open" style:UIAlertActionStyleDefault handler:^(UIAlertAction *) {
				[UIApplication.sharedApplication openURL:[NSURL URLWithString:address] options:@{} completionHandler:nil];
			}]];
			[presenter presentViewController:prompt animated:YES completion:nil];
			return 1;
		}
	}
	return 0;
}
