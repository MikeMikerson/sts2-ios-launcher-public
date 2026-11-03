// A plain UIKit screen over the engine, for the end of setup.
//
// The shader warmup makes the phone's GPU report faults (gpuEvent reports: "MMU interrupt", with a compute slot active), after
// which nothing the engine submits reaches the screen until the app is started again, though its main loop and the warmup carry on.
// UIKit draws through the system, not through the engine's queue, so progress drawn here keeps moving and the button keeps working
// whatever state the GPU is in. The screen stays until the app closes: the next launch starts the engine clean.
//
// The managed host (STS2Mobile.Platform/SetupOverlay.cs) looks the sts2_overlay_* functions up in the app by name, on the main thread.
#include <stdio.h>
#include <unistd.h>
#import <UIKit/UIKit.h>

// The launcher's look (STS2Mobile.FullGame/LauncherTheme.cs), in its units: its portrait canvas is 1080 across.
static const CGFloat STS2CanvasWidth = 1080.0;
static const CGFloat STS2WordmarkSize = 46.0, STS2HeadingSize = 96.0, STS2BodySize = 48.0, STS2StepSize = 44.0, STS2ButtonHeight = 150.0,
					 STS2Gap = 36.0, STS2Side = 64.0, STS2StepHeight = 88.0;

static UIColor *STS2Colour(uint32_t rgb) {
	return [UIColor colorWithRed:((rgb >> 16) & 0xff) / 255.0 green:((rgb >> 8) & 0xff) / 255.0 blue:(rgb & 0xff) / 255.0 alpha:1.0];
}
#define STS2Dusk STS2Colour(0x1e1b2e)
#define STS2Stone STS2Colour(0x2a2640)
#define STS2Line STS2Colour(0x4a4466)
#define STS2Bone STS2Colour(0xede6d6)
#define STS2Mist STS2Colour(0xa9a2bf)
#define STS2Ember STS2Colour(0xe9b44c)
#define STS2Moss STS2Colour(0x8db580)

// The headings' face here: the system serif stands in for the launcher's Grenze, which lives in the engine's pack, out of UIKit's reach.
static UIFont *STS2HeadingFont(CGFloat size) {
	UIFontDescriptor *serif = [[UIFont systemFontOfSize:size weight:UIFontWeightSemibold].fontDescriptor fontDescriptorWithDesign:UIFontDescriptorSystemDesignSerif];
	return serif ? [UIFont fontWithDescriptor:serif size:size] : [UIFont systemFontOfSize:size weight:UIFontWeightSemibold];
}

@interface STS2SetupOverlay : UIView
@property(strong, nonatomic) UIStackView *steps;
@property(strong, nonatomic) UILabel *title;
@property(strong, nonatomic) UILabel *status;
@property(strong, nonatomic) UIProgressView *bar;
@property(strong, nonatomic) UIButton *button;
@property(nonatomic) CGFloat unit;
@property(copy, nonatomic) NSString *shownSteps;
@property(strong, nonatomic) UIView *rail;
@end

static STS2SetupOverlay *gOverlay = nil;

static UIWindow *STS2KeyWindow(void) {
	for (UIScene *scene in UIApplication.sharedApplication.connectedScenes) {
		if (![scene isKindOfClass:UIWindowScene.class]) {
			continue;
		}
		for (UIWindow *window in ((UIWindowScene *)scene).windows) {
			if (window.isKeyWindow) {
				return window;
			}
		}
	}
	return nil;
}

@implementation STS2SetupOverlay

