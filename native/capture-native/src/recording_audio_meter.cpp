#include "clypdat_audio_meter.h"
#include "recording_audio.h"
#include <Windows.h>
#include <stdexcept>
struct cd_audio_meter {std::unique_ptr<clypdat::AudioMeter> value;};
namespace {
std::wstring copy(cd_string16 text){if(text.length>32768||(!text.data&&text.length))throw std::invalid_argument("Invalid audio meter string");return text.length?std::wstring(reinterpret_cast<const wchar_t*>(text.data),text.length):std::wstring();}
}
int32_t CD_CALL cd_audio_meter_create(const cd_audio_meter_config* config,cd_audio_meter** output){
    if(!output)return CD_E_INVALID_ARGUMENT;*output=nullptr;
    if(!config||config->header.struct_size<sizeof(*config))return CD_E_INVALID_ARGUMENT;
    if(config->header.abi_version!=CD_ABI_VERSION)return CD_E_UNSUPPORTED_ABI;
    try{clypdat::AudioMeterConfig settings;settings.source.device_id=copy(config->device_id);if(settings.source.device_id==L"default")settings.source.device_id.clear();settings.source.lane="meter";settings.source.source="meter";LARGE_INTEGER qpc,frequency;QueryPerformanceCounter(&qpc);QueryPerformanceFrequency(&frequency);settings.source.qpc_anchor=qpc.QuadPart;settings.source.qpc_frequency=frequency.QuadPart;settings.ffmpeg=copy(config->ffmpeg_path);settings.rnnoise_model=copy(config->rnnoise_model);settings.suppression=config->noise_suppression!=0;settings.gate_db=config->gate_db;auto meter=std::make_unique<cd_audio_meter>();meter->value=std::make_unique<clypdat::AudioMeter>(std::move(settings));*output=meter.release();return CD_OK;}catch(...){return CD_E_DEVICE_FAILURE;}
}
int32_t CD_CALL cd_audio_meter_level(cd_audio_meter* meter,float* db){if(!meter||!db)return CD_E_INVALID_ARGUMENT;*db=meter->value->level_db();return CD_OK;}
int32_t CD_CALL cd_audio_meter_destroy(cd_audio_meter* meter){if(!meter)return CD_OK;if(!meter->value->stop())return CD_E_INVALID_STATE;delete meter;return CD_OK;}
