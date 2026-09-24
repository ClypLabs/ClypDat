#include "detector_stage.h"
#include <Windows.h>
#include <d3d11_4.h>
#include <wrl/client.h>
extern "C" {
#include <libavutil/buffer.h>
#include <libavutil/error.h>
#include <libavutil/frame.h>
#include <libavutil/macros.h>
#include <libswscale/swscale.h>
}
#include <algorithm>
#include <chrono>
#include <climits>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace clypdat {
namespace {
using Microsoft::WRL::ComPtr;
using Clock = std::chrono::steady_clock;
double since_ms(Clock::time_point started) { return std::chrono::duration<double, std::milli>(Clock::now() - started).count(); }
void check(int result, const char* what) {
    if (result >= 0) return;
    char text[AV_ERROR_MAX_STRING_SIZE]{}; av_strerror(result, text, sizeof(text));
    throw std::runtime_error(std::string(what) + ": " + text);
}
void checked(HRESULT result, const char* what) {
    if (SUCCEEDED(result)) return;
    char code[16]{}; std::snprintf(code, sizeof(code), "0x%08X", unsigned(result));
    throw std::runtime_error(std::string(what) + " (hr=" + code + ")");
}
struct FrameFree { void operator()(AVFrame* frame) const { av_frame_free(&frame); } };
using Canvas = std::unique_ptr<AVFrame, FrameFree>;

SwsContext* create_scaler(int source_width, int source_height, int width, int height) {
    auto* scaler = sws_getContext(source_width, source_height, AV_PIX_FMT_BGRA, width, height, AV_PIX_FMT_NV12, SWS_BILINEAR, nullptr, nullptr, nullptr);
    if (!scaler) return nullptr;
    const auto coefficients = sws_getCoefficients(SWS_CS_ITU709);
    if (sws_setColorspaceDetails(scaler, coefficients, 1, coefficients, 0, 0, 1 << 16, 1 << 16) < 0) { sws_freeContext(scaler); return nullptr; }
    return scaler;
}
// swscale's SIMD stores may extend past the fitted row width. Studio black
// over the bars of one canvas row, or all of it outside the fitted rows.
void restore_bars(uint8_t* row, int pitch, const CaptureRect& fit, int canvas_row, int divisor, uint8_t black) {
    if (canvas_row < fit.y / divisor || canvas_row >= (fit.y + fit.height) / divisor) { std::memset(row, black, pitch); return; }
    std::memset(row, black, fit.x); std::memset(row + fit.x + fit.width, black, pitch - fit.x - fit.width);
}
// The full-canvas conversion the recorder's CPU path uses: studio black,
// the aspect-fitted BT.709 limited-range image, black bars restored.
Canvas reference_canvas(const CapturePixels& pixels, int width, int height, SwsContext*& conversion) {
    Canvas frame(av_frame_alloc()); if (!frame) throw std::bad_alloc();
    frame->format = AV_PIX_FMT_NV12; frame->width = width; frame->height = height;
    check(av_frame_get_buffer(frame.get(), 32), "Allocate recording frame");
    for (int y = 0; y < height; ++y) std::memset(frame->data[0] + size_t(y) * frame->linesize[0], 16, width);
    for (int y = 0; y < height / 2; ++y) std::memset(frame->data[1] + size_t(y) * frame->linesize[1], 128, width);
    const auto fit = capture_aspect_fit(pixels.width, pixels.height, width, height);
    conversion = sws_getCachedContext(conversion, pixels.width, pixels.height, AV_PIX_FMT_BGRA,
        fit.width, fit.height, AV_PIX_FMT_NV12, SWS_BILINEAR, nullptr, nullptr, nullptr);
    if (!conversion) throw std::runtime_error("Initialize recording frame conversion");
    const auto coefficients = sws_getCoefficients(SWS_CS_ITU709);
    check(sws_setColorspaceDetails(conversion, coefficients, 1, coefficients, 0, 0, 1 << 16, 1 << 16), "Set BT.709 conversion");
    const uint8_t* input[] = {pixels.bgra.data()}; const int strides[] = {pixels.stride};
    uint8_t* output[] = {frame->data[0] + size_t(fit.y) * frame->linesize[0] + fit.x,
        frame->data[1] + size_t(fit.y / 2) * frame->linesize[1] + fit.x};
    check(sws_scale(conversion, input, strides, 0, pixels.height, output, frame->linesize), "Convert recording frame");
    for (int plane = 0; plane < 2; ++plane) {
        const int divisor = plane ? 2 : 1;
        for (int row = 0; row < height / divisor; ++row)
            restore_bars(frame->data[plane] + size_t(row) * frame->linesize[plane], frame->linesize[plane], fit, row, divisor, plane ? 128 : 16);
    }
    return frame;
}
// Crops the regions from an NV12 canvas and derives the counter mask of the
// third from its luma and chroma.
void crop(const uint8_t* luma, size_t luma_pitch, const uint8_t* chroma, size_t chroma_pitch, const std::array<CaptureRect, 3>& rects,
    bool counter_mask, RecordingDetectorSnapshot& snapshot) {
    for (size_t i = 0; i < 3; ++i) {
        const auto& rect = rects[i];
        auto& image = snapshot.regions[i]; image.width = rect.width; image.height = rect.height; image.pixels.resize(size_t(rect.width) * rect.height);
        for (int y = 0; y < rect.height; ++y) std::copy_n(luma + size_t(rect.y + y) * luma_pitch + rect.x, rect.width, image.pixels.data() + size_t(y) * rect.width);
        if (i != 2 || !counter_mask) continue;
        auto& mask = snapshot.third_mask; mask.width = rect.width; mask.height = rect.height; mask.pixels.resize(image.pixels.size());
        for (int y = 0; y < rect.height; ++y) for (int x = 0; x < rect.width; ++x) {
            const auto uv = chroma + size_t((rect.y + y) / 2) * chroma_pitch + ((rect.x + x) / 2) * 2;
            const double value = (image.pixels[size_t(y) * rect.width + x] - 16) * (255.0 / 219), u = uv[0] - 128, v = uv[1] - 128;
            const double r = std::clamp(value + 1.792741 * v, 0.0, 255.0), g = std::clamp(value - .213249 * u - .532909 * v, 0.0, 255.0),
                b = std::clamp(value + 2.112402 * u, 0.0, 255.0);
            const bool skull = int64_t(x) * 308 < int64_t(rect.width) * 120;
            const bool pink = skull && r > 70 && r > 1.6 * g && r > b + 35 && b > .15 * r;
            const bool gold = skull && r > 140 && g > 130 && b < .45 * std::min(r, g) && std::abs(r - g) < 80;
            const bool yellow = r > 140 && g > 130 && b < .45 * std::min(r, g) && std::abs(r - g) < 35;
            mask.pixels[size_t(y) * rect.width + x] = (pink || gold || yellow) ? 255 : 0;
        }
    }
    if (!counter_mask) { snapshot.third_mask.width = snapshot.third_mask.height = 0; snapshot.third_mask.pixels.clear(); }
}

constexpr int StrayAccess = INT_MIN;
// A region plan that under-estimates swscale's reach touches uncommitted
// pages; that sample falls back to the full-frame path.
int guarded_receive(SwsContext* scaler, unsigned start, unsigned rows) {
    __try { return sws_receive_slice(scaler, start, rows); }
    __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) { return StrayAccess; }
}
// FFmpeg 8.1.2's sws_receive_slice offsets every destination plane by the
// chroma row of slice_start, so luma lands at slice_start / 2. Mode 1 moves
// the luma pointer to compensate; mode 0 is a build that offsets luma by the
// full row.
ptrdiff_t luma_compensation(int start, int mode) { return mode ? start - (start >> 1) : 0; }
int convert_rows(SwsContext* scaler, AVFrame* output, AVFrame* input, uint8_t* luma, uint8_t* chroma, int pitch, int start, int rows, int mode) {
    output->data[0] = luma + luma_compensation(start, mode) * pitch; output->data[1] = chroma;
    output->linesize[0] = output->linesize[1] = pitch;
    int result = sws_frame_start(scaler, output, input);
    if (result < 0) return result;
    result = sws_send_slice(scaler, 0, unsigned(input->height));
    if (result >= 0) result = guarded_receive(scaler, unsigned(start), unsigned(rows));
    sws_frame_end(scaler);
    return result;
}
void no_free(void*, uint8_t*) {}
uint8_t anchor_byte = 0;
AVFrame* describe(AVPixelFormat format, int width, int height, AVBufferRef* anchor) {
    AVFrame* frame = av_frame_alloc(); if (!frame) throw std::bad_alloc();
    frame->format = format; frame->width = width; frame->height = height;
    // Referenced, so swscale refs it instead of copying the pixels.
    frame->buf[0] = av_buffer_ref(anchor); if (!frame->buf[0]) { av_frame_free(&frame); throw std::bad_alloc(); }
    return frame;
}
// Converts a small frame whole and as one destination slice to find where
// this swscale writes slice luma. -1 when neither placement is exact.
int detect_slice_mode() {
    constexpr int sw = 96, sh = 64, dw = 64, dh = 40, start = 12, rows = 16;
    std::vector<uint8_t> source(size_t(sw) * sh * 4);
    for (int y = 0; y < sh; ++y) for (int x = 0; x < sw; ++x) {
        auto* p = &source[(size_t(y) * sw + x) * 4]; p[0] = uint8_t(x * 7 + y * 3); p[1] = uint8_t(x * y); p[2] = uint8_t(255 - x * 5 + y * 11); p[3] = 255;
    }
    SwsContext* scaler = create_scaler(sw, sh, dw, dh); if (!scaler) return -1;
    std::vector<uint8_t> expected_luma(size_t(dw) * dh), expected_chroma(size_t(dw) * dh / 2);
    const uint8_t* in[] = {source.data()}; const int in_pitch[] = {sw * 4};
    uint8_t* out[] = {expected_luma.data(), expected_chroma.data()}; const int out_pitch[] = {dw, dw};
    int found = -1;
    AVBufferRef* anchor = av_buffer_create(&anchor_byte, 1, no_free, nullptr, 0);
    if (anchor && sws_scale(scaler, in, in_pitch, 0, sh, out, out_pitch) == dh) {
        AVFrame* input = describe(AV_PIX_FMT_BGRA, sw, sh, anchor); input->data[0] = source.data(); input->linesize[0] = sw * 4;
        AVFrame* output = describe(AV_PIX_FMT_NV12, dw, dh, anchor);
        for (int mode = 0; mode < 2 && found < 0; ++mode) {
            std::vector<uint8_t> luma(size_t(dw) * dh, 0xEE), chroma(size_t(dw) * dh / 2, 0xEE);
            if (convert_rows(scaler, output, input, luma.data(), chroma.data(), dw, start, rows, mode) < 0) continue;
            if (!std::memcmp(luma.data() + size_t(start) * dw, expected_luma.data() + size_t(start) * dw, size_t(rows) * dw) &&
                !std::memcmp(chroma.data() + size_t(start / 2) * dw, expected_chroma.data() + size_t(start / 2) * dw, size_t(rows / 2) * dw)) found = mode;
        }
        av_frame_free(&input); av_frame_free(&output);
    }
    av_buffer_unref(&anchor); sws_freeContext(scaler);
    return found;
}
int slice_mode() { static const int mode = detect_slice_mode(); return mode; }

