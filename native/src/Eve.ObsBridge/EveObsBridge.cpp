#define NOMINMAX
#include <windows.h>

#include <algorithm>
#include <cstdint>
#include <filesystem>
#include <mutex>
#include <string>

namespace {

struct ObsVideoInfo {
    const char *graphics_module;
    uint32_t fps_num;
    uint32_t fps_den;
    uint32_t base_width;
    uint32_t base_height;
    uint32_t output_width;
    uint32_t output_height;
    int output_format;
    uint32_t adapter;
    bool gpu_conversion;
    int colorspace;
    int range;
    int scale_type;
};

struct ObsAudioInfo {
    uint32_t samples_per_sec;
    int speakers;
};

struct Calldata {
    uint8_t *stack;
    size_t size;
    size_t capacity;
    bool fixed;
};

using obs_data_t = void;
using obs_source_t = void;
using obs_scene_t = void;
using obs_sceneitem_t = void;
using obs_output_t = void;
using obs_encoder_t = void;
using proc_handler_t = void;

constexpr int VIDEO_FORMAT_NV12 = 7;
constexpr int VIDEO_CS_709 = 2;
constexpr int VIDEO_RANGE_PARTIAL = 1;
constexpr int OBS_SCALE_BICUBIC = 2;
constexpr int SPEAKERS_STEREO = 2;

std::mutex g_lock;
std::wstring g_last_error;
std::wstring g_runtime;
std::wstring g_last_replay;
HMODULE g_obs = nullptr;
bool g_initialized = false;
obs_source_t *g_scene_source = nullptr;
obs_source_t *g_monitor_source = nullptr;
obs_scene_t *g_scene = nullptr;
obs_sceneitem_t *g_scene_item = nullptr;
obs_output_t *g_replay = nullptr;
obs_encoder_t *g_video_encoder = nullptr;
obs_encoder_t *g_audio_encoder = nullptr;
int g_duration_seconds = 60;
int g_max_height = 1080;
int g_frame_rate = 60;

std::string narrow(const std::wstring &value)
{
    if (value.empty()) return {};
    int size = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string result(static_cast<size_t>(size - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, result.data(), size, nullptr, nullptr);
    return result;
}

std::wstring widen(const char *value)
{
    if (!value || !*value) return {};
    int size = MultiByteToWideChar(CP_UTF8, 0, value, -1, nullptr, 0);
    std::wstring result(static_cast<size_t>(size - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value, -1, result.data(), size);
    return result;
}

void set_error(const std::wstring &message)
{
    g_last_error = message;
}

template <typename T>
bool load_fn(T &target, const char *name)
{
    target = reinterpret_cast<T>(GetProcAddress(g_obs, name));
    if (!target) {
        set_error(L"OBS function missing: " + widen(name));
        return false;
    }
    return true;
}

struct ObsApi {
    bool(__cdecl *startup)(const char *, const char *, void *) = nullptr;
    void(__cdecl *shutdown)() = nullptr;
    void(__cdecl *add_module_path)(const char *, const char *) = nullptr;
    void(__cdecl *load_all_modules)() = nullptr;
    int(__cdecl *reset_video)(ObsVideoInfo *) = nullptr;
    bool(__cdecl *reset_audio)(ObsAudioInfo *) = nullptr;
    obs_data_t *(__cdecl *data_create)() = nullptr;
    void(__cdecl *data_release)(obs_data_t *) = nullptr;
    void(__cdecl *data_set_int)(obs_data_t *, const char *, long long) = nullptr;
    void(__cdecl *data_set_bool)(obs_data_t *, const char *, bool) = nullptr;
    void(__cdecl *data_set_string)(obs_data_t *, const char *, const char *) = nullptr;
    obs_scene_t *(__cdecl *scene_create)(const char *) = nullptr;
    obs_source_t *(__cdecl *scene_get_source)(obs_scene_t *) = nullptr;
    obs_sceneitem_t *(__cdecl *scene_add)(obs_scene_t *, obs_source_t *) = nullptr;
    obs_source_t *(__cdecl *source_create)(const char *, const char *, obs_data_t *, obs_data_t *) = nullptr;
    void(__cdecl *source_release)(obs_source_t *) = nullptr;
    void(__cdecl *set_output_source)(uint32_t, obs_source_t *) = nullptr;
    void *(__cdecl *get_video)() = nullptr;
    void *(__cdecl *get_audio)() = nullptr;
    obs_encoder_t *(__cdecl *video_encoder_create)(const char *, const char *, obs_data_t *, obs_data_t *) = nullptr;
    obs_encoder_t *(__cdecl *audio_encoder_create)(const char *, const char *, obs_data_t *, size_t, obs_data_t *) = nullptr;
    void(__cdecl *encoder_set_video)(obs_encoder_t *, void *) = nullptr;
    void(__cdecl *encoder_set_audio)(obs_encoder_t *, void *) = nullptr;
    void(__cdecl *encoder_release)(obs_encoder_t *) = nullptr;
    obs_output_t *(__cdecl *output_create)(const char *, const char *, obs_data_t *, obs_data_t *) = nullptr;
    void(__cdecl *output_set_video_encoder)(obs_output_t *, obs_encoder_t *) = nullptr;
    void(__cdecl *output_set_audio_encoder)(obs_output_t *, obs_encoder_t *, size_t) = nullptr;
    bool(__cdecl *output_start)(obs_output_t *) = nullptr;
    void(__cdecl *output_stop)(obs_output_t *) = nullptr;
    bool(__cdecl *output_active)(const obs_output_t *) = nullptr;
    void(__cdecl *output_update)(obs_output_t *, obs_data_t *) = nullptr;
    void(__cdecl *output_release)(obs_output_t *) = nullptr;
    proc_handler_t *(__cdecl *output_get_proc_handler)(const obs_output_t *) = nullptr;
    bool(__cdecl *proc_handler_call)(proc_handler_t *, const char *, Calldata *) = nullptr;
    bool(__cdecl *calldata_get_string)(const Calldata *, const char *, const char **) = nullptr;
    void(__cdecl *bfree)(void *) = nullptr;
} obs;

bool load_obs_api(const std::filesystem::path &bin)
{
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_USER_DIRS);
    AddDllDirectory(bin.c_str());
    g_obs = LoadLibraryW((bin / L"obs.dll").c_str());
    if (!g_obs) {
        set_error(L"Could not load obs.dll from " + bin.wstring());
        return false;
    }

    return load_fn(obs.startup, "obs_startup") &&
           load_fn(obs.shutdown, "obs_shutdown") &&
           load_fn(obs.add_module_path, "obs_add_module_path") &&
           load_fn(obs.load_all_modules, "obs_load_all_modules") &&
           load_fn(obs.reset_video, "obs_reset_video") &&
           load_fn(obs.reset_audio, "obs_reset_audio") &&
           load_fn(obs.data_create, "obs_data_create") &&
           load_fn(obs.data_release, "obs_data_release") &&
           load_fn(obs.data_set_int, "obs_data_set_int") &&
           load_fn(obs.data_set_bool, "obs_data_set_bool") &&
           load_fn(obs.data_set_string, "obs_data_set_string") &&
           load_fn(obs.scene_create, "obs_scene_create") &&
           load_fn(obs.scene_get_source, "obs_scene_get_source") &&
           load_fn(obs.scene_add, "obs_scene_add") &&
           load_fn(obs.source_create, "obs_source_create") &&
           load_fn(obs.source_release, "obs_source_release") &&
           load_fn(obs.set_output_source, "obs_set_output_source") &&
           load_fn(obs.get_video, "obs_get_video") &&
           load_fn(obs.get_audio, "obs_get_audio") &&
           load_fn(obs.video_encoder_create, "obs_video_encoder_create") &&
           load_fn(obs.audio_encoder_create, "obs_audio_encoder_create") &&
           load_fn(obs.encoder_set_video, "obs_encoder_set_video") &&
           load_fn(obs.encoder_set_audio, "obs_encoder_set_audio") &&
           load_fn(obs.encoder_release, "obs_encoder_release") &&
           load_fn(obs.output_create, "obs_output_create") &&
           load_fn(obs.output_set_video_encoder, "obs_output_set_video_encoder") &&
           load_fn(obs.output_set_audio_encoder, "obs_output_set_audio_encoder") &&
           load_fn(obs.output_start, "obs_output_start") &&
           load_fn(obs.output_stop, "obs_output_stop") &&
           load_fn(obs.output_active, "obs_output_active") &&
           load_fn(obs.output_update, "obs_output_update") &&
           load_fn(obs.output_release, "obs_output_release") &&
           load_fn(obs.output_get_proc_handler, "obs_output_get_proc_handler") &&
           load_fn(obs.proc_handler_call, "proc_handler_call") &&
           load_fn(obs.calldata_get_string, "calldata_get_string") &&
           load_fn(obs.bfree, "bfree");
}

std::pair<int, int> primary_monitor_size()
{
    return {GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN)};
}

std::pair<int, int> output_size()
{
    auto [width, height] = primary_monitor_size();
    if (height <= g_max_height) return {width, height};
    int scaled_width = std::max(2, (width * g_max_height / height) & ~1);
    int scaled_height = std::max(2, g_max_height & ~1);
    return {scaled_width, scaled_height};
}

bool create_scene()
{
    obs_data_t *settings = obs.data_create();
    obs.data_set_int(settings, "monitor", 0);
    obs.data_set_bool(settings, "capture_cursor", true);
    obs.data_set_bool(settings, "compatibility", false);
    g_monitor_source = obs.source_create("monitor_capture", "Primary Monitor", settings, nullptr);
    obs.data_release(settings);
    if (!g_monitor_source) {
        set_error(L"OBS monitor_capture source failed.");
        return false;
    }

    g_scene = obs.scene_create("EVE Replay Scene");
    if (!g_scene) {
        set_error(L"OBS scene create failed.");
        return false;
    }

    g_scene_item = obs.scene_add(g_scene, g_monitor_source);
    if (!g_scene_item) {
        set_error(L"OBS scene add failed.");
        return false;
    }

    g_scene_source = obs.scene_get_source(g_scene);
    obs.set_output_source(0, g_scene_source);
    return true;
}

bool create_replay_output()
{
    obs_data_t *v = obs.data_create();
    obs.data_set_int(v, "bitrate", g_max_height >= 1440 ? 32000 : 20000);
    obs.data_set_string(v, "preset", "p4");
    obs.data_set_string(v, "profile", "high");
    g_video_encoder = obs.video_encoder_create("ffmpeg_nvenc", "EVE NVENC", v, nullptr);
    if (!g_video_encoder) {
        obs.data_set_string(v, "rate_control", "CRF");
        obs.data_set_int(v, "crf", 18);
        g_video_encoder = obs.video_encoder_create("obs_x264", "EVE x264", v, nullptr);
    }
    obs.data_release(v);
    if (!g_video_encoder) {
        set_error(L"OBS video encoder create failed.");
        return false;
    }
    obs.encoder_set_video(g_video_encoder, obs.get_video());

    obs_data_t *a = obs.data_create();
    obs.data_set_int(a, "bitrate", 192);
    g_audio_encoder = obs.audio_encoder_create("ffmpeg_aac", "EVE AAC", a, 0, nullptr);
    obs.data_release(a);
    if (g_audio_encoder) obs.encoder_set_audio(g_audio_encoder, obs.get_audio());

    obs_data_t *o = obs.data_create();
    obs.data_set_int(o, "max_time_sec", g_duration_seconds);
    obs.data_set_string(o, "format_name", "mp4");
    obs.data_set_string(o, "directory", narrow(std::filesystem::temp_directory_path().wstring()).c_str());
    obs.data_set_bool(o, "allow_spaces", true);
    g_replay = obs.output_create("replay_buffer", "EVE Replay Buffer", o, nullptr);
    obs.data_release(o);
    if (!g_replay) {
        set_error(L"OBS replay_buffer output create failed.");
        return false;
    }

    obs.output_set_video_encoder(g_replay, g_video_encoder);
    if (g_audio_encoder) obs.output_set_audio_encoder(g_replay, g_audio_encoder, 0);
    return true;
}

bool copy_string(wchar_t *destination, int length, const std::wstring &value)
{
    if (!destination || length <= 0) return false;
    wcsncpy_s(destination, static_cast<size_t>(length), value.c_str(), _TRUNCATE);
    return true;
}

} // namespace

