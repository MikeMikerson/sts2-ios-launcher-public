// Watches for the two ways the picture can freeze while the music plays on, and gives the player a way out of the first.
//
// 1. The GPU stops taking the game's work. The phone files gpuEvent reports for the game ("BIF0 page fault", "MMU interrupt"),
//    after which iOS can refuse the process's command buffers: the main loop carries on and nothing new reaches the screen
//   . Every command buffer the engine makes gets a completion handler here, which
//    counts the ones that end in an error. When errors keep coming with none completing cleanly for a few seconds while the app is
//    in front, the UIKit screen of native/setup_overlay.mm (drawn by the system, not the engine's queue) says so and offers to
//    close the app.
// 2. The main thread stops. FMOD mixes on its own threads, so the music goes on. A background timer sends the main queue a beat a
//    second and notes when the beats stop and start again.
//
// Both go to Documents/watch.log, written from here and flushed at once, so the lines survive a stalled main thread and a relaunch.
// The previous launch's game log is kept as Documents/full-game-previous.log, since the engine overwrites the log as it
// starts and a frozen run is usually followed by a relaunch. The name must not begin with "full-game-startup": the engine's log
// rotation deletes files with that start as old backups.
//
// STS2_GPU_FAULT_TEST=1 treats every command buffer as failed from 60 s after launch (the main menu is up by then), to see the screen with nobody there.
#import <Metal/Metal.h>
#import <UIKit/UIKit.h>
#import <objc/runtime.h>
#include <atomic>
#include <fcntl.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

extern "C" void sts2_overlay_show(const char *steps, const char *title, const char *status, float fraction, const char *button);

static const double STS2StoppedAfterSeconds = 3.0;
static const int STS2StallSeconds = 5;
static const int STS2LoggedErrors = 20;
static const off_t STS2WatchLogLimit = 256 * 1024;

static int gWatchLog = -1;
static std::atomic<int> gErrors{0}, gGood{0};
// When errors started with no clean command buffer since, on the monotonic clock in seconds; 0 for none.
static std::atomic<double> gErrorSinceGood{0};
static std::atomic<bool> gActive{false}, gStopped{false}, gFaultTest{false};
static std::atomic<long> gBeat{0};
static double gLaunched = 0;
static const void *const kWatchedKey = &kWatchedKey;
// Kept for the life of the app.
static dispatch_source_t gBeatTimer;

static double STS2Now(void) {
	return (double)clock_gettime_nsec_np(CLOCK_MONOTONIC) / 1e9;
}

static NSString *STS2Documents(void) {
	return NSSearchPathForDirectoriesInDomains(NSDocumentDirectory, NSUserDomainMask, YES).firstObject;
}

// One line with the wall-clock time, written in one call so lines from different threads do not mix.
static void STS2WatchLine(NSString *line) {
	if (gWatchLog < 0) {
		return;
	}
	NSDateFormatter *format = [[NSDateFormatter alloc] init];
	format.dateFormat = @"yyyy-MM-dd HH:mm:ss.SSS";
	NSString *text = [NSString stringWithFormat:@"%@ %@\n", [format stringFromDate:NSDate.date], line];
	NSData *data = [text dataUsingEncoding:NSUTF8StringEncoding];
	write(gWatchLog, data.bytes, data.length);
	fsync(gWatchLog);
	NSLog(@"Watch: %@", line);
}

