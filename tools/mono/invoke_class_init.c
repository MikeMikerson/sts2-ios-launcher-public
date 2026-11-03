// Workaround 6: linked into the Mono library by tools/mono/build_mono.py, then mono_library.patch_class_init
// points the mono_runtime_class_init_full calls in mono_jit_runtime_invoke and mono_interp_transform_method here.
//
// Mono 10.0.12 runs a class's static constructor whenever reflection invokes one of its methods and before the
// interpreter runs any of its methods, even for BeforeFieldInit classes. CoreCLR and NativeAOT run those only at
// the first static field access, and the game depends on that: ModelDb.Init creates every model with
// Activator.CreateInstance, and an encounter's static field initializer looks up monsters that are not registered yet.
#include <stdint.h>
#include <mono/metadata/class.h>
#include <mono/metadata/object.h>
#include <mono/utils/mono-error.h>

#define TYPE_ATTRIBUTE_BEFORE_FIELD_INIT 0x00100000

extern int mono_runtime_class_init_full (MonoVTable *vtable, MonoError *error);

__attribute__((used)) int
sts2_invoke_class_init (MonoVTable *vtable, MonoError *error)
{
    MonoClass *klass = *(MonoClass **)vtable; // MonoVTable starts with its MonoClass
    if (mono_class_get_flags (klass) & TYPE_ATTRIBUTE_BEFORE_FIELD_INIT)
        return 1; // static field access still runs the static constructor
    return mono_runtime_class_init_full (vtable, error);
}