// Address space for a whole plane, committed only where the plan reads or
// writes. Anything else faults instead of reading stale memory silently.
struct Plane {
    uint8_t* base = nullptr; size_t size = 0;
    Plane() = default;
    Plane(const Plane&) = delete; Plane& operator=(const Plane&) = delete;
    ~Plane() { reset(); }
    static size_t page() { static const size_t value = [] { SYSTEM_INFO info{}; GetSystemInfo(&info); return size_t(info.dwPageSize); }(); return value; }
    void reserve(size_t bytes) {
        reset(); size = (bytes + 0xFFFF) & ~size_t(0xFFFF);
        base = static_cast<uint8_t*>(VirtualAlloc(nullptr, size, MEM_RESERVE, PAGE_NOACCESS));
        if (!base) { size = 0; throw std::bad_alloc(); }
    }
    void commit(size_t first, size_t last) {
        first &= ~(page() - 1); last = std::min(size, (last + page() - 1) & ~(page() - 1));
        if (first < last && !VirtualAlloc(base + first, last - first, MEM_COMMIT, PAGE_READWRITE)) throw std::bad_alloc();
    }
    void reset() { if (base) VirtualFree(base, 0, MEM_RELEASE); base = nullptr; size = 0; }
};
}

std::array<CaptureRect, 3> detector_region_rects(const DetectorRequest& request) {
    std::array<CaptureRect, 3> rects{};
    for (size_t i = 0; i < 3; ++i) {
        const auto& region = request.regions[i]; auto& rect = rects[i];
        rect.x = std::clamp(int(std::nearbyint(region.x * request.width)), 0, request.width - 1);
        rect.y = std::clamp(int(std::nearbyint(region.y * request.height)), 0, request.height - 1);
        rect.width = std::clamp(int(std::nearbyint(region.width * request.width)), 1, request.width - rect.x);
        rect.height = std::clamp(int(std::nearbyint(region.height * request.height)), 1, request.height - rect.y);
    }
    return rects;
}

