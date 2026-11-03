// Turns one of the game's desktop textures (S3TC or BPTC in Godot's .ctex container) into ASTC 4x4, with the bytes the Mac's
// conversion gives. Built by tools/build_texture_encoder.py,
// with -ffp-contract=off, which the bytes depend on. Called from TextureEncoder.cs.
#include "astcenc.h"

#define BCDEC_IMPLEMENTATION
#include "bcdec.h"

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <new>
#include <system_error>
#include <thread>
#include <vector>

#define API extern "C" __attribute__((visibility("default")))

namespace {

constexpr size_t kHeaderBytes = 36;      // magic, version, width, height, flags, mipmap limit, 3 reserved words
constexpr size_t kImageHeaderBytes = 16; // data format, width, height (u16), mipmaps, image format
constexpr size_t kPrefixBytes = kHeaderBytes + kImageHeaderBytes;
constexpr uint32_t kDxt1 = 17, kDxt3 = 18, kDxt5 = 19, kBptcRgba = 22, kAstc4x4 = 35;
constexpr size_t kAstcBlockBytes = 16;

// The same numbers as TextureEncoder.cs's Failure.
enum Failure : int32_t {
    kOk = 0,
    kNoContext = -1,
    kNotAContainer = -2,
    kUnsupportedFormat = -3,
    kNotWholeBlocks = -4,
    kWrongDataSize = -5,
    kOutputTooSmall = -6,
    kEncoderFailed = -7,
    kOutOfMemory = -8,
};

uint32_t read_u32(const uint8_t *p) {
    uint32_t v;
    memcpy(&v, p, 4);
    return v;
}

uint16_t read_u16(const uint8_t *p) {
    uint16_t v;
    memcpy(&v, p, 2);
    return v;
}

// Godot's Image::_get_dst_image_size for block formats: each mip halves down to 1x1 and rounds up to whole blocks.
size_t mip_bytes(int w, int h, size_t bytes_per_block) {
    return size_t((w + 3) / 4) * size_t((h + 3) / 4) * bytes_per_block;
}

using BlockDecoder = void (*)(const void *, void *, int);

struct Layout {
    int width = 0, height = 0, mip_count = 0;
    BlockDecoder decoder = nullptr;
    size_t source_block = 0, output_bytes = 0;
};

Failure read_layout(const uint8_t *source, int64_t size, Layout &layout) {
    if (size < int64_t(kPrefixBytes) || memcmp(source, "GST2", 4) != 0 || read_u32(source + 4) != 1) {
        return kNotAContainer;
    }
    const uint8_t *image_header = source + kHeaderBytes;
    const uint32_t mipmaps = read_u32(image_header + 8), format = read_u32(image_header + 12);
    layout.width = read_u16(image_header + 4);
    layout.height = read_u16(image_header + 6);
    layout.decoder = format == kDxt1 ? bcdec_bc1 : format == kDxt3 ? bcdec_bc2 : format == kDxt5 ? bcdec_bc3 : format == kBptcRgba ? bcdec_bc7 : nullptr;
    layout.source_block = format == kDxt1 ? 8 : 16;
    if (read_u32(image_header) != 0 || !layout.decoder) {
        return kUnsupportedFormat;
    }
    // Godot's encoder resizes such a texture and this one does not. No texture of a supported version is one.
    if (layout.width < 1 || layout.height < 1 || layout.width % 4 || layout.height % 4) {
        return kNotWholeBlocks;
    }
    size_t source_needed = kPrefixBytes;
    layout.output_bytes = kPrefixBytes;
    layout.mip_count = 0;
    for (int w = layout.width, h = layout.height;; w = std::max(1, w >> 1), h = std::max(1, h >> 1)) {
        source_needed += mip_bytes(w, h, layout.source_block);
        layout.output_bytes += mip_bytes(w, h, kAstcBlockBytes);
        layout.mip_count++;
        if (mipmaps == 0 || (w == 1 && h == 1)) {
            break;
        }
    }
    return size_t(size) == source_needed ? kOk : kWrongDataSize;
}

// Writes width x height RGBA8. Blocks that hang over the edge go through a small buffer, as Godot's "safe" path does.
void decode_mip(BlockDecoder decode, size_t bytes_per_block, int width, int height, const uint8_t *source, uint8_t *pixels) {
    const int pitch = width * 4;
    uint8_t edge[16 * 4];
    for (int y = 0; y < height; y += 4) {
        for (int x = 0; x < width; x += 4) {
            uint8_t *destination = pixels + size_t(y) * pitch + size_t(x) * 4;
            if (x + 4 <= width && y + 4 <= height) {
                decode(source, destination, pitch);
            } else {
                decode(source, edge, 16);
                const int columns = std::min(4, width - x), rows = std::min(4, height - y);
                for (int row = 0; row < rows; row++) {
                    memcpy(destination + size_t(row) * pitch, edge + row * 16, size_t(columns) * 4);
                }
            }
            source += bytes_per_block;
        }
    }
}

astcenc_error encode_mip(astcenc_context *context, unsigned threads, astcenc_image &image, uint8_t *output, size_t bytes) {
    static const astcenc_swizzle swizzle = {ASTCENC_SWZ_R, ASTCENC_SWZ_G, ASTCENC_SWZ_B, ASTCENC_SWZ_A};
    std::vector<astcenc_error> errors(threads, ASTCENC_SUCCESS);
    std::vector<std::thread> workers;
    workers.reserve(threads - 1);
    try {
        for (unsigned t = 1; t < threads; t++) {
            workers.emplace_back([&, t] { errors[t] = astcenc_compress_image(context, &image, &swizzle, output, bytes, t); });
        }
    } catch (const std::system_error &) {
        // The threads that did start share all the work between them, with the same bytes.
    }
    errors[0] = astcenc_compress_image(context, &image, &swizzle, output, bytes, 0);
    for (std::thread &worker : workers) {
        worker.join();
    }
    for (astcenc_error error : errors) {
        if (error != ASTCENC_SUCCESS) {
            return error;
        }
    }
    return ASTCENC_SUCCESS;
}

} // namespace

