cmake_minimum_required(VERSION 3.25)

project(ClypDatCaptureNative LANGUAGES C CXX)

add_library(ClypDat.Capture.Native SHARED
    src/engine.cpp
    include/clypdat_capture_native.h)

target_compile_features(ClypDat.Capture.Native PRIVATE cxx_std_20)
set_target_properties(ClypDat.Capture.Native PROPERTIES
    OUTPUT_NAME "ClypDat.Capture.Native"
    WINDOWS_EXPORT_ALL_SYMBOLS OFF
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

target_include_directories(ClypDat.Capture.Native PUBLIC include)
target_compile_definitions(ClypDat.Capture.Native PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(ClypDat.Capture.Native PRIVATE /W4 /permissive- /EHsc)
target_link_libraries(ClypDat.Capture.Native PRIVATE d3d11 dxgi)

enable_testing()
add_executable(ClypDat.Capture.Native.Tests tests/abi_smoke.cpp)
target_compile_features(ClypDat.Capture.Native.Tests PRIVATE cxx_std_20)
target_include_directories(ClypDat.Capture.Native.Tests PRIVATE include)
target_link_libraries(ClypDat.Capture.Native.Tests PRIVATE ClypDat.Capture.Native)
add_test(NAME ClypDat.Capture.Native.AbiSmoke COMMAND ClypDat.Capture.Native.Tests)
