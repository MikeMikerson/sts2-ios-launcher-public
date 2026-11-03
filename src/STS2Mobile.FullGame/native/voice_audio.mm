// QuickMatch voice chat on the iPhone: the microphone, and the teammates' voices coming out.
//
// The managed side (STS2Mobile.Platform/VoiceAudio.cs, used by STS2Mobile.GameHost/PhoneVoiceDevice.cs)
// looks the sts2_voice_* functions up in the app by name. Nothing here runs until the player turns their mic on in a quick match or a
// teammate speaks; the game's own sound (FMOD) is never touched.
//
// - Playing: one AVAudioEngine whose source node plays 16 kHz mono voice from a ring buffer that the
//   game thread fills. The engine's mixer resamples it for the output.
// - Recording, only while the mic is on: the audio session moves to play-and-record (the speaker
//   stays the output, Bluetooth headphones keep high quality A2DP output and the phone's own mic is
//   used) and a tap on the engine's input converts the microphone to 16 kHz mono into a second ring
//   buffer. On the phone's speaker the input uses Apple's voice processing, which removes from the
//   microphone what this engine plays (the teammates' voices), so they do not hear themselves. With
//   headphones there is no echo and voice processing stays off. When the mic turns off the engine is
//   rebuilt without its input, so iOS's microphone indicator goes out, and the session goes back to
//   the category the app had.
// - Permission: asked when the player first turns the mic on, never at launch.
// - A route change, an interruption (a call) or the return from the background stops the engine;
//   it is rebuilt as it was within a quarter of a second, and a microphone that fails to start is
//   tried again at most once a second while the voice keeps playing.
//
// Building the engine and changing the audio session take several hundred milliseconds (voice
// processing most of all), so they run on a serial
// queue of their own and never on the game's thread. The game thread only says what it wants (mic,
// output) and reads what is running; sts2_voice_capture_status follows a start. All sts2_voice_*
// calls come from the game's thread, and only the queue touches the engine and the audio session.
// The ring buffers each have one writer and one reader (the game thread and an audio thread).
#import <AVFoundation/AVFoundation.h>

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <os/lock.h>

namespace {

constexpr double kVoiceRate = 16000.0;
constexpr CFAbsoluteTime kRetrySeconds = 1.0;
constexpr CFAbsoluteTime kCheckSeconds = 0.25;

class Ring {
public:
	explicit Ring(size_t capacity) : _data(new int16_t[capacity]), _capacity(capacity) {}

	size_t Count() const {
		return _write.load(std::memory_order_acquire) - _read.load(std::memory_order_acquire);
	}

	// Writer only. Returns how many fitted; the rest is dropped.
	size_t Write(const int16_t *samples, size_t count) {
		size_t write = _write.load(std::memory_order_relaxed);
		size_t room = _capacity - (write - _read.load(std::memory_order_acquire));
		if (count > room) {
			count = room;
		}
		for (size_t i = 0; i < count; i++) {
			_data[(write + i) % _capacity] = samples[i];
		}
		_write.store(write + count, std::memory_order_release);
		return count;
	}

	// Reader only.
	size_t Read(int16_t *samples, size_t count) {
		size_t read = _read.load(std::memory_order_relaxed);
		size_t available = _write.load(std::memory_order_acquire) - read;
		if (count > available) {
			count = available;
		}
		for (size_t i = 0; i < count; i++) {
			samples[i] = _data[(read + i) % _capacity];
		}
		_read.store(read + count, std::memory_order_release);
		return count;
	}