extern "C" __declspec(dllexport) int eve_obs_init(const wchar_t *runtime_folder, int max_height, int frame_rate, int duration_seconds)
{
    std::lock_guard lock(g_lock);
    if (g_initialized) return 0;
    g_last_error.clear();
    g_runtime = runtime_folder ? runtime_folder : L"";
    g_max_height = std::clamp(max_height, 480, 2160);
    g_frame_rate = std::clamp(frame_rate, 15, 60);
    g_duration_seconds = std::clamp(duration_seconds, 5, 1200);

    const std::filesystem::path root(g_runtime);
    const auto bin = root / L"bin" / L"64bit";
    if (!std::filesystem::exists(bin / L"obs.dll")) {
        set_error(L"OBS runtime missing obs.dll: " + (bin / L"obs.dll").wstring());
        return -1;
    }

    if (!load_obs_api(bin)) return -2;

    const auto plugins = root / L"obs-plugins" / L"64bit";
    const auto data = root / L"data" / L"obs-plugins" / L"%module%";
    obs.add_module_path(narrow(plugins / L"%module%.dll").c_str(), narrow(data).c_str());

    if (!obs.startup("en-US", nullptr, nullptr)) {
        set_error(L"obs_startup failed.");
        return -3;
    }

    obs.load_all_modules();

    auto [base_width, base_height] = primary_monitor_size();
    auto [out_width, out_height] = output_size();
    ObsVideoInfo video = {};
    video.graphics_module = "libobs-d3d11";
    video.fps_num = static_cast<uint32_t>(g_frame_rate);
    video.fps_den = 1;
    video.base_width = static_cast<uint32_t>(base_width);
    video.base_height = static_cast<uint32_t>(base_height);
    video.output_width = static_cast<uint32_t>(out_width);
    video.output_height = static_cast<uint32_t>(out_height);
    video.output_format = VIDEO_FORMAT_NV12;
    video.gpu_conversion = true;
    video.colorspace = VIDEO_CS_709;
    video.range = VIDEO_RANGE_PARTIAL;
    video.scale_type = OBS_SCALE_BICUBIC;
    if (obs.reset_video(&video) != 0) {
        set_error(L"obs_reset_video failed.");
        return -4;
    }

    ObsAudioInfo audio = {};
    audio.samples_per_sec = 48000;
    audio.speakers = SPEAKERS_STEREO;
    if (!obs.reset_audio(&audio)) {
        set_error(L"obs_reset_audio failed.");
        return -5;
    }

    if (!create_scene()) return -6;
    if (!create_replay_output()) return -7;
    g_initialized = true;
    return 0;
}

