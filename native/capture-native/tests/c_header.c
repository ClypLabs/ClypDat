#include "clypdat_capture_native.h"
#include "clypdat_recorder.h"
#include "clypdat_camera_preview.h"
#include "clypdat_audio_meter.h"
#include "clypdat_recording_recovery.h"
#include "clypdat_clip_preview.h"

_Static_assert(sizeof(cd_struct_header) == 8, "Structure header layout");
_Static_assert(sizeof(cd_clip_preview_config) == 72, "Clip preview config layout");
_Static_assert(sizeof(cd_clip_preview_frame) == 40, "Clip preview frame layout");