- (instancetype)initWithFrame:(CGRect)frame {
	self = [super initWithFrame:frame];
	if (!self) {
		return nil;
	}
	self.backgroundColor = STS2Dusk;
	self.autoresizingMask = UIViewAutoresizingFlexibleWidth | UIViewAutoresizingFlexibleHeight;
	// The narrow side, so a landscape screen (a failure after the game turned) keeps the same sizes.
	CGFloat unit = MIN(frame.size.width, frame.size.height) / STS2CanvasWidth;
	_unit = unit;

	UILabel *wordmark = [[UILabel alloc] init];
	wordmark.text = @"STS2 Mobile";
	wordmark.font = STS2HeadingFont(STS2WordmarkSize * unit);
	wordmark.textColor = STS2Mist;
	wordmark.textAlignment = NSTextAlignmentRight;
	_title = [[UILabel alloc] init];
	_title.font = STS2HeadingFont(STS2HeadingSize * unit);
	_title.textColor = STS2Bone;
	_title.numberOfLines = 0;
	_steps = [[UIStackView alloc] init];
	_steps.axis = UILayoutConstraintAxisVertical;
	_status = [[UILabel alloc] init];
	_status.font = [UIFont systemFontOfSize:STS2BodySize * unit];
	_status.textColor = STS2Bone;
	_status.numberOfLines = 0;
	_bar = [[UIProgressView alloc] initWithProgressViewStyle:UIProgressViewStyleDefault];
	_bar.progressTintColor = STS2Ember;
	_bar.trackTintColor = STS2Stone;
	_button = [UIButton buttonWithType:UIButtonTypeCustom];
	_button.backgroundColor = STS2Ember;
	_button.layer.cornerRadius = 10 * unit;
	_button.titleLabel.font = [UIFont systemFontOfSize:STS2BodySize * unit weight:UIFontWeightSemibold];
	[_button setTitleColor:STS2Dusk forState:UIControlStateNormal];
	[_button addTarget:self action:@selector(close) forControlEvents:UIControlEventTouchUpInside];

	UIStackView *column = [[UIStackView alloc] initWithArrangedSubviews:@[ wordmark, _title, _steps, _status, _bar ]];
	column.axis = UILayoutConstraintAxisVertical;
	column.spacing = STS2Gap * unit;
	column.translatesAutoresizingMaskIntoConstraints = NO;
	_button.translatesAutoresizingMaskIntoConstraints = NO;
	[self addSubview:column];
	[self addSubview:_button];
	UILayoutGuide *safe = self.safeAreaLayoutGuide;
	[NSLayoutConstraint activateConstraints:@[
		[column.topAnchor constraintEqualToAnchor:safe.topAnchor constant:24 * unit],
		[column.leadingAnchor constraintEqualToAnchor:safe.leadingAnchor constant:STS2Side * unit],
		[column.trailingAnchor constraintEqualToAnchor:safe.trailingAnchor constant:-STS2Side * unit],
		[_button.leadingAnchor constraintEqualToAnchor:column.leadingAnchor],
		[_button.trailingAnchor constraintEqualToAnchor:column.trailingAnchor],
		[_button.bottomAnchor constraintEqualToAnchor:safe.bottomAnchor constant:-48 * unit],
		[_button.heightAnchor constraintEqualToConstant:STS2ButtonHeight * unit],
	]];
	return self;
}

// One row of the climb: a node and the step's name. The state is 0 done, 1 current, 2 waiting.
- (UIView *)stepRow:(NSString *)label state:(NSInteger)state {
	CGFloat unit = _unit, node = 34 * unit;
	UIView *dot = [[UIView alloc] init];
	dot.layer.cornerRadius = node / 2;
	dot.layer.borderWidth = (state == 0 ? 0 : (state == 1 ? 5 : 3)) * unit;
	dot.layer.borderColor = (state == 1 ? STS2Ember : STS2Line).CGColor;
	dot.backgroundColor = state == 0 ? STS2Moss : STS2Dusk;
	dot.translatesAutoresizingMaskIntoConstraints = NO;
	UILabel *name = [[UILabel alloc] init];
	name.text = label;
	name.font = [UIFont systemFontOfSize:STS2StepSize * unit weight:(state == 1 ? UIFontWeightSemibold : UIFontWeightRegular)];
	name.textColor = state == 1 ? STS2Bone : (state == 0 ? STS2Mist : STS2Line);
	UIStackView *row = [[UIStackView alloc] initWithArrangedSubviews:@[ dot, name ]];
	row.axis = UILayoutConstraintAxisHorizontal;
	row.alignment = UIStackViewAlignmentCenter;
	row.spacing = 40 * unit;
	[NSLayoutConstraint activateConstraints:@[
		[dot.widthAnchor constraintEqualToConstant:node], [dot.heightAnchor constraintEqualToConstant:node],
		[row.heightAnchor constraintEqualToConstant:STS2StepHeight * unit],
	]];
	return row;
}

