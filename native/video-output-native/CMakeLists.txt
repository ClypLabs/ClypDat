cmake_minimum_required(VERSION 3.25)
project(ClypDatVideoOutput LANGUAGES C CXX)
if(NOT MSVC OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "The pinned VLC output requires MSVC x64.")
endif()
if(NOT EXISTS "${VLC_ROOT}/include/vlc/plugins/vlc_common.h")
    message(FATAL_ERROR "Set VLC_ROOT to VideoLAN.LibVLC.Windows 3.0.23.1 build/x64.")
endif()
file(SHA256 "${VLC_ROOT}/libvlccore.dll" vlc_core_sha256)
if(NOT vlc_core_sha256 STREQUAL "d3475b834dd3eb77910f37f71b0341d358bcbdda5b9f04cc4a3a8e2be1bc8e35")
    message(FATAL_ERROR "VLC core does not match pinned NuGet package 3.0.23.1 / revision 79128878dd.")
endif()
set(vlc vendor/vlc/modules)
set(output ${vlc}/video_output/win32)
add_library(clypdat_video_output SHARED
    ${output}/direct3d11.c ${output}/d3d11_quad.c ${output}/d3d11_shaders.c
    ${output}/common.c ${output}/d3d11_scaler.cpp ${output}/d3d11_tonemap.cpp
    ${output}/events.c ${output}/sensors.cpp ${output}/win32touch.c
    ${vlc}/video_chroma/d3d11_fmt.c ${vlc}/video_chroma/dxgi_fmt.c ${vlc}/video_chroma/copy.c
    src/compositor.cpp src/player.c src/vlc_allocator.cpp)
set_target_properties(clypdat_video_output PROPERTIES OUTPUT_NAME libclypdat_d3d11_plugin
    C_STANDARD 11 CXX_STANDARD 20 MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
target_include_directories(clypdat_video_output PRIVATE compat include src
    "${VLC_ROOT}/include/vlc/plugins" vendor/vlc/include ${vlc}/video_output)
target_compile_definitions(clypdat_video_output PRIVATE MODULE_STRING="clypdat_d3d11"
    MODULE_NAME_IS_direct3d11 __PLUGIN__ _CRT_SECURE_NO_WARNINGS NOMINMAX)
target_compile_options(clypdat_video_output PRIVATE /FI${CMAKE_CURRENT_SOURCE_DIR}/compat/msvc.h /EHsc)
set_source_files_properties(${output}/events.c PROPERTIES COMPILE_OPTIONS /experimental:c11atomics)
target_link_libraries(clypdat_video_output PRIVATE "${VLC_ROOT}/libvlccore.lib"
    d3d11 d3dcompiler dxgi dxguid uuid ole32 user32 gdi32 shell32 sensorsapi ws2_32)
enable_testing()
add_executable(compositor_tests tests/compositor_tests.cpp src/compositor.cpp)
target_compile_features(compositor_tests PRIVATE cxx_std_20)
target_compile_definitions(compositor_tests PRIVATE NOMINMAX)
target_include_directories(compositor_tests PRIVATE include src)
target_link_libraries(compositor_tests PRIVATE d3d11 d3dcompiler)
add_test(NAME ClypDat.VideoOutput.GpuComposition COMMAND compositor_tests)
add_executable(vlc_tests tests/vlc_tests.cpp)
target_compile_features(vlc_tests PRIVATE cxx_std_20)
target_include_directories(vlc_tests PRIVATE include "${VLC_ROOT}/include")
target_link_libraries(vlc_tests PRIVATE clypdat_video_output "${VLC_ROOT}/libvlc.lib" user32)
target_link_libraries(vlc_tests PRIVATE dbghelp)
target_compile_options(clypdat_video_output PRIVATE /Zi)
target_link_options(clypdat_video_output PRIVATE /DEBUG)
target_compile_definitions(clypdat_video_output PRIVATE HAVE_ID3D11VIDEODECODER=1)