extern "C" __declspec(dllexport) int eve_obs_start_primary_monitor()
{
    std::lock_guard lock(g_lock);
    if (!g_initialized || !g_replay) {
        set_error(L"OBS bridge not initialized.");
        return -1;
    }
    if (obs.output_active(g_replay)) return 0;
    if (!obs.output_start(g_replay)) {
        set_error(L"obs_output_start replay_buffer failed.");
        return -2;
    }
    return 0;
}

extern "C" __declspec(dllexport) int eve_obs_save_replay(const wchar_t *output_folder, wchar_t *output_path, int output_path_length)
{
    std::lock_guard lock(g_lock);
    if (!g_initialized || !g_replay || !obs.output_active(g_replay)) {
        set_error(L"OBS replay buffer is not running.");
        return -1;
    }

    std::filesystem::path folder = output_folder ? output_folder : L"";
    std::filesystem::create_directories(folder);
    obs_data_t *settings = obs.data_create();
    obs.data_set_string(settings, "directory", narrow(folder.wstring()).c_str());
    obs.output_update(g_replay, settings);
    obs.data_release(settings);

    Calldata params = {};
    proc_handler_t *handler = obs.output_get_proc_handler(g_replay);
    if (!handler || !obs.proc_handler_call(handler, "save", &params)) {
        if (!params.fixed && params.stack) obs.bfree(params.stack);
        set_error(L"OBS replay save proc failed.");
        return -2;
    }

    const char *last_path = nullptr;
    std::wstring saved_path;
    if (obs.calldata_get_string(&params, "path", &last_path) && last_path && *last_path) {
        saved_path = widen(last_path);
    }
    if (!params.fixed && params.stack) obs.bfree(params.stack);

    if (saved_path.empty()) {
        set_error(L"OBS replay save returned no path.");
        return -3;
    }

    g_last_replay = saved_path;
    copy_string(output_path, output_path_length, g_last_replay);
    return 0;
}

