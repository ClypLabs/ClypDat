#pragma once
#include "video_encoder.h"
#include <map>

namespace clypdat::detail {
// The readback encoder disables B frames. Packets sharing a QSV 90 kHz
// timestamp must consume their original microsecond timestamps in input order.
template<class Value>
auto reconcile_qsv_readback_timestamp(std::map<int64_t, Value>& pending,
    AVPacket& packet, AVRational time_base) {
    const auto round_trip = [&](int64_t value) {
        return av_rescale_q(av_rescale_q(value, time_base, AVRational{1,90000}),
            AVRational{1,90000}, time_base);
    };
    // Even an exact match can belong to a newer pending input. The ledger is
    // bounded by max_in_flight; scan in submission order without allocating.
    auto mapping = pending.begin();
    while (mapping != pending.end() && round_trip(mapping->first) != packet.pts) ++mapping;
    if (mapping != pending.end()) {
        if (packet.dts != AV_NOPTS_VALUE) packet.dts += mapping->first - packet.pts;
        packet.pts = mapping->first;
    }
    return mapping;
}
}
