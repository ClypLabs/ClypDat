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

add_library(ClypDat.Capture.Native SHARED
    src/engine.cpp
    include/clypdat_capture_native.h)

target_compile_features(ClypDat.Capture.Native PRIVATE cxx_std_20)
set_target_properties(ClypDat.Capture.Native PROPERTIES
    OUTPUT_NAME "ClypDat.Capture.Native"
    WINDOWS_EXPORT_ALL_SYMBOLS OFF
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

target_include_directories(ClypDat.Capture.Native PUBLIC include)
target_compile_definitions(ClypDat.Capture.Native PRIVATE CD_BUILDING_DLL WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(ClypDat.Capture.Native PRIVATE /W4 /permissive- /EHsc)
target_link_libraries(ClypDat.Capture.Native PRIVATE d3d11 dxgi)

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
    ffmpeg_avcodec ffmpeg_avformat ffmpeg_avutil ffmpeg_swresample ffmpeg_swscale)
add_custom_command(TARGET ClypDat.Capture.Native.FfmpegTests POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        $<TARGET_RUNTIME_DLLS:ClypDat.Capture.Native.FfmpegTests>
        $<TARGET_FILE_DIR:ClypDat.Capture.Native.FfmpegTests>
    COMMAND_EXPAND_LISTS)
add_test(NAME ClypDat.Capture.Native.FfmpegSdk COMMAND ClypDat.Capture.Native.FfmpegTests)