	// Reader only: drops everything written so far.
	void Drop() {
		_read.store(_write.load(std::memory_order_acquire), std::memory_order_release);
	}

private:
	int16_t *_data;
	size_t _capacity;
	std::atomic<size_t> _read{0};
	std::atomic<size_t> _write{0};
};

Ring gPlayback(16000);   // one second
Ring gCapture(32000);    // two seconds

// Set by the game thread: what the player wants now.
std::atomic<bool> gCaptureWanted{false};
std::atomic<bool> gOutputWanted{false};
// Set by the queue after each pass: what is running.
std::atomic<bool> gCaptureRunning{false};
std::atomic<bool> gCaptureFailed{false};
std::atomic<int> gRoute{0};
std::atomic<bool> gEngineRunning{false};
// Between the two.
std::atomic<bool> gPassQueued{false};
std::atomic<bool> gRestart{false};
std::atomic<bool> gAsking{false};
CFAbsoluteTime gCheckedAt = 0;   // game thread only

// The queue's own.
AVAudioEngine *gEngine = nil;
id gConfigurationObserver = nil;
id gInterruptionObserver = nil;
bool gTapInstalled = false;
bool gVoiceProcessing = false;
bool gSpeaker = false;
bool gSessionSaved = false;
NSString *gSavedCategory = nil;
NSString *gSavedMode = nil;
AVAudioSessionCategoryOptions gSavedOptions = 0;
CFAbsoluteTime gCaptureFailedAt = 0;
CFAbsoluteTime gOutputFailedAt = 0;

os_unfair_lock gErrorLock = OS_UNFAIR_LOCK_INIT;
char gLastError[256] = "";
char gErrorForGame[256] = "";   // game thread only
char gStateForGame[512] = "";   // game thread only

void SetError(const char *what, NSError *error) {
	os_unfair_lock_lock(&gErrorLock);
	snprintf(gLastError, sizeof gLastError, "%s: %s", what, error ? error.localizedDescription.UTF8String : "failed");
	os_unfair_lock_unlock(&gErrorLock);
}

void ClearError() {
	os_unfair_lock_lock(&gErrorLock);
	gLastError[0] = 0;
	os_unfair_lock_unlock(&gErrorLock);
}

// 0 not asked, 1 denied, 2 granted.
int RecordPermission() {
	if (@available(iOS 17.0, *)) {
		switch ([AVAudioApplication sharedInstance].recordPermission) {
			case AVAudioApplicationRecordPermissionGranted: return 2;
			case AVAudioApplicationRecordPermissionDenied: return 1;
			default: return 0;
		}
	}
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
	switch ([AVAudioSession sharedInstance].recordPermission) {
		case AVAudioSessionRecordPermissionGranted: return 2;
		case AVAudioSessionRecordPermissionDenied: return 1;
		default: return 0;
	}
#pragma clang diagnostic pop
}

bool OutputIsPhoneSpeaker(AVAudioSession *session) {
	for (AVAudioSessionPortDescription *port in session.currentRoute.outputs) {
		if ([port.portType isEqualToString:AVAudioSessionPortBuiltInSpeaker] ||
				[port.portType isEqualToString:AVAudioSessionPortBuiltInReceiver]) {
			return true;
		}
	}
	return false;
}

void TearDown() {
	if (gEngine == nil) {
		return;
	}
	if (gTapInstalled) {
		[gEngine.inputNode removeTapOnBus:0];
		gTapInstalled = false;
	}
	[gEngine stop];
	if (gConfigurationObserver != nil) {
		[[NSNotificationCenter defaultCenter] removeObserver:gConfigurationObserver];
		gConfigurationObserver = nil;
	}
	gEngine = nil;
	gVoiceProcessing = false;
	// The engine's audio thread, the playback ring's reader, has stopped.
	gPlayback.Drop();
}

// The microphone in 16 kHz mono, from the input tap's thread into the capture ring.
void Capture(AVAudioPCMBuffer *buffer, AVAudioConverter *converter) {
	AVAudioFrameCount capacity = (AVAudioFrameCount)(buffer.frameLength * kVoiceRate / buffer.format.sampleRate) + 64;
	AVAudioPCMBuffer *converted = [[AVAudioPCMBuffer alloc] initWithPCMFormat:converter.outputFormat frameCapacity:capacity];
	if (converted == nil) {
		return;
	}
	__block bool given = false;
	NSError *error = nil;
	AVAudioConverterOutputStatus status = [converter convertToBuffer:converted error:&error
			withInputFromBlock:^AVAudioBuffer *(AVAudioPacketCount count, AVAudioConverterInputStatus *inputStatus) {
		if (given) {
			*inputStatus = AVAudioConverterInputStatus_NoDataNow;
			return nil;
		}
		given = true;
		*inputStatus = AVAudioConverterInputStatus_HaveData;
		return buffer;
	}];
	if (status == AVAudioConverterOutputStatus_Error || converted.frameLength == 0) {
		return;
	}
	gCapture.Write(converted.int16ChannelData[0], converted.frameLength);
}

bool InstallMicrophone(AVAudioEngine *engine) {
	AVAudioInputNode *input = engine.inputNode;
	NSError *error = nil;
	gSpeaker = OutputIsPhoneSpeaker([AVAudioSession sharedInstance]);
	if (gSpeaker) {
		if ([input setVoiceProcessingEnabled:YES error:&error]) {
			gVoiceProcessing = true;
			if (@available(iOS 17.0, *)) {
				// Keep the game's own sound at its level while the mic is open.
				AVAudioVoiceProcessingOtherAudioDuckingConfiguration ducking = {NO, AVAudioVoiceProcessingOtherAudioDuckingLevelMin};
				input.voiceProcessingOtherAudioDuckingConfiguration = ducking;
			}
		} else {
			SetError("voice processing", error);
		}
	}
	AVAudioFormat *hardware = [input outputFormatForBus:0];
	if (hardware.sampleRate <= 0 || hardware.channelCount == 0) {
		SetError("microphone", nil);
		return false;
	}
	AVAudioFormat *wanted = [[AVAudioFormat alloc] initWithCommonFormat:AVAudioPCMFormatInt16 sampleRate:kVoiceRate
			channels:1 interleaved:YES];
	AVAudioConverter *converter = [[AVAudioConverter alloc] initFromFormat:hardware toFormat:wanted];
	if (converter == nil) {
		SetError("microphone format", nil);
		return false;
	}
	converter.downmix = YES;
	[input installTapOnBus:0 bufferSize:1024 format:hardware block:^(AVAudioPCMBuffer *buffer, AVAudioTime *when) {
		Capture(buffer, converter);
	}];
	gTapInstalled = true;
	return true;
}

void Request();

// Builds and starts the engine: always the voice output, and the microphone when withInput.
bool Build(bool withInput) {
	TearDown();
	gEngine = [[AVAudioEngine alloc] init];
	AVAudioFormat *voice = [[AVAudioFormat alloc] initWithCommonFormat:AVAudioPCMFormatFloat32 sampleRate:kVoiceRate
			channels:1 interleaved:NO];
	AVAudioSourceNode *source = [[AVAudioSourceNode alloc] initWithFormat:voice
			renderBlock:^OSStatus(BOOL *isSilence, const AudioTimeStamp *timestamp, AVAudioFrameCount frames, AudioBufferList *output) {
		float *out = (float *)output->mBuffers[0].mData;
		int16_t chunk[256];
		AVAudioFrameCount done = 0;
		while (done < frames) {
			size_t want = frames - done < 256 ? frames - done : 256;
			size_t got = gPlayback.Read(chunk, want);
			for (size_t i = 0; i < got; i++) {
				out[done + i] = chunk[i] / 32768.0f;
			}
			done += (AVAudioFrameCount)got;
			if (got < want) {
				memset(out + done, 0, (frames - done) * sizeof(float));
				break;
			}
		}
		for (UInt32 b = 1; b < output->mNumberBuffers; b++) {
			memcpy(output->mBuffers[b].mData, out, frames * sizeof(float));
		}
		*isSilence = done == 0;
		return noErr;
	}];
	[gEngine attachNode:source];
	[gEngine connect:source to:gEngine.mainMixerNode format:voice];
	if (withInput && !InstallMicrophone(gEngine)) {
		TearDown();
		return false;
	}
	gConfigurationObserver = [[NSNotificationCenter defaultCenter] addObserverForName:AVAudioEngineConfigurationChangeNotification
			object:gEngine queue:nil usingBlock:^(NSNotification *note) {
		gRestart.store(true);
		Request();
	}];
	if (gInterruptionObserver == nil) {
		gInterruptionObserver = [[NSNotificationCenter defaultCenter] addObserverForName:AVAudioSessionInterruptionNotification
				object:nil queue:nil usingBlock:^(NSNotification *note) {
			gRestart.store(true);
			Request();
		}];
	}
	[gEngine prepare];
	NSError *error = nil;
	if (![gEngine startAndReturnError:&error]) {
		SetError("audio engine", error);
		TearDown();
		return false;
	}
	ClearError();
	return true;
}

bool UsePlayAndRecord() {
	AVAudioSession *session = [AVAudioSession sharedInstance];
	if (!gSessionSaved) {
		gSavedCategory = session.category;
		gSavedMode = session.mode;
		gSavedOptions = session.categoryOptions;
		gSessionSaved = true;
	}
	AVAudioSessionCategoryOptions options = AVAudioSessionCategoryOptionDefaultToSpeaker |
		AVAudioSessionCategoryOptionAllowBluetoothA2DP | (gSavedOptions & AVAudioSessionCategoryOptionMixWithOthers);
	NSError *error = nil;
	if (![session setCategory:AVAudioSessionCategoryPlayAndRecord mode:AVAudioSessionModeDefault options:options error:&error] ||
			![session setActive:YES error:&error]) {
		SetError("audio session", error);
		return false;
	}
	return true;
}

void RestoreSession() {
	if (!gSessionSaved) {
		return;
	}
	[[AVAudioSession sharedInstance] setCategory:gSavedCategory mode:gSavedMode options:gSavedOptions error:nil];
	gSessionSaved = false;
}

// One pass on the queue: brings the engine in line with what the game thread wants, then says what runs.
void Reconcile() {
	CFAbsoluteTime now = CFAbsoluteTimeGetCurrent();
	bool restart = gRestart.exchange(false) || (gEngine != nil && !gEngine.isRunning);
	bool output = gOutputWanted.load();
	// A microphone that failed waits a second before the next try; voice keeps playing meanwhile.
	bool capture = gCaptureWanted.load() && !(gCaptureFailed.load() && now - gCaptureFailedAt < kRetrySeconds);
	if (!capture && !output) {
		TearDown();
		RestoreSession();
	} else if (restart || gEngine == nil || gTapInstalled != capture) {
		if (capture && UsePlayAndRecord() && Build(true)) {
			gCaptureFailed.store(false);
		} else {
			if (capture) {
				gCaptureFailedAt = now;
				gCaptureFailed.store(true);
			}
			TearDown();
			RestoreSession();
			if (output && now - gOutputFailedAt >= kRetrySeconds && !Build(false)) {
				gOutputFailedAt = now;
			}
		}
	}
	gEngineRunning.store(gEngine != nil && gEngine.isRunning);
	gCaptureRunning.store(gEngine != nil && gEngine.isRunning && gTapInstalled);
	gRoute.store(gVoiceProcessing ? 2 : (gSpeaker ? 1 : 0));
}

dispatch_queue_t Queue() {
	static dispatch_queue_t queue = dispatch_queue_create("sts2.voice",
		dispatch_queue_attr_make_with_qos_class(DISPATCH_QUEUE_SERIAL, QOS_CLASS_USER_INITIATED, 0));
	return queue;
}

// Asks the queue for a pass; one waiting pass covers every request made before it runs. Any thread.
void Request() {
	if (gPassQueued.exchange(true)) {
		return;
	}
	dispatch_async(Queue(), ^{
		gPassQueued.store(false);
		Reconcile();
	});
}

// From the game thread's calls each frame: a stopped engine (the return from the background) or a
// failed microphone gets its next pass within a quarter of a second.
void Check() {
	CFAbsoluteTime now = CFAbsoluteTimeGetCurrent();
	if (now - gCheckedAt < kCheckSeconds || !(gCaptureWanted.load() || gOutputWanted.load())) {
		return;
	}
	gCheckedAt = now;
	Request();
}

}  // namespace

