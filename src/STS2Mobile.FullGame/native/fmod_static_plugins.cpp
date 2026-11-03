// Linked into the iOS app by tools/prepare_full_game_xcode.py.
//
// On iOS the FMOD Godot extension cannot load plugin libraries at run time, so during startup it
// calls load_all_fmod_plugins, which the app must define to register statically linked FMOD
// plugins. The extension's editor export plugin normally generates this function; this host does not
// run that plugin. The game's project settings declare no FMOD plugins, so none are registered.
// Without this definition the call jumps to a null address and the app dies during engine setup.
#include <cstdint>
#include <cstdlib>

extern "C" __attribute__((visibility("default"))) __attribute__((used))
uint32_t *load_all_fmod_plugins(void *fmod_ios_interface, uint32_t *r_count) {
	(void)fmod_ios_interface;
	*r_count = 0;
	// The extension frees the returned handle array, so it must come from malloc.
	return static_cast<uint32_t *>(std::malloc(sizeof(uint32_t)));
}
