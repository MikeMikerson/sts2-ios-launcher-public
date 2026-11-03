// The Files picker behind the launcher's "Import from Files" button and the game's Mods screen
// "Add mod from Files" button.
//
// The managed host (STS2Mobile.Platform/FilesPicker.cs, used by STS2Mobile.FullGame/Launcher.Mods.cs
// and STS2Mobile.GameHost/ModImportFlow.cs) looks the sts2_mod_import_* functions up in the app by
// name. sts2_mod_import_pick opens iOS's document picker for one .zip; the picker copies the chosen
// file into the app's own temporary folder, so the app never holds a reference into another app's
// storage. The host asks sts2_mod_import_poll each frame until the player has chosen a file or
// closed the picker. Everything here runs on the main thread: the host calls from the engine's main
// loop, and UIKit calls the delegate there.
//
// Nothing is checked here. The host checks the archive (STS2Mobile.Platform/ModPackageImport.cs,
// with the pack check in STS2Mobile.Platform/ModImport.cs) before anything is stored.
#import <UIKit/UIKit.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>

// sts2_mod_import_poll results; FilesPicker.PollResult (STS2Mobile.Platform/FilesPicker.cs) holds the same values.
enum {
	kImportFailed = -1,
	kImportIdle = 0,
	kImportPicked = 1,
	kImportCancelled = 2,
};

static int gImportState = kImportIdle;
static NSString *gImportPath = nil;
static BOOL gPickerOpen = NO;

@interface STS2ModImportPicker : NSObject <UIDocumentPickerDelegate>
@end

@implementation STS2ModImportPicker

- (void)documentPicker:(UIDocumentPickerViewController *)controller didPickDocumentsAtURLs:(NSArray<NSURL *> *)urls {
	gPickerOpen = NO;
	NSURL *url = urls.firstObject;
	if (url.isFileURL && url.path.length) {
		gImportPath = url.path;
		gImportState = kImportPicked;
	} else {
		gImportState = kImportCancelled;
	}
}

- (void)documentPickerWasCancelled:(UIDocumentPickerViewController *)controller {
	gPickerOpen = NO;
	gImportState = kImportCancelled;
}

@end

static STS2ModImportPicker *gPickerDelegate = nil;

static UIViewController *STS2TopViewController(void) {
	for (UIScene *scene in UIApplication.sharedApplication.connectedScenes) {
		if (![scene isKindOfClass:UIWindowScene.class] || scene.activationState != UISceneActivationStateForegroundActive) {
			continue;
		}
		for (UIWindow *window in ((UIWindowScene *)scene).windows) {
			if (!window.isKeyWindow) {
				continue;
			}
			UIViewController *top = window.rootViewController;
			while (top.presentedViewController) {
				top = top.presentedViewController;
			}
			return top;
		}
	}
	return nil;
}

// 1 when the picker was opened, 0 when one is already open, -1 when there is nothing to show it on.
extern "C" __attribute__((visibility("default"))) int sts2_mod_import_pick(void) {
	if (gPickerOpen) {
		return 0;
	}
	UIViewController *top = STS2TopViewController();
	if (!top) {
		return -1;
	}
	if (!gPickerDelegate) {
		gPickerDelegate = [STS2ModImportPicker new];
	}
	UIDocumentPickerViewController *picker =
			[[UIDocumentPickerViewController alloc] initForOpeningContentTypes:@[ UTTypeZIP ] asCopy:YES];
	picker.allowsMultipleSelection = NO;
	picker.delegate = gPickerDelegate;
	gImportState = kImportIdle;
	gImportPath = nil;
	gPickerOpen = YES;
	[top presentViewController:picker animated:YES completion:nil];
	return 1;
}

// kImportIdle while nothing has happened, kImportCancelled when the picker was closed without a file,
// kImportPicked when a file was chosen: its path is written to buffer as UTF-8 with a terminating
// zero. kImportFailed when the path does not fit; the choice is then dropped. A returned result is
// cleared, so each choice is handed over once.
extern "C" __attribute__((visibility("default"))) int sts2_mod_import_poll(char *buffer, int capacity) {
	int state = gImportState;
	if (state == kImportIdle) {
		return kImportIdle;
	}
	gImportState = kImportIdle;
	if (state == kImportCancelled) {
		return kImportCancelled;
	}
	NSString *path = gImportPath;
	gImportPath = nil;
	const char *utf8 = path.fileSystemRepresentation;
	size_t length = utf8 ? strlen(utf8) : 0;
	if (!utf8 || capacity <= 0 || length + 1 > (size_t)capacity) {
		return kImportFailed;
	}
	memcpy(buffer, utf8, length + 1);
	return kImportPicked;
}

// Holding the functions here keeps the linker from removing them: nothing in the app calls them
// directly, the managed host finds them by name.
__attribute__((used)) static void *volatile gModImportExports[] = {
	(void *)&sts2_mod_import_pick, (void *)&sts2_mod_import_poll
};
