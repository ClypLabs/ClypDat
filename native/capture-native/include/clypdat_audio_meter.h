#pragma once
#include "clypdat_recorder.h"
#pragma pack(push,8)
typedef struct cd_audio_meter_config {
    cd_struct_header header;
    cd_string16 device_id,ffmpeg_path,rnnoise_model;
    uint32_t noise_suppression,reserved;
    double gate_db;
} cd_audio_meter_config;
#pragma pack(pop)
typedef struct cd_audio_meter cd_audio_meter;
CD_API int32_t CD_CALL cd_audio_meter_create(const cd_audio_meter_config* config,cd_audio_meter** meter);
CD_API int32_t CD_CALL cd_audio_meter_level(cd_audio_meter* meter,float* level_db);
CD_API int32_t CD_CALL cd_audio_meter_destroy(cd_audio_meter* meter);
