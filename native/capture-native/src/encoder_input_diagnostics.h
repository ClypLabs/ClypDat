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
    if (std::string(value) != "amf-system-memory")
        throw std::invalid_argument("Diagnostic encoder input configuration: unsupported CLYPDAT_DIAGNOSTIC_ENCODER_INPUT (expected amf-system-memory)");
    if (config.cpu_encoder)
        throw std::invalid_argument("Diagnostic encoder input configuration: amf-system-memory cannot use CPU encoder mode");
    const auto requested = config.av1 ? "av1_amf" : "h264_amf";
    std::erase_if(candidates, [&](const auto& candidate) {
        return candidate.name != requested || candidate.d3d11 || candidate.low_power;
    });
    if (candidates.size() != 1)
        throw std::runtime_error("Diagnostic encoder input configuration: requested AMF system-memory candidate unavailable");
    return true;
}
}
#endif
