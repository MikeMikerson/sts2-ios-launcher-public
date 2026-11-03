// Loads game assemblies from the app's data folder. Linked into the Mono library by
// tools/mono/build_mono.py for the variant that holds no game files.
//
// Mono asks this after it has searched the embedded assemblies, so a file here can never replace an assembly of the app.
// The hook is in place before managed code runs, because a reference that fails once stays failed for that image.
// Mono maps the file, so its image is page aligned and keeps its exception clauses (workaround 8).
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <mono/metadata/assembly.h>

static MonoAssembly *
sts2_load_game_assembly (MonoAssemblyName *name, char **assemblies_path, void *user_data)
{
    const char *home = getenv ("HOME");
    const char *assembly = mono_assembly_name_get_name (name);
    const char *mods = getenv ("STS2_MODS");
    // Never fall back to a woven assembly when mods are off. The selected file is still mapped by Mono.
    if (strcmp (assembly, "sts2") == 0 && (!mods || strcmp (mods, "1") != 0))
        assembly = "sts2.unmodded";
    char path[PATH_MAX];
    struct stat file;
    if (!home || snprintf (path, sizeof path, "%s/Documents/game/%s.dll", home, assembly) >= (int)sizeof path)
        return NULL;
    if (stat (path, &file) != 0 || !S_ISREG (file.st_mode))
        return NULL;
    return mono_assembly_open (path, NULL);
}

__attribute__((constructor)) static void
sts2_install_game_assembly_hook (void)
{
    mono_install_assembly_preload_hook (sts2_load_game_assembly, NULL);
}
