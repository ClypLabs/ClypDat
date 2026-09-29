#pragma once

// Private native policy, shared with tests. No code or environment access in
// ordinary builds; no recorder ABI or worker IPC setting.
#ifdef CLYPDAT_ENABLE_ENCODER_INPUT_DIAGNOSTICS
#include "recording_capture.h"
#include <algorithm>
#include <cstdlib>
#include <stdexcept>

namespace clypdat::detail {
inline bool select_diagnostic_encoder_input(std::vector<RecordingEncoderCandidate>& candidates,
    const RecordingCaptureConfig& config) {
    char* value = nullptr; size_t length = 0;
    const auto error = _dupenv_s(&value, &length, "CLYPDAT_DIAGNOSTIC_ENCODER_INPUT");
    const std::unique_ptr<char, decltype(&std::free)> owned(value, &std::free);
    if (error) throw std::runtime_error("Diagnostic encoder input configuration: cannot read environment");
    if (!value || !*value) return false;
    const bool qsv = std::string(value) == "qsv-system-memory";
    if (!qsv && std::string(value) != "amf-system-memory")
        throw std::invalid_argument("Diagnostic encoder input configuration: unsupported CLYPDAT_DIAGNOSTIC_ENCODER_INPUT (expected amf-system-memory or qsv-system-memory)");
    if (config.cpu_encoder)
        throw std::invalid_argument("Diagnostic encoder input configuration: system-memory selector cannot use CPU encoder mode");
    bool low_power = false;
    if (qsv) {
        if (config.av1)
            throw std::invalid_argument("Diagnostic encoder input configuration: qsv-system-memory requires H.264");
        char* power = nullptr; size_t power_length = 0;
        const auto power_error = _dupenv_s(&power, &power_length, "CLYPDAT_DIAGNOSTIC_QSV_LOW_POWER");
        const std::unique_ptr<char, decltype(&std::free)> owned_power(power, &std::free);
        if (power_error) throw std::runtime_error("Diagnostic encoder input configuration: cannot read QSV low-power environment");
        if (!power || (std::string(power) != "0" && std::string(power) != "1"))
            throw std::invalid_argument("Diagnostic encoder input configuration: qsv-system-memory requires CLYPDAT_DIAGNOSTIC_QSV_LOW_POWER=0 or 1");
        low_power = std::string(power) == "1";
    }
    const auto requested = qsv ? "h264_qsv" : config.av1 ? "av1_amf" : "h264_amf";
    std::erase_if(candidates, [&](const auto& candidate) {
        return candidate.name != requested || candidate.d3d11 || candidate.low_power != low_power;
    });
    if (candidates.size() != 1)
        throw std::runtime_error("Diagnostic encoder input configuration: requested system-memory candidate unavailable");
    return true;
}
}
#endif
