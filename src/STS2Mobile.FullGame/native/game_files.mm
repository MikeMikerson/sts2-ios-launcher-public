// What iOS says about the game's files: the space a download may count on, and keeping a 2 GB game out of device backups.
//
// The managed host (STS2Mobile.Platform/DeviceStorage.cs) looks the sts2_files_* functions up in the app by name.
#import <Foundation/Foundation.h>

// iOS's answer for space the player asked to use: it counts what the system would clear for it, which a plain free space
// reading does not. -1 when iOS gives no answer.
extern "C" __attribute__((visibility("default"))) long long sts2_files_free_bytes(void) {
	NSURL *documents = [NSFileManager.defaultManager URLsForDirectory:NSDocumentDirectory inDomains:NSUserDomainMask].firstObject;
	NSNumber *available = nil;
	if (![documents getResourceValue:&available forKey:NSURLVolumeAvailableCapacityForImportantUsageKey error:nil] || available == nil) {
		return -1;
	}
	return available.longLongValue;
}

extern "C" __attribute__((visibility("default"))) int sts2_files_exclude_from_backup(const char *path) {
	NSURL *url = [NSURL fileURLWithFileSystemRepresentation:path isDirectory:YES relativeToURL:nil];
	return [url setResourceValue:@YES forKey:NSURLIsExcludedFromBackupKey error:nil] ? 1 : 0;
}

// Holding the functions here keeps the linker from removing them: nothing in the app calls them
// directly, the managed host finds them by name.
__attribute__((used)) static void *volatile gFileExports[] = {
	(void *)&sts2_files_free_bytes, (void *)&sts2_files_exclude_from_backup
};