static void STS2OpenLogs(void) {
	NSString *documents = STS2Documents();
	if (!documents) {
		return;
	}
	NSFileManager *files = NSFileManager.defaultManager;
	NSString *log = [documents stringByAppendingPathComponent:@"full-game-startup.log"];
	NSString *previous = [documents stringByAppendingPathComponent:@"full-game-previous.log"];
	NSString *kept = @"no game log from the launch before";
	if ([files fileExistsAtPath:log]) {
		NSError *error = nil;
		[files removeItemAtPath:previous error:nil];
		kept = [files moveItemAtPath:log toPath:previous error:&error] ? @"kept the launch before's game log"
																	   : [NSString stringWithFormat:@"could not keep the game log: %@", error];
	}
	NSString *watch = [documents stringByAppendingPathComponent:@"watch.log"];
	struct stat info;
	if (stat(watch.fileSystemRepresentation, &info) == 0 && info.st_size > STS2WatchLogLimit) {
		NSString *older = [documents stringByAppendingPathComponent:@"watch.previous.log"];
		[files removeItemAtPath:older error:nil];
		[files moveItemAtPath:watch toPath:older error:nil];
	}
	gWatchLog = open(watch.fileSystemRepresentation, O_WRONLY | O_CREAT | O_APPEND | O_CLOEXEC, 0644);
	STS2WatchLine([NSString stringWithFormat:@"Launch: %@", kept]);
}

static void STS2ShowStopped(void) {
	dispatch_async(dispatch_get_main_queue(), ^{
		sts2_overlay_show("", "The picture stopped",
						  "The iPhone's graphics stopped taking the game's drawing, so the screen cannot change any more. The run "
						  "carries on from its last save when the app is opened again.",
						  -1, "Close the app");
	});
}

static void STS2Completed(id<MTLCommandBuffer> buffer) {
	double now = STS2Now();
	bool failed = buffer.status == MTLCommandBufferStatusError;
	if (gFaultTest && now - gLaunched > 60) {
		failed = true;
	}
	if (!failed) {
		gGood++;
		gErrorSinceGood = 0;
		return;
	}
	int count = ++gErrors;
	NSError *error = buffer.error;
	// Work refused while the app is not in front is expected, and says nothing about the GPU.
	bool background = error.code == MTLCommandBufferErrorNotPermitted || !gActive;
	if (count <= STS2LoggedErrors || count % 100 == 0) {
		STS2WatchLine([NSString stringWithFormat:@"GPU error %d: code %ld (%@)%@, label %@, %d buffers fine before", count,
												 (long)error.code, error.localizedDescription ?: @"no error object",
												 background ? @", app not in front" : @"", buffer.label ?: @"none",
												 gGood.load()]);
	}
	if (background) {
		return;
	}
	double expected = 0;
	gErrorSinceGood.compare_exchange_strong(expected, now);
	double since = gErrorSinceGood;
	if (since > 0 && now - since >= STS2StoppedAfterSeconds && !gStopped.exchange(true)) {
		STS2WatchLine([NSString stringWithFormat:@"Picture stopped: %d GPU errors and no clean command buffer for %.1f s", count,
												 now - since]);
		STS2ShowStopped();
	}
}

// Gives a new command buffer a handler that reports how it ended. One queue method may call another; one handler is enough.
static id<MTLCommandBuffer> STS2Watched(id<MTLCommandBuffer> buffer) {
	if (buffer && !objc_getAssociatedObject(buffer, kWatchedKey)) {
		objc_setAssociatedObject(buffer, kWatchedKey, @YES, OBJC_ASSOCIATION_RETAIN_NONATOMIC);
		[buffer addCompletedHandler:^(id<MTLCommandBuffer> done) {
			STS2Completed(done);
		}];
	}
	return buffer;
}

// Wraps a command queue method with no arguments that returns a new command buffer.
static void STS2WatchQueueMethod(Class queueClass, SEL selector) {
	Method method = class_getInstanceMethod(queueClass, selector);
	if (!method) {
		return;
	}
	IMP original = method_getImplementation(method);
	id (^wrapper)(id) = ^id(id queue) {
		return STS2Watched(((id (*)(id, SEL))original)(queue, selector));
	};
	class_replaceMethod(queueClass, selector, imp_implementationWithBlock(wrapper), method_getTypeEncoding(method));
}

// The same for commandBufferWithDescriptor:.
static void STS2WatchQueueDescriptorMethod(Class queueClass) {
	SEL selector = @selector(commandBufferWithDescriptor:);
	Method method = class_getInstanceMethod(queueClass, selector);
	if (!method) {
		return;
	}
	IMP original = method_getImplementation(method);
	id (^wrapper)(id, id) = ^id(id queue, id descriptor) {
		return STS2Watched(((id (*)(id, SEL, id))original)(queue, selector, descriptor));
	};
	class_replaceMethod(queueClass, selector, imp_implementationWithBlock(wrapper), method_getTypeEncoding(method));
}

