#include "recording_verification.h"
#include <Windows.h>
#include <fstream>
#include <iostream>
int wmain(int argc, wchar_t** argv) {
    try {
        const auto root = argc > 1 ? std::filesystem::path(argv[1]) :
            std::filesystem::current_path() / (L"session-verification-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
        const auto ffmpeg = argc > 2 ? std::filesystem::path(argv[2]) :
            std::filesystem::absolute(std::filesystem::path(L"../../vendor/ffmpeg/ffmpeg.exe"));
        for (int fps : {30, 60, 90, 120}) for (bool variable : {false, true}) {
            const auto directory = root / (std::to_wstring(fps) + (variable ? L"-VFR" : L"-CFR"));
            const auto result = clypdat::verify_recording(directory, ffmpeg, fps, variable, 1);
            std::ofstream(directory / L"result.json") << result;
            std::cout << result << '\n';
        }
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
