#pragma once
#include <atomic>
#include <chrono>
#include <filesystem>
#include <string>
#include <vector>
#include <functional>
#include <cstdint>

namespace clypdat {
struct ProcessResult { unsigned long exit_code = 0; std::string output, error; };
// All arguments use Windows CRT quoting; no shell is involved. Cancellation
// kills the entire job before output readers and process handles are closed.
class ProcessRunner {
public:
    static ProcessResult run(const std::filesystem::path& executable,
        const std::vector<std::wstring>& arguments, const std::atomic_bool& cancel,
        std::chrono::milliseconds timeout = std::chrono::minutes(5),
        std::function<void(const uint8_t*, size_t)> stdout_sink = {}, bool require_success = true,
        std::function<size_t(uint8_t*, size_t)> stdin_source = {});
    static std::wstring quote(const std::wstring& argument);
};
}
