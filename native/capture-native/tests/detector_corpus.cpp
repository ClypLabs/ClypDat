// Detector regression corpus: real game frames through the production
// normalized detector path, end to end through RecordingCapture, one line per
// frame, layout and output size. Uses only the public capture API so the same
// harness records the reference from an earlier build and checks a later one.
//
// detector_corpus <frames dir> <out.tsv> [--layouts helldivers2,overwatch,fortnite]
//                 [--outputs 1920x1080,2560x1440,3840x2160] [--source-scale N] [--cpu]
//
// Each image is shown as repeated frames stamped (index + 1) seconds plus a
// millisecond per repeat, so the detector's 500 ms cadence samples every image
// exactly once, in order, without waiting on the wall clock.
#include "recording_capture.h"
#include <Windows.h>
#include <d3d11_4.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <map>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>
extern "C" {
#include <libavutil/log.h>
}

using namespace clypdat;
using namespace std::chrono_literals;
using Microsoft::WRL::ComPtr;

namespace {
struct Frame { std::string name; std::filesystem::path path; int width = 0, height = 0; };
struct Pixels { int width = 0, height = 0; std::vector<uint8_t> bgra; };

ComPtr<IWICBitmapSource> open(IWICImagingFactory* wic, const std::filesystem::path& path) {
    ComPtr<IWICBitmapDecoder> decoder; ComPtr<IWICBitmapFrameDecode> frame; ComPtr<IWICFormatConverter> converter;
    if (FAILED(wic->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnDemand, &decoder)) ||
        FAILED(decoder->GetFrame(0, &frame)) || FAILED(wic->CreateFormatConverter(&converter)) ||
        FAILED(converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom)))
        throw std::runtime_error("Cannot decode " + path.string());
    return converter;
}

// Nearest-neighbour upscale stands in for a higher-resolution source of the
// same game frame.
Pixels decode(IWICImagingFactory* wic, const std::filesystem::path& path, int scale) {
    auto source = open(wic, path);
    UINT w = 0, h = 0; source->GetSize(&w, &h);
    std::vector<uint8_t> bgra(size_t(w) * h * 4);
    if (FAILED(source->CopyPixels(nullptr, w * 4, UINT(bgra.size()), bgra.data()))) throw std::runtime_error("Cannot read " + path.string());
    for (size_t i = 3; i < bgra.size(); i += 4) bgra[i] = 255;
    Pixels pixels{int(w) * scale, int(h) * scale, {}};
    if (scale == 1) { pixels.bgra = std::move(bgra); return pixels; }
    pixels.bgra.resize(size_t(pixels.width) * pixels.height * 4);
    for (int y = 0; y < pixels.height; ++y) for (int x = 0; x < pixels.width; ++x)
        std::memcpy(&pixels.bgra[(size_t(y) * pixels.width + x) * 4], &bgra[(size_t(y / scale) * w + x / scale) * 4], 4);
    return pixels;
}

class CorpusSource final : public RecordingFrameSource {
    const std::vector<Frame>& frames_;
    const int scale_; const bool cpu_;
    ComPtr<IWICImagingFactory> wic_;
    ComPtr<ID3D11Device> device_; ComPtr<ID3D11DeviceContext> context_;
    std::mutex mutex_;
    size_t index_ = 0; bool finished_ = false;
    size_t loaded_ = SIZE_MAX; int repeat_ = 0;
    Pixels pixels_; std::shared_ptr<ID3D11Texture2D> texture_;
public:
    CorpusSource(const std::vector<Frame>& frames, int scale, bool cpu) : frames_(frames), scale_(scale), cpu_(cpu) {
        if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&wic_)))) throw std::runtime_error("No WIC");
        if (cpu) return;
        if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, &device_, nullptr, &context_)))
            throw std::runtime_error("No D3D11 device");
        ComPtr<ID3D11Multithread> protection; context_.As(&protection); protection->SetMultithreadProtected(TRUE);
    }
    bool finished() { std::lock_guard lock(mutex_); return finished_; }
    // Called from the snapshot callback with the index its timestamp names.
    void sampled(size_t index) { std::lock_guard lock(mutex_); if (index == index_ && ++index_ >= frames_.size()) finished_ = true; }
    bool acquire(CapturePixels& pixels, std::chrono::milliseconds) override {
        std::this_thread::sleep_for(4ms);
        size_t index; { std::lock_guard lock(mutex_); if (finished_) return false; index = index_; }
        if (loaded_ != index) {
            pixels_ = decode(wic_.Get(), frames_[index].path, scale_); loaded_ = index; repeat_ = 0; texture_.reset();
            if (!cpu_) {
                D3D11_TEXTURE2D_DESC desc{}; desc.Width = UINT(pixels_.width); desc.Height = UINT(pixels_.height); desc.MipLevels = 1; desc.ArraySize = 1;
                desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1; desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
                D3D11_SUBRESOURCE_DATA data{pixels_.bgra.data(), UINT(pixels_.width * 4), 0};
                ID3D11Texture2D* raw = nullptr;
                if (FAILED(device_->CreateTexture2D(&desc, &data, &raw))) throw std::runtime_error("Cannot create corpus texture");
                texture_ = std::shared_ptr<ID3D11Texture2D>(raw, [](auto* p) { p->Release(); });
            }
        }
        pixels.width = pixels_.width; pixels.height = pixels_.height; pixels.stride = pixels_.width * 4;
        pixels.timestamp_us = int64_t(index + 1) * 1000000 + int64_t(std::min(repeat_++, 400)) * 1000;
        if (cpu_) { pixels.bgra = pixels_.bgra; pixels.texture.reset(); }
        else { pixels.texture = texture_; pixels.bgra.clear(); }
        return true;
    }
    bool eligible() const override { return true; }
    const char* name() const override { return "detector corpus"; }
    ID3D11Device* d3d_device() const override { return device_.Get(); }
};