void detector_crop(const uint8_t* luma, size_t luma_pitch, const uint8_t* chroma, size_t chroma_pitch, const DetectorRequest& request,
    RecordingDetectorSnapshot& snapshot) {
    crop(luma, luma_pitch, chroma, chroma_pitch, detector_region_rects(request), request.counter_mask, snapshot);
}

bool detector_reference_sample(CapturePixels& pixels, const DetectorRequest& request, SwsContext*& scaler,
    const std::function<bool()>& cancelled, RecordingDetectorSnapshot& snapshot) {
    if (!capture_copy_texture_pixels_nonblocking(pixels, cancelled)) return false;
    const auto canvas = reference_canvas(pixels, request.width, request.height, scaler);
    snapshot.timestamp_us = pixels.timestamp_us;
    crop(canvas->data[0], size_t(canvas->linesize[0]), canvas->data[1], size_t(canvas->linesize[1]), detector_region_rects(request), request.counter_mask, snapshot);
    return true;
}

struct DetectorStage::Impl {
    DetectorStageCounters counters;
    DetectorStageTiming timing;
    // What the plan was built for.
    bool built = false, gpu = false;
    ID3D11Device* device_key = nullptr;
    int source_width = 0, source_height = 0, source_stride = 0;
    DetectorRequest request;
    // Source rects read back, stacked in the staging texture from `row`.
    struct Copy { int x = 0, y = 0, width = 0, height = 0, row = 0; };
    std::array<Copy, 3> copies{}; int copy_count = 0;
    // Output rows converted, relative to the fitted image; even.
    struct Band { int start = 0, rows = 0; };
    std::array<Band, 3> bands{}; int band_count = 0;
    CaptureRect fit{};
    std::array<CaptureRect, 3> rects{};
    int pitch = 0;
    // Grows after a stray access so the rebuilt plan reaches further.
    int halo_scale = 1;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11Texture2D> staging;
    Plane source, luma, chroma;
    SwsContext* scaler = nullptr;
    AVBufferRef* anchor = nullptr;
    AVFrame* input = nullptr;
    AVFrame* output = nullptr;
    SwsContext* reference_scaler = nullptr;
    ~Impl() { release(); sws_freeContext(reference_scaler); }
    void release() {
        built = false; context.Reset(); staging.Reset(); source.reset(); luma.reset(); chroma.reset();
        sws_freeContext(scaler); scaler = nullptr; av_frame_free(&input); av_frame_free(&output); av_buffer_unref(&anchor);
    }
    bool matches(const CapturePixels& pixels, const DetectorRequest& wanted, bool from_gpu, ID3D11Device* device) const {
        if (!built || gpu != from_gpu || device_key != device || source_width != pixels.width || source_height != pixels.height ||
            source_stride != pixels.stride || request.width != wanted.width || request.height != wanted.height) return false;
        for (size_t i = 0; i < 3; ++i) {
            const auto& a = request.regions[i]; const auto& b = wanted.regions[i];
            if (a.x != b.x || a.y != b.y || a.width != b.width || a.height != b.height) return false;
        }
        return true;
    }
    void build(const CapturePixels& pixels, const DetectorRequest& wanted, bool from_gpu, ID3D11Device* device) {
        const bool rebuild = counters.staging_rebuilds > 0;
        release();
        gpu = from_gpu; device_key = device; source_width = pixels.width; source_height = pixels.height; source_stride = pixels.stride; request = wanted;
        fit = capture_aspect_fit(pixels.width, pixels.height, wanted.width, wanted.height);
        rects = detector_region_rects(wanted);
        pitch = FFALIGN(wanted.width, 32); // av_frame_get_buffer's NV12 pitch, so SIMD paths match.
        const double scale_x = double(pixels.width) / fit.width, scale_y = double(pixels.height) / fit.height;
        // Bilinear taps reach 2x the scale for chroma; twice that plus
        // swscale's filter alignment is the halo.
        const int halo_x = (int(std::ceil(4 * std::max(scale_x, 1.0))) + 16) * halo_scale;
        const int halo_y = (int(std::ceil(4 * std::max(scale_y, 1.0))) + 8) * halo_scale;
        auto source_rows = [&](int start, int end, int halo) {
            return std::pair{std::clamp(int(std::floor(start * scale_y)) - halo, 0, pixels.height), std::clamp(int(std::ceil(end * scale_y)) + halo, 0, pixels.height)};
        };
        std::array<Band, 3> wanted_rows{}; int wanted_count = 0;
        copy_count = band_count = 0; int staging_width = 0, staging_height = 0;
        for (const auto& rect : rects) {
            const int left = std::max(rect.x, fit.x) - fit.x, right = std::min(rect.x + rect.width, fit.x + fit.width) - fit.x;
            const int top = std::max(rect.y, fit.y) - fit.y, bottom = std::min(rect.y + rect.height, fit.y + fit.height) - fit.y;
            if (left >= right || top >= bottom) continue; // Only bars.
            const int start = top & ~1, end = std::min(fit.height, (bottom + 1) & ~1);
            wanted_rows[wanted_count++] = {start, end - start};
            Copy copy;
            copy.x = std::clamp(int(std::floor(left * scale_x)) - halo_x, 0, pixels.width);
            copy.width = std::clamp(int(std::ceil(right * scale_x)) + halo_x, 0, pixels.width) - copy.x;
            const auto [first, last] = source_rows(start, end, halo_y);
            copy.y = first; copy.height = last - first; copy.row = staging_height;
            staging_width = std::max(staging_width, copy.width); staging_height += copy.height;
            copies[copy_count++] = copy;
        }
        std::sort(wanted_rows.begin(), wanted_rows.begin() + wanted_count, [](const Band& a, const Band& b) { return a.start < b.start; });
        for (int i = 0; i < wanted_count; ++i) {
            const auto& next = wanted_rows[i];
            // Close bands share one conversion pass instead of refilling
            // swscale's line buffers for each.
            if (band_count && next.start <= bands[band_count - 1].start + bands[band_count - 1].rows + 8) {
                auto& band = bands[band_count - 1]; band.rows = std::max(band.rows, next.start + next.rows - band.start);
            } else bands[band_count++] = next;
        }
        uint64_t allocations = 2;
        luma.reserve(size_t(pitch) * (wanted.height + 2)); chroma.reserve(size_t(pitch) * (wanted.height / 2 + 2));
        for (const auto& rect : rects) {
            luma.commit(size_t(rect.y) * pitch, size_t(rect.y + rect.height) * pitch);
            chroma.commit(size_t(rect.y / 2) * pitch, size_t((rect.y + rect.height + 1) / 2) * pitch);
        }
        for (int i = 0; i < band_count; ++i) {
            // Two rows past each band for SIMD stores beyond the row end.
            const int first = fit.y + bands[i].start, last = first + bands[i].rows;
            luma.commit(size_t(first) * pitch, size_t(last + 2) * pitch);
            chroma.commit(size_t(first / 2) * pitch, size_t(last / 2 + 2) * pitch);
        }
        if (gpu && copy_count) {
            ++allocations; source.reserve(size_t(pixels.stride) * (pixels.height + 2));
            for (int i = 0; i < band_count; ++i) {
                const auto [first, last] = source_rows(bands[i].start, bands[i].start + bands[i].rows, halo_y + 2);
                source.commit(size_t(first) * pixels.stride, size_t(last + (last == pixels.height ? 2 : 0)) * pixels.stride);
            }
            ComPtr<ID3D11Device> owner; pixels.texture->GetDevice(&owner); owner->GetImmediateContext(&context);
            D3D11_TEXTURE2D_DESC desc{}; desc.Width = UINT(staging_width); desc.Height = UINT(staging_height); desc.MipLevels = 1; desc.ArraySize = 1;
            desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1; desc.Usage = D3D11_USAGE_STAGING; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            checked(owner->CreateTexture2D(&desc, nullptr, &staging), "Allocate detector readback");
            ++allocations; ++counters.gpu_textures_allocated;
        }
        counters.cpu_buffers_allocated += gpu && copy_count ? 3 : 2;
        if (rebuild) counters.allocations_after_warmup += allocations;
        ++counters.staging_rebuilds;
        if (band_count) {
            scaler = create_scaler(pixels.width, pixels.height, fit.width, fit.height);
            if (!scaler) throw std::runtime_error("Initialize detector conversion");
            anchor = av_buffer_create(&anchor_byte, 1, no_free, nullptr, 0); if (!anchor) throw std::bad_alloc();
            input = describe(AV_PIX_FMT_BGRA, pixels.width, pixels.height, anchor);
            output = describe(AV_PIX_FMT_NV12, fit.width, fit.height, anchor);
        }
        built = true;
    }
    bool fallback(const CapturePixels& pixels, const DetectorRequest& wanted, const std::function<bool()>& cancelled, RecordingDetectorSnapshot& snapshot) {
        const auto started = Clock::now();
        auto owned = pixels; const bool texture = owned.texture && owned.bgra.empty();
        if (!detector_reference_sample(owned, wanted, reference_scaler, cancelled, snapshot)) { ++counters.cancelled; return false; }
        const uint64_t bytes = texture ? uint64_t(owned.stride) * owned.height : 0;
        counters.gpu_textures_allocated += texture; counters.cpu_buffers_allocated += texture ? 2 : 1;
        counters.readback_bytes += bytes; ++counters.samples;
        timing = {0, 0, since_ms(started), bytes};
        return true;
    }
};

