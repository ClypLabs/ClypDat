#include "clypdat_capture_native.h"
#include "clypdat_recorder.h"
#include "clypdat_camera_preview.h"
#include "clypdat_audio_meter.h"
#include "clypdat_recording_recovery.h"

_Static_assert(sizeof(cd_struct_header) == 8, "Structure header layout");
