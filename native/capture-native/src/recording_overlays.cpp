#include "recording_overlays.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>
#include <locale>
#include <sstream>
#include <stdexcept>
extern "C" {
#include <libavutil/frame.h>
#include <libavutil/pixfmt.h>
}

namespace clypdat {
namespace {
void key_json(std::ostream& out, const PhysicalKey& key) {
    constexpr const char* buttons[] = {"", "MouseLeft", "MouseRight", "MouseMiddle", "MouseBack", "MouseForward"};
    out << "{\"ScanCode\":" << key.scan_code << ",\"E0\":" << (key.e0 ? "true" : "false")
        << ",\"E1\":" << (key.e1 ? "true" : "false") << ",\"MouseButton\":";
    if (key.mouse_button > 0 && key.mouse_button < std::size(buttons)) out << '"' << buttons[key.mouse_button] << '"';
    else out << "null";
    out << '}';
}
template<class T> void key_list(std::ostream& out, const T& keys) {
    out << '['; bool first = true;
    for (const auto& key : keys) { if (!first) out << ','; first = false; key_json(out, key); }
    out << ']';
}
}
InputHistory::InputHistory(size_t maximum_edges) : maximum_edges_(maximum_edges) {
    if (!maximum_edges) throw std::invalid_argument("Input history must have a positive edge bound");
}
void InputHistory::reset(bool available) {
    std::lock_guard lock(mutex_);
    available_ = available; overflowed_ = false; edges_.clear(); checkpoints_.clear(); down_.clear(); ++revision_;
    if(changed_) changed_();
}
void InputHistory::set_available(bool value) { std::lock_guard lock(mutex_); if(available_!=value) { available_=value; ++revision_; if(changed_) changed_(); } }
void InputHistory::on_change(std::function<void()> callback) { std::lock_guard lock(mutex_); changed_=std::move(callback); }
bool InputHistory::add(int64_t at_us, PhysicalKey key, bool down) {
    std::lock_guard lock(mutex_);
    if (!available_ || overflowed_ || (key.scan_code == 0 && key.mouse_button == 0) || key.mouse_button > 5) return false;
    const bool changed = down ? down_.insert(key).second : down_.erase(key) != 0;
    if (!changed) return false;
    if (edges_.size() >= maximum_edges_) {
        overflowed_ = true; edges_.clear(); checkpoints_.clear(); down_.clear(); ++revision_; if(changed_) changed_(); return false;
    }
    if (!edges_.empty()) at_us = std::max(at_us, edges_.back().at_us);
    edges_.push_back({at_us, key, down}); ++revision_;
    if (checkpoints_.empty() || at_us - checkpoints_.back().at_us >= 2000000)
        checkpoints_.push_back({at_us, {down_.begin(), down_.end()}});
    if(changed_) changed_();
    return true;
}
InputSnapshot InputHistory::snapshot(int64_t start_us, int64_t end_us) const {
    if (end_us < start_us) throw std::invalid_argument("Invalid input snapshot interval");
    std::lock_guard lock(mutex_);
    InputSnapshot result{start_us, end_us, !available_, overflowed_, {}, {}};
    std::set<PhysicalKey> initial;
    for (const auto& edge : edges_) {
        if (edge.at_us <= start_us) {
            if (edge.down) initial.insert(edge.key); else initial.erase(edge.key);
        }
        if (edge.at_us > start_us && edge.at_us <= end_us) result.edges.push_back(edge);
    }
    result.checkpoints.push_back({start_us, {initial.begin(), initial.end()}});
    for (const auto& checkpoint : checkpoints_)
        if (checkpoint.at_us > start_us && checkpoint.at_us <= end_us) result.checkpoints.push_back(checkpoint);
    return result;
}
std::vector<PhysicalKey> InputHistory::pressed() const { std::lock_guard lock(mutex_); return {down_.begin(), down_.end()}; }
uint64_t InputHistory::revision() const { std::lock_guard lock(mutex_); return revision_; }
std::string InputSnapshot::json(double media_scale) const {
    if (!std::isfinite(media_scale) || media_scale <= 0) media_scale = 1;
    auto seconds = [&](int64_t at) { return static_cast<double>(std::max<int64_t>(0, at - start_us)) / 1000000 * media_scale; };
    std::ostringstream out; out.imbue(std::locale::classic()); out.precision(17);
    out << "{\"Version\":2,\"MissingHistory\":";
    if (unavailable) out << "\"Keyboard input was not recorded.\"";
    else if (overflowed) out << "\"Input history overflowed or capture reset.\"";
    else out << "null";
    out << ",\"Transitions\":["; bool first = true;
    for (const auto& edge : edges) {
        if (!first) out << ','; first = false;
        out << "{\"Seconds\":" << seconds(edge.at_us) << ",\"Key\":"; key_json(out, edge.key);
        out << ",\"Down\":" << (edge.down ? "true" : "false") << ",\"Kind\":\"" << (edge.key.mouse_button ? "Mouse" : "Key") << "\"}";
    }
    out << "],\"Checkpoints\":["; first = true;
    for (const auto& checkpoint : checkpoints) {
        if (!first) out << ','; first = false;
        out << "{\"Seconds\":" << seconds(checkpoint.at_us) << ",\"Down\":"; key_list(out, checkpoint.down); out << '}';
    }
    out << "]}"; return out.str();
}
std::shared_ptr<const OverlayBitmap> OverlayBitmap::copy(int width, int height, int stride,
    const uint8_t* pixels, size_t bytes, uint64_t revision, int64_t at_us, bool premultiplied) {
    if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || stride < width * 4 || !pixels ||
        static_cast<uint64_t>(stride) * static_cast<uint64_t>(height) > bytes)
        throw std::invalid_argument("Invalid overlay BGRA buffer");
    auto result = std::make_shared<OverlayBitmap>();
    result->width = width; result->height = height; result->stride = width * 4; result->revision = revision;
    result->at_us = at_us; result->premultiplied = premultiplied;
    result->bgra.resize(static_cast<size_t>(width) * height * 4);
    for (int row = 0; row < height; ++row)
        std::copy_n(pixels + static_cast<size_t>(row) * stride, result->stride, result->bgra.data() + static_cast<size_t>(row) * result->stride);
    return result;
}
OverlayHistory::OverlayHistory(std::shared_ptr<InputHistory> input) : input_(std::move(input)) {
    if (!input_) throw std::invalid_argument("Overlay input history is required");
}
CameraFileLease::~CameraFileLease() {
    if(remove_on_release && path.is_absolute()) { std::error_code ignored; std::filesystem::remove(path,ignored); }
}
void OverlayHistory::retention(int64_t duration_us) {
    if(duration_us<=0) throw std::invalid_argument("Overlay retention must be positive");
    std::lock_guard lock(mutex_); retention_us_=duration_us;
}
void OverlayHistory::reset(bool burned) {
    std::lock_guard lock(mutex_); burned_ = burned; ++camera_generation_; settings_.clear(); artwork_.clear();
    camera_frame_.reset(); camera_segments_.clear(); camera_stopped_ = false; camera_failure_.clear();
}
bool OverlayHistory::apply(OverlaySettingsNative settings) {
    auto valid = [](const OverlayTransformNative& t) { return std::isfinite(t.x) && std::isfinite(t.y) && std::isfinite(t.width) && t.width > 0; };
    if (!valid(settings.camera_transform) || !valid(settings.keyboard_transform)) return false;
    std::vector<CameraSegmentNative> retired;
    std::lock_guard lock(mutex_);
    if (!settings_.empty() && settings.revision <= settings_.back().revision) return false;
    settings.burned = burned_; // Recording mode belongs to the session, not live settings.
    if (!settings_.empty()) settings.at_us = std::max(settings.at_us, settings_.back().at_us);
    const auto cutoff=settings.at_us-retention_us_;
    settings_.push_back(std::move(settings));
    auto first=std::lower_bound(settings_.begin(),settings_.end(),cutoff,[](const auto& value,int64_t at) { return value.at_us<at; });
    if(first!=settings_.begin()) settings_.erase(settings_.begin(),std::prev(first));
    for(auto item=camera_segments_.begin();item!=camera_segments_.end();)
        if(item->completed && item->end_us<cutoff) { retired.push_back(std::move(*item)); item=camera_segments_.erase(item); } else ++item;
    return true;
}
bool OverlayHistory::set_artwork(std::shared_ptr<const OverlayBitmap> bitmap) {
    if (!bitmap) return false;
    std::lock_guard lock(mutex_);
    if (!artwork_.empty() && bitmap->revision <= artwork_.back()->revision) return false;
    // Burned frames retain pixels in encoded history; accepted saves retain this
    // shared immutable revision. Editable playback rasterizes the input index.
    artwork_.clear(); artwork_.push_back(std::move(bitmap)); return true;
}
// A new generation never shows the previous camera's frame.
uint64_t OverlayHistory::replace_camera(bool reconnect) {
    std::lock_guard lock(mutex_); camera_frame_.reset(); camera_stopped_ = false;
    if (!reconnect) camera_failure_.clear();
    return ++camera_generation_;
}
bool OverlayHistory::camera_frame(uint64_t generation, std::shared_ptr<const OverlayBitmap> frame) {
    if (!frame) return false;
    std::lock_guard lock(mutex_);
    if (generation != camera_generation_) return false;
    camera_frame_ = std::move(frame); camera_failure_.clear(); return true;
}
void OverlayHistory::camera_failed(std::string failure) {
    std::lock_guard lock(mutex_); camera_frame_.reset(); camera_stopped_ = true; camera_failure_ = std::move(failure);
}
void OverlayHistory::camera_stopped(uint64_t generation, std::string failure) {
    std::lock_guard lock(mutex_);
    if (generation != camera_generation_) return;
    camera_stopped_ = true; camera_failure_ = std::move(failure);
}
OverlayFrame OverlayHistory::frame(int64_t now_us) const {
    std::lock_guard lock(mutex_);
    OverlayFrame result;
    result.burned = burned_; result.camera_generation = camera_generation_;
    if (!burned_ || settings_.empty()) return result;
    const auto& settings = settings_.back();
    result.settings_revision = settings.revision;
    result.camera = {!settings.camera_moniker.empty(), camera_stopped_ ? nullptr : camera_frame_, settings.camera_transform};
    result.keyboard = {_stricmp(settings.keyboard_layout.c_str(), "None") != 0, artwork_.empty() ? nullptr : artwork_.back(), settings.keyboard_transform};
    result.keyboard_revision = result.keyboard.bitmap ? result.keyboard.bitmap->revision : 0;
    if (result.camera.requested) {
        result.camera_failure = camera_failure_; result.camera_stopped = camera_stopped_;
        result.camera_stale = result.camera.bitmap && now_us - result.camera.bitmap->at_us > kCameraStaleUs;
    }
    return result;
}
void OverlayHistory::camera_segment(CameraSegmentNative segment) {
    std::vector<CameraSegmentNative> retired;
    std::lock_guard lock(mutex_);
    if (segment.generation != camera_generation_) return;
    for (auto& previous : camera_segments_) if (previous.generation == segment.generation && !previous.completed) {
        previous.end_us = segment.start_us; previous.completed = previous.end_us > previous.start_us;
    }
    if(!segment.lease) { segment.lease=std::make_shared<CameraFileLease>(); segment.lease->path=segment.path; }
    const auto cutoff=segment.start_us-retention_us_;
    camera_segments_.push_back(std::move(segment));
    for(auto item=camera_segments_.begin();item!=camera_segments_.end();)
        if(item->completed && item->end_us<cutoff) { retired.push_back(std::move(*item)); item=camera_segments_.erase(item); } else ++item;
}
void OverlayHistory::complete_camera(uint64_t generation, int64_t end_us) {
    std::lock_guard lock(mutex_);
    for (auto& segment : camera_segments_) if (segment.generation == generation && !segment.completed) {
        segment.end_us = std::max(segment.start_us, end_us); segment.completed = segment.end_us > segment.start_us;
    }
}
std::shared_ptr<const OverlayBitmap> OverlayHistory::preview() const { std::lock_guard lock(mutex_); return camera_frame_; }
OverlaySnapshot OverlayHistory::snapshot(int64_t start_us, int64_t end_us) const {
    if (end_us < start_us) throw std::invalid_argument("Invalid overlay snapshot interval");
    std::lock_guard lock(mutex_);
    OverlaySnapshot result; result.start_us = start_us; result.end_us = end_us;
    result.input = input_->snapshot(start_us, end_us);
    const OverlaySettingsNative* initial = nullptr;
    for (const auto& setting : settings_) {
        if (setting.at_us <= start_us) initial = &setting;
        else if (setting.at_us <= end_us) result.settings.push_back(setting);
    }
    if (initial) result.settings.insert(result.settings.begin(), *initial);
    result.artwork = artwork_;
    for (const auto& segment : camera_segments_) {
        const auto segment_end=segment.completed?segment.end_us:end_us;
        if(segment_end<=start_us || segment.start_us>=end_us) continue;
        auto pinned=segment; pinned.end_us=segment_end;
        std::error_code error; pinned.pinned_bytes=std::filesystem::file_size(segment.path,error);
        if(error) pinned.pinned_bytes=0;
        result.camera_segments.push_back(std::move(pinned));
    }
    std::sort(result.camera_segments.begin(), result.camera_segments.end(), [](const auto& a, const auto& b) { return a.start_us < b.start_us; });
    return result;
}
namespace {
using Bounds = OverlayPlacement;
Bounds bounds(const OverlayBitmap& bitmap, const OverlayTransformNative& transform, int width, int height) {
    return overlay_placement(bitmap, transform, width, height);
}
std::array<int,4> sample(const OverlayBitmap& bitmap, const Bounds& b, int x, int y) {
    if (x < b.x || y < b.y || x >= b.x+b.width || y >= b.y+b.height) return {};
    auto* p = bitmap.bgra.data() + static_cast<size_t>((y-b.y)*bitmap.height/b.height)*bitmap.stride + ((x-b.x)*bitmap.width/b.width)*4;
    const int a = p[3];
    auto channel = [&](int i) { return bitmap.premultiplied && a ? std::min(255, (p[i]*255+a/2)/a) : p[i]; };
    return {channel(0), channel(1), channel(2), a};
}
void blend_nv12(AVFrame& target, const OverlayBitmap& bitmap, const OverlayTransformNative& transform) {
    const auto b = bounds(bitmap, transform, target.width, target.height);
    for (int y = b.y; y < b.y+b.height; ++y) for (int x = b.x; x < b.x+b.width; ++x) {
        const auto p = sample(bitmap,b,x,y); const int a = p[3]; if (!a) continue;
        const int luma = std::clamp(16 + ((47*p[2]+157*p[1]+16*p[0]+128)>>8),16,235);
        auto& destination = target.data[0][static_cast<ptrdiff_t>(y)*target.linesize[0]+x];
        destination = static_cast<uint8_t>((luma*a+destination*(255-a)+127)/255);
    }
    // A chroma sample represents four luma pixels. Blend once with their mean
    // premultiplied chroma; repeated per-pixel blending over-saturates edges.
    for (int y = b.y&~1; y < b.y+b.height; y += 2) for (int x = b.x&~1; x < b.x+b.width; x += 2) {
        int weighted_u=0, weighted_v=0, alpha=0;
        for (int dy=0;dy<2;++dy) for (int dx=0;dx<2;++dx) {
            const auto p=sample(bitmap,b,x+dx,y+dy); const int a=p[3]; alpha+=a;
            weighted_u += std::clamp(((-26*p[2]-87*p[1]+112*p[0]+128)>>8)+128,16,240)*a;
            weighted_v += std::clamp(((112*p[2]-102*p[1]-10*p[0]+128)>>8)+128,16,240)*a;
        }
        auto* destination=target.data[1]+static_cast<ptrdiff_t>(y/2)*target.linesize[1]+x;
        destination[0]=static_cast<uint8_t>((weighted_u+destination[0]*(1020-alpha)+510)/1020);
        destination[1]=static_cast<uint8_t>((weighted_v+destination[1]*(1020-alpha)+510)/1020);
    }
}
}
OverlayPlacement overlay_placement(const OverlayBitmap& bitmap, const OverlayTransformNative& transform, int width, int height) {
    int w = static_cast<int>(std::clamp(std::round(transform.width * width), 1., static_cast<double>(width)));
    int h = static_cast<int>(std::clamp(std::round(static_cast<double>(w) * bitmap.height / bitmap.width), 1., static_cast<double>(height)));
    return {static_cast<int>(std::clamp(std::round(transform.x * width), 0., static_cast<double>(width-w))),
        static_cast<int>(std::clamp(std::round(transform.y * height), 0., static_cast<double>(height-h))),w,h};
}
OverlayCompositionResult compose_overlay_nv12(AVFrame& frame, const OverlayFrame& layers) {
    if (!layers.drawable()) return {};
    if (frame.format != AV_PIX_FMT_NV12 || frame.width <= 0 || frame.height <= 0 || (frame.width&1) || (frame.height&1))
        throw std::invalid_argument("Overlay composition requires an even NV12 frame");
    if (av_frame_make_writable(&frame) < 0) throw std::runtime_error("Overlay frame is not writable");
    OverlayCompositionResult result;
    if (layers.camera.requested && layers.camera.bitmap) { blend_nv12(frame,*layers.camera.bitmap,layers.camera.transform); result.camera=true; }
    if (layers.keyboard.requested && layers.keyboard.bitmap) { blend_nv12(frame,*layers.keyboard.bitmap,layers.keyboard.transform); result.keyboard=true; }
    return result;
}
OverlayCompositionResult OverlayHistory::compose(AVFrame& frame) const {
    return compose_overlay_nv12(frame, this->frame(frame.pts));
}
OverlayCompositionResult OverlayHistory::compose_bgra(uint8_t* pixels, size_t bytes, int width, int height, int stride) const {
    std::shared_ptr<const OverlayBitmap> camera, keyboard;
    OverlayTransformNative camera_transform,keyboard_transform; bool camera_enabled=false,keyboard_enabled=false;
    { std::lock_guard lock(mutex_); if (!burned_ || settings_.empty()) return {};
      const auto& settings=settings_.back(); camera_transform=settings.camera_transform; keyboard_transform=settings.keyboard_transform;
      camera_enabled=!settings.camera_moniker.empty(); keyboard_enabled=_stricmp(settings.keyboard_layout.c_str(),"None")!=0;
      camera=camera_frame_; if (!artwork_.empty()) keyboard=artwork_.back(); }
    if (!pixels || width<=0 || height<=0 || width>16384 || height>16384 || stride<width*4 || static_cast<uint64_t>(stride)*height>bytes)
        throw std::invalid_argument("Invalid overlay composition BGRA target");
    auto blend=[&](const OverlayBitmap& bitmap,const OverlayTransformNative& transform) {
        const auto b=bounds(bitmap,transform,width,height);
        for(int y=b.y;y<b.y+b.height;++y) for(int x=b.x;x<b.x+b.width;++x) {
            const auto p=sample(bitmap,b,x,y); const int a=p[3]; if(!a) continue;
            auto* output=pixels+static_cast<size_t>(y)*stride+x*4;
            for(int channel=0;channel<3;++channel) output[channel]=static_cast<uint8_t>((p[channel]*a+output[channel]*(255-a)+127)/255);
            output[3]=255;
        }
    };
    OverlayCompositionResult result;
    if (camera_enabled && camera) { blend(*camera,camera_transform); result.camera=true; }
    if (keyboard_enabled && keyboard) { blend(*keyboard,keyboard_transform); result.keyboard=true; }
    return result;
}
}