// 0 cannot record, 1 not asked yet, 2 asking, 3 refused, 4 ready (VoiceCaptureState in QuickMatch).
extern "C" __attribute__((visibility("default"))) int sts2_voice_capture_state(void) {
	if (gAsking.load()) {
		return 2;
	}
	switch (RecordPermission()) {
		case 2: return 4;
		case 1: return 3;
		default: return 1;
	}
}

extern "C" __attribute__((visibility("default"))) void sts2_voice_request_permission(void) {
	if (RecordPermission() != 0 || gAsking.exchange(true)) {
		return;
	}
	void (^answered)(BOOL) = ^(BOOL granted) {
		gAsking.store(false);
	};
	if (@available(iOS 17.0, *)) {
		[AVAudioApplication requestRecordPermissionWithCompletionHandler:answered];
		return;
	}
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
	[[AVAudioSession sharedInstance] requestRecordPermission:answered];
#pragma clang diagnostic pop
}

// 1 when the start is under way on the queue (sts2_voice_capture_status follows it), 0 without permission.
extern "C" __attribute__((visibility("default"))) int sts2_voice_start_capture(void) {
	if (RecordPermission() != 2) {
		SetError("microphone permission", nil);
		return 0;
	}
	if (!gCaptureWanted.exchange(true)) {
		gCaptureFailed.store(false);
		// What an earlier recording left unread; the game thread is the capture ring's reader.
		gCapture.Drop();
		Request();
	}
	return 1;
}

