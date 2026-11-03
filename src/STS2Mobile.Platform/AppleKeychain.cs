using System.Runtime.InteropServices;

namespace STS2Mobile.Platform;

// All keys are device-local and inaccessible while the device is locked.
public sealed unsafe partial class AppleKeychain
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint CFStringEncodingUtf8 = 0x08000100;
    private const int ErrSecItemNotFound = -25300;
    private readonly string _service;
    private static readonly nint SecurityHandle = NativeLibrary.Load(Security);
    private static readonly nint CoreHandle = NativeLibrary.Load(CoreFoundation);
    public AppleKeychain(string service) => _service = service;

    public static string ApplicationIdentifier()
    {
        nint bundle = CFBundleGetMainBundle();
        nint identifier = bundle == 0 ? 0 : CFBundleGetIdentifier(bundle);
        if (identifier == 0) throw new InvalidOperationException("App bundle identifier is unavailable.");
        byte* buffer = stackalloc byte[1024];
        if (CFStringGetCString(identifier, buffer, 1024, CFStringEncodingUtf8) == 0)
            throw new InvalidOperationException("App bundle identifier cannot be read.");
        return Marshal.PtrToStringUTF8((nint)buffer)
            ?? throw new InvalidOperationException("App bundle identifier is empty.");
    }

    [LibraryImport(CoreFoundation)] private static partial nint CFBundleGetMainBundle();
    [LibraryImport(CoreFoundation)] private static partial nint CFBundleGetIdentifier(nint bundle);
    [LibraryImport(CoreFoundation)] private static partial byte CFStringGetCString(nint text, byte* buffer, nint size, uint encoding);

    private static nint Constant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(SecurityHandle, name));
    private static nint String(string value) => CFStringCreateWithCString(0, value, CFStringEncodingUtf8);
    private nint Query(string account)
    {
        nint query = CFDictionaryCreateMutable(0, 0, NativeLibrary.GetExport(CoreHandle, "kCFTypeDictionaryKeyCallBacks"),
            NativeLibrary.GetExport(CoreHandle, "kCFTypeDictionaryValueCallBacks"));
        nint service = String(_service), name = String(account);
        try
        {
            CFDictionarySetValue(query, Constant("kSecClass"), Constant("kSecClassGenericPassword"));
            CFDictionarySetValue(query, Constant("kSecAttrService"), service);
            CFDictionarySetValue(query, Constant("kSecAttrAccount"), name);
        }
        finally { CFRelease(service); CFRelease(name); }
        return query;
    }

    public byte[]? Read(string account)
    {
        nint query = Query(account), result = 0;
        try
        {
            CFDictionarySetValue(query, Constant("kSecReturnData"), Marshal.ReadIntPtr(NativeLibrary.GetExport(CoreHandle, "kCFBooleanTrue")));
            int status = SecItemCopyMatching(query, out result);
            if (status == ErrSecItemNotFound) return null;
            if (status != 0) throw new InvalidOperationException("Keychain read failed with status " + status);
            int length = checked((int)CFDataGetLength(result));
            var bytes = new byte[length];
            Marshal.Copy(CFDataGetBytePtr(result), bytes, 0, length);
            return bytes;
        }
        finally { if (result != 0) CFRelease(result); CFRelease(query); }
    }

    public void Write(string account, byte[] bytes)
    {
        nint query = Query(account), value = 0, attributes = 0;
        try
        {
            fixed (byte* pointer = bytes) value = CFDataCreate(0, pointer, bytes.Length);
            attributes = CFDictionaryCreateMutable(0, 0, NativeLibrary.GetExport(CoreHandle, "kCFTypeDictionaryKeyCallBacks"),
                NativeLibrary.GetExport(CoreHandle, "kCFTypeDictionaryValueCallBacks"));
            CFDictionarySetValue(attributes, Constant("kSecValueData"), value);
            int status = SecItemUpdate(query, attributes);
            if (status == ErrSecItemNotFound)
            {
                CFDictionarySetValue(query, Constant("kSecValueData"), value);
                CFDictionarySetValue(query, Constant("kSecAttrAccessible"), Constant("kSecAttrAccessibleWhenUnlockedThisDeviceOnly"));
                status = SecItemAdd(query, 0);
            }
            if (status != 0) throw new InvalidOperationException("Keychain write failed with status " + status);
        }
        finally { if (value != 0) CFRelease(value); if (attributes != 0) CFRelease(attributes); CFRelease(query); }
    }

    public void Delete(string account)
    {
        nint query = Query(account);
        try
        {
            int status = SecItemDelete(query);
            if (status != 0 && status != ErrSecItemNotFound) throw new InvalidOperationException("Keychain delete failed with status " + status);
        }
        finally { CFRelease(query); }
    }

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)] private static partial nint CFStringCreateWithCString(nint allocator, string text, uint encoding);
    [LibraryImport(CoreFoundation)] private static partial nint CFDictionaryCreateMutable(nint allocator, nint capacity, nint keys, nint values);
    [LibraryImport(CoreFoundation)] private static partial void CFDictionarySetValue(nint dictionary, nint key, nint value);
    [LibraryImport(CoreFoundation)] private static partial nint CFDataCreate(nint allocator, byte* bytes, nint length);
    [LibraryImport(CoreFoundation)] private static partial nint CFDataGetLength(nint data);
    [LibraryImport(CoreFoundation)] private static partial nint CFDataGetBytePtr(nint data);
    [LibraryImport(CoreFoundation)] private static partial void CFRelease(nint value);
    [LibraryImport(Security)] private static partial int SecItemCopyMatching(nint query, out nint result);
    [LibraryImport(Security)] private static partial int SecItemAdd(nint query, nint result);
    [LibraryImport(Security)] private static partial int SecItemUpdate(nint query, nint attributes);
    [LibraryImport(Security)] private static partial int SecItemDelete(nint query);
}