struct sts2_texture_encoder {
    astcenc_context *context;
    unsigned threads;
};

// Threads are per call to convert: a context is made for a number of threads and runs a texture on that many.
API sts2_texture_encoder *sts2_texture_encoder_create(int32_t threads) {
    astcenc_config config;
    // The same call as Godot's _compress_astc: LDR, 4x4, medium, no flags.
    if (threads < 1 || astcenc_config_init(ASTCENC_PRF_LDR, 4, 4, 1, ASTCENC_PRE_MEDIUM, 0, &config) != ASTCENC_SUCCESS) {
        return nullptr;
    }
    astcenc_context *context = nullptr;
    if (astcenc_context_alloc(&config, unsigned(threads), &context) != ASTCENC_SUCCESS) {
        return nullptr;
    }
    return new (std::nothrow) sts2_texture_encoder{context, unsigned(threads)};
}

API void sts2_texture_encoder_free(sts2_texture_encoder *encoder) {
    if (encoder) {
        astcenc_context_free(encoder->context);
        delete encoder;
    }
}

// The size of the converted file, or a Failure.
API int64_t sts2_texture_encoder_output_bytes(const uint8_t *source, int64_t size) {
    Layout layout;
    const Failure failure = read_layout(source, size, layout);
    return failure == kOk ? int64_t(layout.output_bytes) : int64_t(failure);
}

// One texture, one mip's pixels held at a time. Returns 0 or a Failure. One encoder runs one texture at a time.
API int32_t sts2_texture_encoder_convert(sts2_texture_encoder *encoder, const uint8_t *source, int64_t size, uint8_t *output, int64_t capacity) {
    if (!encoder) {
        return kNoContext;
    }
    Layout layout;
    const Failure failure = read_layout(source, size, layout);
    if (failure != kOk) {
        return failure;
    }
    if (capacity < int64_t(layout.output_bytes)) {
        return kOutputTooSmall;
    }
    try {
        std::vector<uint8_t> pixels(size_t(layout.width) * layout.height * 4);
        memcpy(output, source, kPrefixBytes);
        memcpy(output + kHeaderBytes + 12, &kAstc4x4, 4);
        const uint8_t *read = source + kPrefixBytes;
        uint8_t *write = output + kPrefixBytes;
        int w = layout.width, h = layout.height;
        for (int mip = 0; mip < layout.mip_count; mip++) {
            decode_mip(layout.decoder, layout.source_block, w, h, read, pixels.data());
            uint8_t *mip_data = pixels.data();
            astcenc_image image;
            image.dim_x = w;
            image.dim_y = h;
            image.dim_z = 1;
            image.data_type = ASTCENC_TYPE_U8;
            image.data = reinterpret_cast<void **>(&mip_data);
            const size_t bytes = mip_bytes(w, h, kAstcBlockBytes);
            const astcenc_error error = encode_mip(encoder->context, encoder->threads, image, write, bytes);
            astcenc_compress_reset(encoder->context);
            if (error != ASTCENC_SUCCESS) {
                return kEncoderFailed;
            }
            read += mip_bytes(w, h, layout.source_block);
            write += bytes;
            w = std::max(1, w >> 1);
            h = std::max(1, h >> 1);
        }
    } catch (const std::bad_alloc &) {
        return kOutOfMemory;
    }
    return kOk;
}