extern "C" __attribute__((visibility("default"))) void sts2_voice_stop_capture(void) {
	if (gCaptureWanted.exchange(false)) {
		Request();
	}
}

// 0 mic off, 1 starting, 2 recording, 3 could not start (tried again each second; sts2_voice_last_error says why).
extern "C" __attribute__((visibility("default"))) int sts2_voice_capture_status(void) {
	if (!gCaptureWanted.load()) {
		return 0;
	}
	if (gCaptureRunning.load()) {
		return 2;
	}
	return gCaptureFailed.load() ? 3 : 1;
}

extern "C" __attribute__((visibility("default"))) int sts2_voice_read(int16_t *samples, int count) {
	Check();
	if (!gCaptureWanted.load() || !gCaptureRunning.load() || count <= 0) {
		return 0;
	}
	return (int)gCapture.Read(samples, (size_t)count);
}

extern "C" __attribute__((visibility("default"))) int sts2_voice_queued(void) {
	Check();
	return gOutputWanted.load() ? (int)gPlayback.Count() : 0;
}

// Voice handed over while the engine starts waits in the ring (one second at most) and plays once it runs.
extern "C" __attribute__((visibility("default"))) void sts2_voice_play(const int16_t *samples, int count) {
	if (!gOutputWanted.exchange(true)) {
		Request();
	} else {
		Check();
	}
	if (count > 0) {
		gPlayback.Write(samples, (size_t)count);
	}
}

