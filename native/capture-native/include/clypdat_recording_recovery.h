#pragma once
#include "clypdat_recorder.h"
#pragma pack(push,8)
typedef struct cd_recording_recovery_config {
    cd_struct_header header;
    cd_string16 input_path,ffmpeg_path;
} cd_recording_recovery_config;
#pragma pack(pop)
// Recovery retains the original until native probing verifies positive duration
// and identical ordered stream identities. The caller owns the recording lease.
CD_API int32_t CD_CALL cd_recording_recover(const cd_recording_recovery_config* config,uint32_t* recovered);