extern "C" __declspec(dllexport) int eve_obs_stop()
{
    std::lock_guard lock(g_lock);
    if (!g_initialized || !g_replay) return 0;
    if (obs.output_active(g_replay)) obs.output_stop(g_replay);
    return 0;
}

extern "C" __declspec(dllexport) void eve_obs_shutdown()
{
    std::lock_guard lock(g_lock);
    if (!g_initialized) return;
    if (g_replay) {
        if (obs.output_active(g_replay)) obs.output_stop(g_replay);
        obs.output_release(g_replay);
        g_replay = nullptr;
    }
    if (g_video_encoder) {
        obs.encoder_release(g_video_encoder);
        g_video_encoder = nullptr;
    }
    if (g_audio_encoder) {
        obs.encoder_release(g_audio_encoder);
        g_audio_encoder = nullptr;
    }
    if (g_monitor_source) {
        obs.source_release(g_monitor_source);
        g_monitor_source = nullptr;
    }
    if (g_scene_source) {
        obs.source_release(g_scene_source);
        g_scene_source = nullptr;
    }
    obs.shutdown();
    g_initialized = false;
}

extern "C" __declspec(dllexport) void eve_obs_last_error(wchar_t *message, int message_length)
{
    std::lock_guard lock(g_lock);
    copy_string(message, message_length, g_last_error.empty() ? L"OBS bridge failed." : g_last_error);
}
