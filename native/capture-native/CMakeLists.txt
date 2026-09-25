cmake_minimum_required(VERSION 3.25)

project(ClypDatCaptureNative LANGUAGES C CXX)

if(NOT MSVC OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "ClypDat recording requires MSVC x64")
endif()
set(CLYPDAT_FFMPEG_SDK "${CMAKE_CURRENT_SOURCE_DIR}/build/sdk" CACHE PATH "Verified FFmpeg development SDK")
set(ffmpeg_include "${CLYPDAT_FFMPEG_SDK}/extracted/ffmpeg-8.1.2-full_build-shared/include")
if(NOT EXISTS "${ffmpeg_include}/libavcodec/avcodec.h")
    message(FATAL_ERROR "Run eng/Build-NativeCapture.ps1 to prepare the pinned FFmpeg SDK")
endif()
foreach(component avcodec avformat avutil swresample swscale)
    add_library(ffmpeg_${component} SHARED IMPORTED)
    file(GLOB runtime "${CMAKE_CURRENT_SOURCE_DIR}/../vendor/ffmpeg/${component}-*.dll")
    set_target_properties(ffmpeg_${component} PROPERTIES
        IMPORTED_IMPLIB "${CLYPDAT_FFMPEG_SDK}/lib/${component}.lib"
        IMPORTED_LOCATION "${runtime}"
        INTERFACE_INCLUDE_DIRECTORIES "${ffmpeg_include}")
endforeach()

add_library(RecorderCore STATIC
    src/video_encoder.cpp src/video_encoder.h
    src/encoder_backend.cpp src/encoder_backend.h
    src/readback_stage.cpp src/readback_stage.h
    src/detector_stage.cpp src/detector_stage.h
    src/overlay_compositor.cpp src/overlay_compositor.h
    src/recording_capture.cpp src/recording_capture.h
    src/recording_windows_source.cpp
    src/recording_audio.cpp src/recording_audio.h
    src/recording_process.cpp src/recording_process.h
    src/recording_save.cpp src/recording_save.h
    src/recorder_session.cpp src/recorder_session.h
    src/recording_verification.cpp src/recording_verification.h
    src/recording_overlays.cpp src/recording_overlays.h
    src/recording_overlays_input.cpp
    src/recording_overlays_camera.cpp)