DetectorStage::DetectorStage() : impl_(std::make_unique<Impl>()) {}
DetectorStage::~DetectorStage() = default;
void DetectorStage::release() { impl_->release(); }
const DetectorStageCounters& DetectorStage::counters() const { return impl_->counters; }
const DetectorStageTiming& DetectorStage::timing() const { return impl_->timing; }

bool DetectorStage::sample(const CapturePixels& pixels, const DetectorRequest& request, const std::function<bool()>& cancelled,
    RecordingDetectorSnapshot& snapshot) {
    auto& s = *impl_;
    snapshot.timestamp_us = pixels.timestamp_us;
    if (reference) {
        // Timed as the old path was: readback including the GPU wait, then conversion.
        const auto started = Clock::now();
        auto owned = pixels; const bool texture = owned.texture && owned.bgra.empty();
        if (!capture_copy_texture_pixels_nonblocking(owned, cancelled)) { ++s.counters.cancelled; return false; }
        const double readback = since_ms(started);
        const auto converted = Clock::now();
        if (!detector_reference_sample(owned, request, s.reference_scaler, cancelled, snapshot)) { ++s.counters.cancelled; return false; }
        const uint64_t bytes = texture ? uint64_t(owned.stride) * owned.height : 0;
        s.counters.gpu_textures_allocated += texture; s.counters.cpu_buffers_allocated += texture ? 2 : 1;
        s.counters.readback_bytes += bytes; ++s.counters.samples;
        s.timing = {0, readback, since_ms(converted), bytes};
        return true;
    }
    const bool gpu = pixels.texture && pixels.bgra.empty();
    ComPtr<ID3D11Device> device;
    if (gpu) {
        D3D11_TEXTURE2D_DESC desc{}; pixels.texture->GetDesc(&desc);
        if (desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM) throw std::runtime_error("Detector readback requires BGRA");
        pixels.texture->GetDevice(&device);
    }
    if (slice_mode() < 0) { ++s.counters.fallbacks; return s.fallback(pixels, request, cancelled, snapshot); }
    if (!s.matches(pixels, request, gpu, device.Get())) s.build(pixels, request, gpu, device.Get());
    DetectorStageTiming timing;
    auto started = Clock::now();
    if (gpu && s.copy_count) {
        for (int i = 0; i < s.copy_count; ++i) {
            const auto& copy = s.copies[i];
            const D3D11_BOX box{UINT(copy.x), UINT(copy.y), 0, UINT(copy.x + copy.width), UINT(copy.y + copy.height), 1};
            s.context->CopySubresourceRegion(s.staging.Get(), 0, 0, UINT(copy.row), 0, pixels.texture.get(), 0, &box);
        }
        // Submit now rather than behind the next encoder flush, so the source
        // frame is held for the copy only.
        s.context->Flush();
        D3D11_MAPPED_SUBRESOURCE mapped{};
        for (;;) {
            if (cancelled()) { ++s.counters.cancelled; return false; }
            const auto result = readback_pending && readback_pending() ? DXGI_ERROR_WAS_STILL_DRAWING
                : s.context->Map(s.staging.Get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped);
            if (result == DXGI_ERROR_WAS_STILL_DRAWING) { std::this_thread::sleep_for(std::chrono::milliseconds(1)); continue; }
            checked(result, "Map detector readback"); break;
        }
        timing.gpu_ms = since_ms(started); started = Clock::now();
        for (int i = 0; i < s.copy_count; ++i) {
            const auto& copy = s.copies[i];
            for (int row = 0; row < copy.height; ++row)
                std::memcpy(s.source.base + size_t(copy.y + row) * s.source_stride + size_t(copy.x) * 4,
                    static_cast<const uint8_t*>(mapped.pData) + size_t(copy.row + row) * mapped.RowPitch, size_t(copy.width) * 4);
            timing.bytes += uint64_t(copy.width) * 4 * copy.height;
        }
        s.context->Unmap(s.staging.Get(), 0);
        timing.readback_ms = since_ms(started); started = Clock::now();
    }
    if (s.band_count) {
        s.input->data[0] = gpu ? s.source.base : const_cast<uint8_t*>(pixels.bgra.data()); s.input->linesize[0] = pixels.stride;
        uint8_t* luma = s.luma.base + size_t(s.fit.y) * s.pitch + s.fit.x;
        uint8_t* chroma = s.chroma.base + size_t(s.fit.y / 2) * s.pitch + s.fit.x;
        for (int i = 0; i < s.band_count; ++i) {
            const int result = convert_rows(s.scaler, s.output, s.input, luma, chroma, s.pitch, s.bands[i].start, s.bands[i].rows, slice_mode());
            if (result == StrayAccess) {
                s.halo_scale = std::min(s.halo_scale * 2, 64); s.built = false; ++s.counters.fallbacks;
                return s.fallback(pixels, request, cancelled, snapshot);
            }
            check(result, "Convert detector regions");
        }
    }
    for (size_t i = 0; i < 3; ++i) {
        const auto& rect = s.rects[i];
        for (int row = rect.y; row < rect.y + rect.height; ++row) restore_bars(s.luma.base + size_t(row) * s.pitch, s.pitch, s.fit, row, 1, 16);
        if (i == 2 && request.counter_mask)
            for (int row = rect.y / 2; row <= (rect.y + rect.height - 1) / 2; ++row) restore_bars(s.chroma.base + size_t(row) * s.pitch, s.pitch, s.fit, row, 2, 128);
    }
    crop(s.luma.base, size_t(s.pitch), s.chroma.base, size_t(s.pitch), s.rects, request.counter_mask, snapshot);
    timing.convert_ms = since_ms(started);
    s.counters.readback_bytes += timing.bytes; ++s.counters.samples;
    s.timing = timing;
    return true;
}
}