// The steps as the launcher draws them: "label=state" joined by tabs, first step at the bottom, so the list climbs.
- (void)setStepLine:(NSString *)steps {
	// Progress comes many times a second during the warmup, with the same steps.
	if ([steps isEqualToString:_shownSteps]) {
		return;
	}
	_shownSteps = [steps copy];
	for (UIView *view in _steps.arrangedSubviews) {
		[_steps removeArrangedSubview:view];
		[view removeFromSuperview];
	}
	for (NSString *entry in [[steps componentsSeparatedByString:@"\t"] reverseObjectEnumerator]) {
		NSArray<NSString *> *parts = [entry componentsSeparatedByString:@"="];
		if (parts.count == 2) {
			[_steps addArrangedSubview:[self stepRow:parts[0] state:MIN(parts[1].integerValue, 2)]];
		}
	}
	_steps.hidden = _steps.arrangedSubviews.count == 0;
	// The line the nodes sit on, from the first node to the last, behind them.
	[_rail removeFromSuperview];
	_rail = nil;
	NSUInteger count = _steps.arrangedSubviews.count;
	if (count > 1) {
		CGFloat inset = STS2StepHeight * _unit / 2, width = 3 * _unit;
		_rail = [[UIView alloc] init];
		_rail.backgroundColor = STS2Line;
		_rail.translatesAutoresizingMaskIntoConstraints = NO;
		[_steps insertSubview:_rail atIndex:0];
		[NSLayoutConstraint activateConstraints:@[
			[_rail.centerXAnchor constraintEqualToAnchor:_steps.leadingAnchor constant:17 * _unit],
			[_rail.widthAnchor constraintEqualToConstant:width],
			[_rail.topAnchor constraintEqualToAnchor:_steps.topAnchor constant:inset],
			[_rail.bottomAnchor constraintEqualToAnchor:_steps.bottomAnchor constant:-inset],
		]];
	}
}

- (void)close {
	// Nothing in the app is open at this point of setup. iOS has no call for this; the player opens the app from the Home Screen.
	NSLog(@"Closing the app from the setup screen.");
	// exit(0) runs the engine's static destructors with its threads still going, and on the phone one ended in std::terminate.
	fflush(NULL);
	_exit(0);
}

@end

static NSString *STS2Text(const char *text) {
	return text ? [NSString stringWithUTF8String:text] : @"";
}

// Shows the screen, or updates it. A negative fraction hides the bar, and no button text hides the button.
extern "C" __attribute__((visibility("default"))) void sts2_overlay_show(const char *steps, const char *title, const char *status, float fraction, const char *button) {
	if (!gOverlay) {
		UIWindow *window = STS2KeyWindow();
		if (!window) {
			return;
		}
		gOverlay = [[STS2SetupOverlay alloc] initWithFrame:window.bounds];
		[window addSubview:gOverlay];
	}
	[gOverlay setStepLine:STS2Text(steps)];
	gOverlay.title.text = STS2Text(title);
	gOverlay.status.text = STS2Text(status);
	gOverlay.bar.hidden = fraction < 0;
	if (fraction >= 0) {
		gOverlay.bar.progress = fraction;
	}
	NSString *label = STS2Text(button);
	gOverlay.button.hidden = label.length == 0;
	[gOverlay.button setTitle:label forState:UIControlStateNormal];
}

// Presses the button, for a run with nobody there (STS2_AUTO_TAP).
extern "C" __attribute__((visibility("default"))) void sts2_overlay_press(void) {
	[gOverlay.button sendActionsForControlEvents:UIControlEventTouchUpInside];
}

// Holding the functions here keeps the linker from removing them: nothing in the app calls them
// directly, the managed host finds them by name.
__attribute__((used)) static void *volatile gOverlayExports[] = {
	(void *)&sts2_overlay_show, (void *)&sts2_overlay_press
};