target_compile_features(RecorderCore PUBLIC cxx_std_20)
target_include_directories(RecorderCore PUBLIC src)
target_compile_definitions(RecorderCore PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(RecorderCore PRIVATE /W4 /permissive- /EHsc)
set_target_properties(RecorderCore PROPERTIES MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
target_link_libraries(RecorderCore PUBLIC
    ffmpeg_avcodec ffmpeg_avformat ffmpeg_avutil ffmpeg_swresample ffmpeg_swscale
    d3d11 dxgi d3dcompiler runtimeobject windowsapp ole32 avrt user32 gdi32 mmdevapi uuid psapi)

add_library(ClypDat.Capture.Native SHARED
    src/engine.cpp
    src/recorder_abi.cpp
    src/recording_audio_meter.cpp
    src/recording_recovery_abi.cpp
    include/clypdat_recorder.h
    src/camera_preview_abi.cpp
    include/clypdat_camera_preview.h
    include/clypdat_capture_native.h)

target_compile_features(ClypDat.Capture.Native PRIVATE cxx_std_20)
set_target_properties(ClypDat.Capture.Native PROPERTIES
    OUTPUT_NAME "ClypDat.Capture.Native"
    WINDOWS_EXPORT_ALL_SYMBOLS OFF
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

target_include_directories(ClypDat.Capture.Native PUBLIC include)
target_compile_definitions(ClypDat.Capture.Native PRIVATE CD_BUILDING_DLL WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(ClypDat.Capture.Native PRIVATE /W4 /permissive- /EHsc)
target_link_libraries(ClypDat.Capture.Native PRIVATE RecorderCore d3d11 dxgi)
add_custom_command(TARGET ClypDat.Capture.Native POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        $<TARGET_RUNTIME_DLLS:ClypDat.Capture.Native>
        $<TARGET_FILE_DIR:ClypDat.Capture.Native>
    COMMAND_EXPAND_LISTS)

enable_testing()
add_executable(ClypDat.Capture.Native.Tests tests/abi_smoke.cpp)
target_compile_features(ClypDat.Capture.Native.Tests PRIVATE cxx_std_20)
target_include_directories(ClypDat.Capture.Native.Tests PRIVATE include)
target_link_libraries(ClypDat.Capture.Native.Tests PRIVATE ClypDat.Capture.Native)
add_test(NAME ClypDat.Capture.Native.AbiSmoke COMMAND ClypDat.Capture.Native.Tests)
option(CLYPDAT_TEST_GPU "Run tests requiring a physical D3D11 device" OFF)
if(CLYPDAT_TEST_GPU)
    add_test(NAME ClypDat.Capture.Native.DeviceLifecycle COMMAND ClypDat.Capture.Native.Tests --gpu)
endif()

# Compile the public ABI as C as well as C++.
add_library(ClypDat.Capture.Native.CHeader OBJECT tests/c_header.c)
target_include_directories(ClypDat.Capture.Native.CHeader PRIVATE include)
target_compile_features(ClypDat.Capture.Native.CHeader PRIVATE c_std_11)

add_executable(ClypDat.Capture.Native.FfmpegTests tests/ffmpeg_sdk.cpp)
target_compile_features(ClypDat.Capture.Native.FfmpegTests PRIVATE cxx_std_20)
target_link_libraries(ClypDat.Capture.Native.FfmpegTests PRIVATE
    RecorderCore)
set_target_properties(ClypDat.Capture.Native.FfmpegTests PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_custom_command(TARGET ClypDat.Capture.Native.FfmpegTests POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        $<TARGET_RUNTIME_DLLS:ClypDat.Capture.Native.FfmpegTests>
        $<TARGET_FILE_DIR:ClypDat.Capture.Native.FfmpegTests>
    COMMAND_EXPAND_LISTS)
add_test(NAME ClypDat.Capture.Native.FfmpegSdk COMMAND ClypDat.Capture.Native.FfmpegTests)
if(CLYPDAT_TEST_GPU)
    add_test(NAME ClypDat.Capture.Native.NvencD3D11 COMMAND ClypDat.Capture.Native.FfmpegTests --gpu)
endif()

add_executable(ClypDat.Capture.Native.EncoderTests tests/encoder_failures.cpp)
target_link_libraries(ClypDat.Capture.Native.EncoderTests PRIVATE RecorderCore)
set_target_properties(ClypDat.Capture.Native.EncoderTests PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_test(NAME ClypDat.Capture.Native.EncoderFailures COMMAND ClypDat.Capture.Native.EncoderTests)

add_executable(ClypDat.Capture.Native.EncoderPolicyTests tests/encoder_backend_tests.cpp)
target_link_libraries(ClypDat.Capture.Native.EncoderPolicyTests PRIVATE RecorderCore)
set_target_properties(ClypDat.Capture.Native.EncoderPolicyTests PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_test(NAME ClypDat.Capture.Native.EncoderPolicy COMMAND ClypDat.Capture.Native.EncoderPolicyTests)

add_executable(ClypDat.Capture.Native.CaptureTests tests/recording_capture_tests.cpp)
target_link_libraries(ClypDat.Capture.Native.CaptureTests PRIVATE RecorderCore)
target_compile_definitions(ClypDat.Capture.Native.CaptureTests PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
set_target_properties(ClypDat.Capture.Native.CaptureTests PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_test(NAME ClypDat.Capture.Native.CapturePipeline COMMAND ClypDat.Capture.Native.CaptureTests)

add_executable(ClypDat.Capture.Native.AudioSaveTests tests/recording_audio_save.cpp)
target_link_libraries(ClypDat.Capture.Native.AudioSaveTests PRIVATE RecorderCore)
set_target_properties(ClypDat.Capture.Native.AudioSaveTests PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_test(NAME ClypDat.Capture.Native.AudioSave COMMAND ClypDat.Capture.Native.AudioSaveTests)

add_executable(ClypDat.Capture.Native.SessionRecoveryTests tests/recording_session_recovery.cpp)
target_link_libraries(ClypDat.Capture.Native.SessionRecoveryTests PRIVATE RecorderCore)
set_target_properties(ClypDat.Capture.Native.SessionRecoveryTests PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_test(NAME ClypDat.Capture.Native.SessionRecovery COMMAND ClypDat.Capture.Native.SessionRecoveryTests)

add_executable(ClypDat.Capture.Native.SessionTests tests/recording_session_tests.cpp)
target_link_libraries(ClypDat.Capture.Native.SessionTests PRIVATE RecorderCore)
set_target_properties(ClypDat.Capture.Native.SessionTests PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_test(NAME ClypDat.Capture.Native.Session COMMAND ClypDat.Capture.Native.SessionTests)
if(CLYPDAT_TEST_GPU)
    add_test(NAME ClypDat.Capture.Native.GeneratedGpuCapture COMMAND ClypDat.Capture.Native.CaptureTests --gpu)
endif()

add_executable(ClypDat.Capture.Native.OverlayTests tests/recording_overlays.cpp)
target_link_libraries(ClypDat.Capture.Native.OverlayTests PRIVATE RecorderCore)
set_target_properties(ClypDat.Capture.Native.OverlayTests PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_test(NAME ClypDat.Capture.Native.Overlays COMMAND ClypDat.Capture.Native.OverlayTests)

add_executable(ClypDat.Capture.Native.OverlayPipelineTests tests/overlay_gpu_tests.cpp)
target_link_libraries(ClypDat.Capture.Native.OverlayPipelineTests PRIVATE RecorderCore)
target_compile_definitions(ClypDat.Capture.Native.OverlayPipelineTests PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
set_target_properties(ClypDat.Capture.Native.OverlayPipelineTests PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_test(NAME ClypDat.Capture.Native.OverlayPipeline COMMAND ClypDat.Capture.Native.OverlayPipelineTests)
if(CLYPDAT_TEST_GPU)
    add_test(NAME ClypDat.Capture.Native.GpuOverlayPipeline COMMAND ClypDat.Capture.Native.OverlayPipelineTests --gpu)
endif()

add_executable(ClypDat.Capture.Native.WgcCadenceTests tests/wgc_cadence_tests.cpp)
target_link_libraries(ClypDat.Capture.Native.WgcCadenceTests PRIVATE RecorderCore)
target_compile_definitions(ClypDat.Capture.Native.WgcCadenceTests PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
set_target_properties(ClypDat.Capture.Native.WgcCadenceTests PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_test(NAME ClypDat.Capture.Native.WgcCadence COMMAND ClypDat.Capture.Native.WgcCadenceTests)

add_executable(ClypDat.Capture.Native.VideoHistoryTests tests/video_history_tests.cpp)
target_link_libraries(ClypDat.Capture.Native.VideoHistoryTests PRIVATE RecorderCore)
target_compile_definitions(ClypDat.Capture.Native.VideoHistoryTests PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
set_target_properties(ClypDat.Capture.Native.VideoHistoryTests PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_test(NAME ClypDat.Capture.Native.VideoHistory COMMAND ClypDat.Capture.Native.VideoHistoryTests)

add_executable(ClypDat.Capture.Native.DetectorTests tests/detector_stage_tests.cpp)
target_link_libraries(ClypDat.Capture.Native.DetectorTests PRIVATE RecorderCore windowscodecs)
target_compile_definitions(ClypDat.Capture.Native.DetectorTests PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
set_target_properties(ClypDat.Capture.Native.DetectorTests PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_test(NAME ClypDat.Capture.Native.DetectorStage COMMAND ClypDat.Capture.Native.DetectorTests)
if(CLYPDAT_TEST_GPU)
    add_test(NAME ClypDat.Capture.Native.GpuDetectorStage COMMAND ClypDat.Capture.Native.DetectorTests --gpu)
endif()

# Manual: detector regression corpus over captured game frames (see the file).
add_executable(ClypDat.Capture.Native.DetectorCorpus tests/detector_corpus.cpp)
target_link_libraries(ClypDat.Capture.Native.DetectorCorpus PRIVATE RecorderCore windowscodecs)
target_compile_definitions(ClypDat.Capture.Native.DetectorCorpus PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
set_target_properties(ClypDat.Capture.Native.DetectorCorpus PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# Manual: detector cost on the capture pipeline with a generated 4K source.
add_executable(ClypDat.Capture.Native.DetectorBench tests/detector_bench.cpp)
target_link_libraries(ClypDat.Capture.Native.DetectorBench PRIVATE RecorderCore)
target_compile_definitions(ClypDat.Capture.Native.DetectorBench PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
set_target_properties(ClypDat.Capture.Native.DetectorBench PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# Manual: production replay history and save check on the primary monitor.
add_executable(ClypDat.Capture.Native.ReplaySaveCheck tests/replay_save_check.cpp)
target_link_libraries(ClypDat.Capture.Native.ReplaySaveCheck PRIVATE RecorderCore)
target_compile_definitions(ClypDat.Capture.Native.ReplaySaveCheck PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
set_target_properties(ClypDat.Capture.Native.ReplaySaveCheck PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# Manual: WGC producer cadence on the real primary display.
add_executable(ClypDat.Capture.Native.WgcCadenceBench tests/wgc_cadence_bench.cpp)
target_link_libraries(ClypDat.Capture.Native.WgcCadenceBench PRIVATE RecorderCore pdh)
target_compile_definitions(ClypDat.Capture.Native.WgcCadenceBench PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
set_target_properties(ClypDat.Capture.Native.WgcCadenceBench PROPERTIES
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
