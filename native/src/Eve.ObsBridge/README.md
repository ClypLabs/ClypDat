# Eve.ObsBridge

Native OBS/libobs bridge contract used by `ObsNativeBridge`.

Required exports:

```cpp
extern "C" __declspec(dllexport) int eve_obs_init(const wchar_t* runtime_folder, int max_height, int frame_rate, int duration_seconds);
extern "C" __declspec(dllexport) int eve_obs_start_primary_monitor();
extern "C" __declspec(dllexport) int eve_obs_save_replay(const wchar_t* output_folder, wchar_t* output_path, int output_path_length);
extern "C" __declspec(dllexport) int eve_obs_stop();
extern "C" __declspec(dllexport) void eve_obs_shutdown();
extern "C" __declspec(dllexport) void eve_obs_last_error(wchar_t* message, int message_length);
```

This folder documents the stable ABI before the full libobs C++ implementation is added.