uint64_t fnv(const std::vector<uint8_t>& bytes) {
    uint64_t hash = 1469598103934665603ull;
    for (const auto b : bytes) { hash ^= b; hash *= 1099511628211ull; }
    return hash;
}

const std::map<std::string, std::pair<std::array<CaptureNormalizedRect, 3>, bool>> Layouts = {
    {"helldivers2", {{CaptureNormalizedRect{0.34, 0.445, 0.32, 0.065}, CaptureNormalizedRect{0.42, 0.335, 0.16, 0.055},
        CaptureNormalizedRect{1152.0 / 2560, 1036.0 / 1440, 308.0 / 2560, 174.0 / 1440}}, true}},
    {"overwatch", {{CaptureNormalizedRect{0.015, 0.02, 0.27, 0.80}, CaptureNormalizedRect{0.43, 0.685, 0.28, 0.115},
        CaptureNormalizedRect{0.42, 0.20, 0.18, 0.055}}, false}},
    {"fortnite", {{CaptureNormalizedRect{0.01, 0.49, 0.33, 0.12}, CaptureNormalizedRect{0.42, 0.66, 0.19, 0.16},
        CaptureNormalizedRect{0.30, 0.005, 0.39, 0.30}}, false}},
};

std::vector<std::string> split(const std::string& text) {
    std::vector<std::string> parts; std::stringstream stream(text); std::string part;
    while (std::getline(stream, part, ',')) if (!part.empty()) parts.push_back(part);
    return parts;
}

std::vector<std::string> run(const std::vector<Frame>& frames, const std::string& layout, int width, int height, int scale, bool cpu) {
    RecordingCaptureConfig config; config.width = width; config.height = height; config.fps = 30; config.bitrate_mbps = 10;
    config.detector_enabled = true; config.detector_normalized = Layouts.at(layout).first; config.detector_counter_mask = Layouts.at(layout).second;
    RecordingCaptureDependencies dependencies; dependencies.candidates = {{cpu ? "libx264" : "h264_nvenc", false, !cpu}};
    auto owned = std::make_unique<CorpusSource>(frames, scale, cpu); auto* source = owned.get();
    std::mutex mutex; std::vector<std::string> lines;
    RecordingCaptureCallbacks callbacks;
    callbacks.detector_snapshot = [&](RecordingDetectorSnapshot snapshot) {
        const auto index = size_t(snapshot.timestamp_us / 1000000 - 1);
        if (index >= frames.size()) return;
        const auto& frame = frames[index];
        std::ostringstream line;
        line << frame.name << '\t' << layout << '\t' << width << 'x' << height << '\t' << frame.width * scale << 'x' << frame.height * scale
             << '\t' << (cpu ? "cpu" : "gpu") << '\t' << snapshot.timestamp_us;
        for (const auto& region : snapshot.regions)
            line << '\t' << region.width << 'x' << region.height << ':' << std::hex << fnv(region.pixels) << std::dec;
        size_t ones = 0; for (const auto b : snapshot.third_mask.pixels) ones += b != 0;
        line << '\t' << snapshot.third_mask.width << 'x' << snapshot.third_mask.height << ':' << std::hex << fnv(snapshot.third_mask.pixels) << std::dec << ':' << ones;
        { std::lock_guard lock(mutex); lines.push_back(line.str()); }
        source->sampled(index);
    };
    RecordingCapture capture(config, callbacks, std::move(owned), std::move(dependencies));
    capture.start();
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(30 + frames.size());
    while (!source->finished() && std::chrono::steady_clock::now() < deadline) std::this_thread::sleep_for(20ms);
    capture.stop();
    const auto health = capture.health();
    if (!source->finished()) throw std::runtime_error("Corpus run " + layout + " stalled: " + health.error);
#if __has_include("detector_stage.h")
    // The stage must have produced these, not its full-frame fallback.
    const double full = double(frames.front().width) * scale * frames.front().height * scale * 4;
    std::ostringstream stats;
    stats << "  " << layout << ' ' << width << 'x' << height << ": samples=" << health.detector_samples << " fallbacks=" << health.detector_fallbacks
          << " builds=" << health.detector_builds << " textures=" << health.detector_textures_allocated << " buffers=" << health.detector_buffers_allocated
          << " bytes/sample=" << uint64_t(health.detector_bytes_per_sample) << " (" << 100 * health.detector_bytes_per_sample / full << "% of the first frame)";
    { std::lock_guard lock(mutex); lines.push_back("#" + stats.str()); }
    if (health.detector_fallbacks) throw std::runtime_error("Detector fell back to the full-frame path: " + stats.str());
#endif
    return lines;
}
}