extern "C" __attribute__((visibility("default"))) void sts2_voice_stop_output(void) {
	if (gOutputWanted.exchange(false)) {
		Request();
	}
}

// For the log: 2 with voice processing on the phone's speaker, 1 on the speaker without it, 0 headphones.
extern "C" __attribute__((visibility("default"))) int sts2_voice_route(void) {
	return gRoute.load();
}

extern "C" __attribute__((visibility("default"))) const char *sts2_voice_last_error(void) {
	os_unfair_lock_lock(&gErrorLock);
	memcpy(gErrorForGame, gLastError, sizeof gErrorForGame);
	os_unfair_lock_unlock(&gErrorLock);
	return gErrorForGame;
}

NSString *PortNames(NSArray<AVAudioSessionPortDescription *> *ports) {
	NSMutableArray<NSString *> *names = [NSMutableArray array];
	for (AVAudioSessionPortDescription *port in ports) {
		[names addObject:port.portType];
	}
	return names.count > 0 ? [names componentsJoinedByString:@"+"] : @"none";
}

// For the log: what the voice engine runs and the audio session it shares with the game's sound
// (FMOD), read on the game thread. The managed side logs it when it changes.
extern "C" __attribute__((visibility("default"))) const char *sts2_voice_audio_state(void) {
	AVAudioSession *session = [AVAudioSession sharedInstance];
	NSString *engine = !gEngineRunning.load() ? @"off" : !gCaptureRunning.load() ? @"voice output" :
		gRoute.load() == 2 ? @"voice output and mic with echo removal" : @"voice output and mic";
	NSString *category = [session.category stringByReplacingOccurrencesOfString:@"AVAudioSessionCategory" withString:@""];
	NSString *mode = [session.mode stringByReplacingOccurrencesOfString:@"AVAudioSessionMode" withString:@""];
	NSString *text = [NSString stringWithFormat:@"engine %@; session %@ %@ options 0x%lx; %.0f Hz, buffer %.1f ms, out %@ (%ld ch), in %@",
		engine, category, mode, (unsigned long)session.categoryOptions, session.sampleRate, session.IOBufferDuration * 1000.0,
		PortNames(session.currentRoute.outputs), (long)session.outputNumberOfChannels, PortNames(session.currentRoute.inputs)];
	snprintf(gStateForGame, sizeof gStateForGame, "%s", text.UTF8String);
	return gStateForGame;
}

// Holding the functions here keeps the linker from removing them: nothing in the app calls them
// directly, the managed host finds them by name.
static void *volatile gExports[] = {
	(void *)&sts2_voice_capture_state, (void *)&sts2_voice_request_permission, (void *)&sts2_voice_start_capture,
	(void *)&sts2_voice_stop_capture, (void *)&sts2_voice_capture_status, (void *)&sts2_voice_read,
	(void *)&sts2_voice_queued, (void *)&sts2_voice_play, (void *)&sts2_voice_stop_output, (void *)&sts2_voice_route,
	(void *)&sts2_voice_last_error, (void *)&sts2_voice_audio_state
};

__attribute__((constructor)) static void STS2KeepVoiceExports(void) {
	(void)gExports[0];
}