static void STS2WatchGPU(void) {
	id<MTLDevice> device = MTLCreateSystemDefaultDevice();
	id<MTLCommandQueue> queue = [device newCommandQueue];
	if (!queue) {
		STS2WatchLine(@"GPU watch: no Metal command queue");
		return;
	}
	// The engine's queue comes from the same device, so it is the same class.
	Class queueClass = object_getClass(queue);
	STS2WatchQueueMethod(queueClass, @selector(commandBuffer));
	STS2WatchQueueMethod(queueClass, @selector(commandBufferWithUnretainedReferences));
	STS2WatchQueueDescriptorMethod(queueClass);
	STS2WatchLine([NSString stringWithFormat:@"GPU watch on: %s on %@%@", class_getName(queueClass), device.name,
											 gFaultTest ? @", fault test" : @""]);
}

static void STS2WatchMainThread(void) {
	dispatch_source_t timer = dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER, 0, 0, dispatch_get_global_queue(QOS_CLASS_UTILITY, 0));
	__block long lastBeat = 0;
	__block int still = 0;
	dispatch_source_set_timer(timer, dispatch_time(DISPATCH_TIME_NOW, NSEC_PER_SEC), NSEC_PER_SEC, NSEC_PER_SEC / 10);
	dispatch_source_set_event_handler(timer, ^{
		long beat = gBeat;
		if (beat == lastBeat && gActive) {
			if (++still == STS2StallSeconds) {
				STS2WatchLine([NSString stringWithFormat:@"Main thread stalled: no beat for %d s", still]);
			}
		} else {
			if (still >= STS2StallSeconds) {
				STS2WatchLine([NSString stringWithFormat:@"Main thread back after %d s", still]);
			}
			still = 0;
		}
		lastBeat = beat;
		dispatch_async(dispatch_get_main_queue(), ^{
			gBeat++;
		});
	});
	dispatch_resume(timer);
	gBeatTimer = timer;
}

// Command buffers that ended in an error since launch, for the game's [stats] lines.
extern "C" __attribute__((visibility("default"))) int sts2_gpu_errors(void) {
	return gErrors;
}

// 1 once the picture has been found stopped and the screen shown.
extern "C" __attribute__((visibility("default"))) int sts2_gpu_picture_stopped(void) {
	return gStopped ? 1 : 0;
}

// Holding the functions here keeps the linker from removing them: the managed side finds them by name.
__attribute__((used)) static void *volatile gWatchExports[] = {(void *)&sts2_gpu_errors, (void *)&sts2_gpu_picture_stopped};

@interface STS2Watch : NSObject
@end

@implementation STS2Watch

// Runs when the app image is loaded, before main, so before the engine opens its log.
+ (void)load {
	gLaunched = STS2Now();
	const char *test = getenv("STS2_GPU_FAULT_TEST");
	gFaultTest = test && strcmp(test, "1") == 0;
	STS2OpenLogs();
	NSNotificationCenter *center = NSNotificationCenter.defaultCenter;
	[center addObserverForName:UIApplicationDidFinishLaunchingNotification
						object:nil
						 queue:NSOperationQueue.mainQueue
					usingBlock:^(NSNotification *notification) {
						// Replacing the queue's methods reaches queues made before, so the engine's own is watched too.
						STS2WatchGPU();
						STS2WatchMainThread();
					}];
	[center addObserverForName:UIApplicationDidBecomeActiveNotification
						object:nil
						 queue:NSOperationQueue.mainQueue
					usingBlock:^(NSNotification *notification) {
						gErrorSinceGood = 0;
						gActive = true;
					}];
	[center addObserverForName:UIApplicationWillResignActiveNotification
						object:nil
						 queue:NSOperationQueue.mainQueue
					usingBlock:^(NSNotification *notification) {
						gActive = false;
					}];
}

@end
