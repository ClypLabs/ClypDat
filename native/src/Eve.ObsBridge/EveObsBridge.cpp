#define NOMINMAX
#include <windows.h>

#include <algorithm>
#include <cstdarg>
#include <cstdint>
#include <filesystem>
#include <fstream>
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
using obs_module_t = void;
using proc_handler_t = void;

constexpr int VIDEO_FORMAT_NV12 = 2;
constexpr int VIDEO_CS_709 = 2;
constexpr int VIDEO_RANGE_PARTIAL = 1;
constexpr int OBS_SCALE_BICUBIC = 2;
constexpr int SPEAKERS_STEREO = 2;

std::mutex g_lock;
std::wstring g_last_error;
std::wstring g_runtime;
std::wstring g_last_replay;
std::string g_bin_path;
std::string g_data_path;
std::string g_plugin_binary_path;
std::string g_plugin_data_path;
std::string g_config_path;
HMODULE g_obs = nullptr;
bool g_initialized = false;
obs_source_t *g_scene_source = nullptr;
obs_source_t *g_capture_source = nullptr;
obs_scene_t *g_scene = nullptr;
obs_sceneitem_t *g_scene_item = nullptr;
obs_source_t *g_fallback_source = nullptr;
obs_output_t *g_replay = nullptr;
obs_encoder_t *g_video_encoder = nullptr;
obs_encoder_t *g_audio_encoder = nullptr;
int g_duration_seconds = 60;
int g_max_height = 1080;
int g_frame_rate = 60;

std::filesystem::path app_data_folder();

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

