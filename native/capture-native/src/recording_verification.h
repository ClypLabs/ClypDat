#pragma once
#include <filesystem>
#include <string>
namespace clypdat {
// Explicit verification only. Always injects native generated sources into the
// same RecorderSession as the DLL; never changes production backend selection.
std::string verify_recording(const std::filesystem::path& directory,
    const std::filesystem::path& ffmpeg, int fps, bool variable, int seconds = 3);
}