int main(int argc, char** argv) {
    try {
        av_log_set_level(AV_LOG_ERROR); std::cout << std::unitbuf;
        if (argc < 3) { std::cerr << "detector_corpus <frames dir> <out.tsv> [--layouts a,b] [--outputs WxH,...] [--source-scale N] [--cpu]\n"; return 2; }
        std::string layouts = "helldivers2,overwatch,fortnite", outputs = "1920x1080,2560x1440,3840x2160";
        int scale = 1; bool cpu = false;
        for (int i = 3; i < argc; ++i) {
            const std::string option = argv[i];
            if (option == "--cpu") cpu = true;
            else if (i + 1 < argc && option == "--layouts") layouts = argv[++i];
            else if (i + 1 < argc && option == "--outputs") outputs = argv[++i];
            else if (i + 1 < argc && option == "--source-scale") scale = std::stoi(argv[++i]);
        }
        CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        ComPtr<IWICImagingFactory> wic;
        if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&wic)))) throw std::runtime_error("No WIC");
        std::vector<Frame> frames;
        for (const auto& entry : std::filesystem::recursive_directory_iterator(argv[1], std::filesystem::directory_options::follow_directory_symlink)) {
            const auto extension = entry.path().extension().string();
            if (extension != ".png" && extension != ".jpg") continue;
            UINT w = 0, h = 0; open(wic.Get(), entry.path())->GetSize(&w, &h);
            if (w < 1280 || h < 720) continue; // Crops and scans, not captured frames.
            frames.push_back({entry.path().parent_path().filename().string() + "/" + entry.path().filename().string(), entry.path(), int(w), int(h)});
        }
        std::sort(frames.begin(), frames.end(), [](const Frame& a, const Frame& b) { return a.name < b.name; });
        if (frames.empty()) throw std::runtime_error("No corpus frames");
        std::cout << frames.size() << " frames\n";
        std::ofstream out(argv[2]);
        const auto names = split(layouts);
        for (const auto& output : split(outputs)) {
            const auto x = output.find('x');
            const int width = std::stoi(output.substr(0, x)), height = std::stoi(output.substr(x + 1));
            // Layouts in parallel: independent captures, one encoder each.
            std::vector<std::thread> workers; std::vector<std::vector<std::string>> results(names.size()); std::vector<std::string> errors(names.size());
            for (size_t i = 0; i < names.size(); ++i)
                workers.emplace_back([&, i] {
                    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
                    try { results[i] = run(frames, names[i], width, height, scale, cpu); } catch (const std::exception& e) { errors[i] = e.what(); }
                });
            for (auto& worker : workers) worker.join();
            for (size_t i = 0; i < names.size(); ++i) {
                if (!errors[i].empty()) throw std::runtime_error(errors[i]);
                for (const auto& line : results[i]) { if (line[0] == '#') std::cout << line.substr(1) << '\n'; else out << line << '\n'; }
                std::cout << output << ' ' << names[i] << ": " << results[i].size() << " samples\n";
            }
        }
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << "\n"; return 1; }
}