void trace(const std::string &message)
{
    char temp[MAX_PATH] = {};
    if (!GetTempPathA(MAX_PATH, temp)) return;
    std::string path = std::string(temp) + "eve-obs-bridge-trace.log";
    HANDLE file = CreateFileA(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    DWORD written = 0;
    std::string line = message + "\r\n";
    WriteFile(file, line.data(), static_cast<DWORD>(line.size()), &written, nullptr);
    CloseHandle(file);
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
    void(__cdecl *set_log_handler)(void(__cdecl *)(int, const char *, va_list, void *), void *) = nullptr;
    void(__cdecl *add_data_path)(const char *) = nullptr;
    void(__cdecl *add_module_path)(const char *, const char *) = nullptr;
    void(__cdecl *load_all_modules)() = nullptr;
    int(__cdecl *open_module)(obs_module_t **, const char *, const char *) = nullptr;
    bool(__cdecl *init_module)(obs_module_t *) = nullptr;
    void(__cdecl *post_load_modules)() = nullptr;
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
    void(__cdecl *sceneitem_set_order)(obs_sceneitem_t *, int) = nullptr;
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
    const char *(__cdecl *output_get_last_error)(const obs_output_t *) = nullptr;
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
    SetDllDirectoryW(bin.c_str());
    g_bin_path = narrow(bin.wstring());
    g_obs = LoadLibraryW((bin / L"obs.dll").c_str());
    if (!g_obs) {
        set_error(L"Could not load obs.dll from " + bin.wstring());
        return false;
    }

    return load_fn(obs.startup, "obs_startup") &&
           load_fn(obs.shutdown, "obs_shutdown") &&
           load_fn(obs.set_log_handler, "base_set_log_handler") &&
           load_fn(obs.add_data_path, "obs_add_data_path") &&
           load_fn(obs.add_module_path, "obs_add_module_path") &&
           load_fn(obs.load_all_modules, "obs_load_all_modules") &&
           load_fn(obs.open_module, "obs_open_module") &&
           load_fn(obs.init_module, "obs_init_module") &&
           load_fn(obs.post_load_modules, "obs_post_load_modules") &&
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
           load_fn(obs.sceneitem_set_order, "obs_sceneitem_set_order") &&
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
           load_fn(obs.output_get_last_error, "obs_output_get_last_error") &&
           load_fn(obs.output_update, "obs_output_update") &&
           load_fn(obs.output_release, "obs_output_release") &&
           load_fn(obs.output_get_proc_handler, "obs_output_get_proc_handler") &&
           load_fn(obs.proc_handler_call, "proc_handler_call") &&
           load_fn(obs.calldata_get_string, "calldata_get_string") &&
           load_fn(obs.bfree, "bfree");
}

std::filesystem::path app_data_folder()
{
    wchar_t local[MAX_PATH] = {};
    DWORD length = GetEnvironmentVariableW(L"LOCALAPPDATA", local, MAX_PATH);
    if (length > 0 && length < MAX_PATH) {
        return std::filesystem::path(local) / L"EVE";
    }

    return std::filesystem::temp_directory_path() / L"EVE";
}

void __cdecl obs_log_handler(int, const char *message, va_list args, void *)
{
    try {
        char buffer[4096] = {};
        vsnprintf_s(buffer, sizeof(buffer), _TRUNCATE, message, args);
        const auto folder = app_data_folder() / L"logs";
        std::filesystem::create_directories(folder);
        std::ofstream file(folder / L"obs-bridge.log", std::ios::app);
        file << buffer << '\n';
    } catch (...) {
    }
}

void cleanup_obs()
{
    if (g_replay) {
        if (obs.output_active && obs.output_active(g_replay)) {
            obs.output_stop(g_replay);
            for (int i = 0; i < 120 && obs.output_active(g_replay); i++) Sleep(25);
        }
        if (obs.output_release) obs.output_release(g_replay);
        g_replay = nullptr;
    }
    if (g_video_encoder) {
        if (obs.encoder_release) obs.encoder_release(g_video_encoder);
        g_video_encoder = nullptr;
    }
    if (g_audio_encoder) {
        if (obs.encoder_release) obs.encoder_release(g_audio_encoder);
        g_audio_encoder = nullptr;
    }
    if (g_capture_source) {
        if (obs.source_release) obs.source_release(g_capture_source);
        g_capture_source = nullptr;
    }
    if (g_fallback_source) {
        if (obs.source_release) obs.source_release(g_fallback_source);
        g_fallback_source = nullptr;
    }
    if (g_scene_source) {
        if (obs.set_output_source) obs.set_output_source(0, nullptr);
        if (obs.source_release) obs.source_release(g_scene_source);
        g_scene_source = nullptr;
    }
    if (g_initialized && obs.shutdown) {
        obs.shutdown();
    }

    g_initialized = false;
}

std::filesystem::path process_sibling(const wchar_t *file_name)
{
    wchar_t path[MAX_PATH] = {};
    DWORD length = GetModuleFileNameW(nullptr, path, MAX_PATH);
    if (length == 0 || length >= MAX_PATH) return {};
    return std::filesystem::path(path).parent_path() / file_name;
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
    auto [fallback_width, fallback_height] = output_size();
    obs_data_t *fallback_settings = obs.data_create();
    obs.data_set_int(fallback_settings, "color", 0xFF000000);
    obs.data_set_int(fallback_settings, "width", fallback_width);
    obs.data_set_int(fallback_settings, "height", fallback_height);
    g_fallback_source = obs.source_create("color_source", "EVE Idle Frame", fallback_settings, nullptr);
    obs.data_release(fallback_settings);
    if (!g_fallback_source) {
        trace("init: color_source fallback unavailable");
    } else {
        trace("init: capture_source color_source idle fallback");
    }

    obs_data_t *settings = obs.data_create();
    obs.data_set_string(settings, "capture_mode", "any_fullscreen");
    obs.data_set_bool(settings, "capture_cursor", true);
    obs.data_set_bool(settings, "anti_cheat_hook", true);
    obs.data_set_bool(settings, "capture_overlays", false);
    obs.data_set_bool(settings, "limit_framerate", false);
    obs.data_set_int(settings, "hook_rate", 1);
    g_capture_source = obs.source_create("game_capture", "Auto Game Capture", settings, nullptr);
    obs.data_release(settings);
    if (!g_capture_source) {
        set_error(L"OBS game_capture source failed.");
        return false;
    }
    trace("init: capture_source game_capture any_fullscreen");

    g_scene = obs.scene_create("EVE Replay Scene");
    if (!g_scene) {
        set_error(L"OBS scene create failed.");
        return false;
    }

    if (g_fallback_source) {
        obs_sceneitem_t *fallback_item = obs.scene_add(g_scene, g_fallback_source);
        if (!fallback_item) {
            set_error(L"OBS scene add fallback failed.");
            return false;
        }
        obs.sceneitem_set_order(fallback_item, 3);
    }

    g_scene_item = obs.scene_add(g_scene, g_capture_source);
    if (!g_scene_item) {
        set_error(L"OBS scene add failed.");
        return false;
    }
    obs.sceneitem_set_order(g_scene_item, 2);

    g_scene_source = obs.scene_get_source(g_scene);
    obs.set_output_source(0, g_scene_source);
    obs.source_release(g_capture_source);
    g_capture_source = nullptr;
    if (g_fallback_source) {
        obs.source_release(g_fallback_source);
        g_fallback_source = nullptr;
    }
    return true;
}

bool create_replay_output()
{
    obs_data_t *v = obs.data_create();
    obs.data_set_string(v, "rate_control", "CQP");
    obs.data_set_int(v, "cqp", 20);
    obs.data_set_string(v, "preset2", "p5");
    obs.data_set_string(v, "multipass", "qres");
    obs.data_set_string(v, "tune", "hq");
    obs.data_set_string(v, "profile", "high");
    obs.data_set_int(v, "keyint_sec", 2);
    obs.data_set_bool(v, "psycho_aq", true);
    obs.data_set_int(v, "gpu", 0);
    obs.data_set_int(v, "bf", 2);
    g_video_encoder = obs.video_encoder_create("jim_nvenc", "EVE NVENC H.264", v, nullptr);
    obs.data_release(v);
    if (!g_video_encoder) {
        set_error(L"OBS NVENC encoder create failed. EVE requires NVIDIA NVENC for replay to avoid CPU capture stutter.");
        return false;
    }
    trace("init: video_encoder jim_nvenc created");
    obs.encoder_set_video(g_video_encoder, obs.get_video());

    obs_data_t *a = obs.data_create();
    obs.data_set_int(a, "bitrate", 192);
    g_audio_encoder = obs.audio_encoder_create("ffmpeg_aac", "EVE AAC", a, 0, nullptr);
    obs.data_release(a);
    if (g_audio_encoder) obs.encoder_set_audio(g_audio_encoder, obs.get_audio());

    obs_data_t *o = obs.data_create();
    obs.data_set_int(o, "max_time_sec", g_duration_seconds);
    obs.data_set_string(o, "format", "Replay %CCYY-%MM-%DD %hh-%mm-%ss");
    obs.data_set_string(o, "extension", "mp4");
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

bool load_module(const std::filesystem::path &root, const wchar_t *name)
{
    const auto path = root / L"obs-plugins" / L"64bit" / (std::wstring(name) + L".dll");
    const auto data = root / L"data" / L"obs-plugins" / name;
    const auto module_name = narrow(std::wstring(name));
    obs_module_t *module = nullptr;
    int result = obs.open_module(&module, narrow(path.wstring()).c_str(), narrow(data.wstring()).c_str());
    trace("init: open_module " + module_name + " result=" + std::to_string(result));
    if (result == 0 && module) {
        bool loaded = obs.init_module(module);
        trace("init: init_module " + module_name + " loaded=" + std::to_string(loaded ? 1 : 0));
        return loaded;
    }
    return false;
}

bool copy_string(wchar_t *destination, int length, const std::wstring &value)
{
    if (!destination || length <= 0) return false;
    wcsncpy_s(destination, static_cast<size_t>(length), value.c_str(), _TRUNCATE);
    return true;
}

bool usable_replay_file(const std::filesystem::path &path)
{
    std::error_code error;
    return std::filesystem::exists(path, error) &&
           std::filesystem::is_regular_file(path, error) &&
           std::filesystem::file_size(path, error) > 0;
}

std::wstring find_newest_replay_file(const std::filesystem::path &folder, const std::filesystem::file_time_type &after)
{
    std::error_code error;
    if (!std::filesystem::exists(folder, error)) return {};

    std::filesystem::path newest;
    std::filesystem::file_time_type newest_time = after;
    for (const auto &entry : std::filesystem::directory_iterator(folder, error)) {
        if (error) break;
        if (!entry.is_regular_file(error)) continue;
        auto path = entry.path();
        auto extension = path.extension().wstring();
        std::transform(extension.begin(), extension.end(), extension.begin(), ::towlower);
        if (extension != L".mp4" && extension != L".mkv") continue;
        if (!usable_replay_file(path)) continue;
        auto written = entry.last_write_time(error);
        if (error || written < after || written < newest_time) continue;
        newest = path;
        newest_time = written;
    }

    return newest.empty() ? std::wstring{} : newest.wstring();
}

} // namespace

extern "C" __declspec(dllexport) int eve_obs_init(const wchar_t *runtime_folder, int max_height, int frame_rate, int duration_seconds)
{
    std::lock_guard lock(g_lock);
    trace("init: enter");
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

    SetCurrentDirectoryW(bin.c_str());
    trace("init: load_obs_api");
    if (!load_obs_api(bin)) return -2;

    trace("init: set_log_handler");
    obs.set_log_handler(obs_log_handler, nullptr);
    const auto data_root = root / L"data";
    const auto plugins = root / L"obs-plugins" / L"64bit";
    const auto data = data_root / L"obs-plugins" / L"%module%";
    g_data_path = narrow(data_root.wstring());
    g_plugin_binary_path = narrow((plugins / L"%module%.dll").wstring());
    g_plugin_data_path = narrow(data.wstring());
    trace("init: add_data_path");
    obs.add_data_path(g_data_path.c_str());
    trace("init: add_module_path");
    obs.add_module_path(g_plugin_binary_path.c_str(), g_plugin_data_path.c_str());

    trace("init: config_path_begin");
    const auto config_folder = app_data_folder() / L"obs-config";
    trace("init: config_create");
    const auto app_folder = config_folder.parent_path();
    CreateDirectoryW(app_folder.c_str(), nullptr);
    CreateDirectoryW(config_folder.c_str(), nullptr);
    trace("init: config_narrow");
    g_config_path = narrow(config_folder.wstring());
    trace("init: startup");
    if (!obs.startup("en-US", g_config_path.c_str(), nullptr)) {
        set_error(L"obs_startup failed.");
        cleanup_obs();
        return -3;
    }
    g_initialized = true;

    const auto nvenc_helper = process_sibling(L"obs-nvenc-test.exe");
    trace("init: process_id=" + std::to_string(GetCurrentProcessId()));
    trace("init: nvenc_helper " + narrow(nvenc_helper.wstring()) + " exists=" + std::to_string(std::filesystem::exists(nvenc_helper) ? 1 : 0));

    trace("init: load_selected_modules");
    load_module(root, L"win-capture");
    load_module(root, L"image-source");
    load_module(root, L"obs-ffmpeg");
    if (!load_module(root, L"obs-nvenc")) {
        set_error(L"OBS NVENC module failed. Expected obs-nvenc-test.exe beside EVE.exe: " + nvenc_helper.wstring());
        cleanup_obs();
        return -4;
    }
    obs.post_load_modules();

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
    trace("init: reset_video");
    int video_result = obs.reset_video(&video);
    if (video_result != 0) {
        set_error(L"obs_reset_video failed: " + std::to_wstring(video_result));
        cleanup_obs();
        return -5;
    }

    ObsAudioInfo audio = {};
    audio.samples_per_sec = 48000;
    audio.speakers = SPEAKERS_STEREO;
    trace("init: reset_audio");
    if (!obs.reset_audio(&audio)) {
        set_error(L"obs_reset_audio failed.");
        cleanup_obs();
        return -6;
    }

    trace("init: create_scene");
    if (!create_scene()) {
        cleanup_obs();
        return -7;
    }
    trace("init: create_replay_output");
    if (!create_replay_output()) {
        cleanup_obs();
        return -8;
    }
    trace("init: success");
    return 0;
}

extern "C" __declspec(dllexport) int eve_obs_start_replay_capture()
{
    std::lock_guard lock(g_lock);
    if (!g_initialized || !g_replay) {
        set_error(L"OBS bridge not initialized.");
        return -1;
    }
    if (obs.output_active(g_replay)) return 0;
    trace("start: output_start");
    if (!obs.output_start(g_replay)) {
        const char *last_error = obs.output_get_last_error ? obs.output_get_last_error(g_replay) : nullptr;
        std::wstring detail = widen(last_error);
        set_error(detail.empty() ? L"obs_output_start replay_buffer failed." : L"obs_output_start replay_buffer failed: " + detail);
        return -2;
    }
    trace("start: output_started");
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
    const auto save_started_at = std::filesystem::file_time_type::clock::now();
    obs_data_t *settings = obs.data_create();
    obs.data_set_string(settings, "directory", narrow(folder.wstring()).c_str());
    obs.data_set_string(settings, "format", "Replay %CCYY-%MM-%DD %hh-%mm-%ss");
    obs.data_set_string(settings, "extension", "mp4");
    obs.data_set_string(settings, "format_name", "mp4");
    obs.data_set_bool(settings, "allow_spaces", true);
    obs.output_update(g_replay, settings);
    obs.data_release(settings);
    g_last_replay.clear();

    Calldata params = {};
    proc_handler_t *handler = obs.output_get_proc_handler(g_replay);
    trace("save: request");
    if (!handler || !obs.proc_handler_call(handler, "save", &params)) {
        if (!params.fixed && params.stack) obs.bfree(params.stack);
        set_error(L"OBS replay save proc failed.");
        return -2;
    }

    std::wstring saved_path;
    if (!params.fixed && params.stack) obs.bfree(params.stack);

    for (int i = 0; i < 400 && saved_path.empty(); i++) {
        Sleep(25);
        Calldata last = {};
        if (obs.proc_handler_call(handler, "get_last_replay", &last)) {
            const char *last_path = nullptr;
            if (obs.calldata_get_string(&last, "path", &last_path) && last_path && *last_path) {
                auto candidate = widen(last_path);
                if (usable_replay_file(candidate)) {
                    saved_path = candidate;
                }
            }
        }
        if (!last.fixed && last.stack) obs.bfree(last.stack);

        if (saved_path.empty()) {
            saved_path = find_newest_replay_file(folder, save_started_at);
        }

        if (saved_path.empty()) {
            saved_path = find_newest_replay_file(std::filesystem::temp_directory_path(), save_started_at);
        }
    }

    if (saved_path.empty()) {
        trace("save: no path after wait");
        set_error(L"OBS replay save returned no path.");
        return -3;
    }

    g_last_replay = saved_path;
    trace("save: path=" + narrow(g_last_replay));
    copy_string(output_path, output_path_length, g_last_replay);
    return 0;
}

extern "C" __declspec(dllexport) int eve_obs_stop()
{
    std::lock_guard lock(g_lock);
    if (!g_initialized || !g_replay) return 0;
    trace("stop: enter");
    if (obs.output_active(g_replay)) {
        obs.output_stop(g_replay);
        for (int i = 0; i < 120 && obs.output_active(g_replay); i++) Sleep(25);
    }
    trace("stop: exit active=" + std::to_string(obs.output_active(g_replay) ? 1 : 0));
    return 0;
}

extern "C" __declspec(dllexport) void eve_obs_shutdown()
{
    std::lock_guard lock(g_lock);
    if (!g_initialized) return;
    cleanup_obs();
}

extern "C" __declspec(dllexport) void eve_obs_last_error(wchar_t *message, int message_length)
{
    std::lock_guard lock(g_lock);
    copy_string(message, message_length, g_last_error.empty() ? L"OBS bridge failed." : g_last_error);
}
