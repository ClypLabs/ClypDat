#include "clypdat_capture_native.h"
#include "clypdat_recorder.h"
#include "clypdat_camera_preview.h"
#include "clypdat_audio_meter.h"
#include "clypdat_recording_recovery.h"

_Static_assert(sizeof(cd_abi_info) == 40, "ABI information layout");
_Static_assert(sizeof(cd_engine_config) == 56, "Engine configuration layout");
_Static_assert(sizeof(cd_engine_health) == 112, "Engine health layout");
_Static_assert(sizeof(cd_save_request) == 24, "Save request layout");
_Static_assert(sizeof(cd_save_result) == 56, "Save result layout");
