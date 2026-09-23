#include "clypdat_recording_recovery.h"
#include "recording_save.h"
#include <stdexcept>
namespace {
std::filesystem::path path(cd_string16 text){if(!text.data||!text.length||text.length>32768)throw std::invalid_argument("Invalid recovery path");std::wstring value(reinterpret_cast<const wchar_t*>(text.data),text.length);if(value.find(L'\0')!=std::wstring::npos)throw std::invalid_argument("Embedded NUL in recovery path");std::filesystem::path result(value);if(!result.is_absolute())throw std::invalid_argument("Recovery requires absolute paths");return result;}
}
int32_t CD_CALL cd_recording_recover(const cd_recording_recovery_config* config,uint32_t* recovered){if(!recovered)return CD_E_INVALID_ARGUMENT;*recovered=0;if(!config||config->header.struct_size<sizeof(*config))return CD_E_INVALID_ARGUMENT;if(config->header.abi_version!=CD_ABI_VERSION)return CD_E_UNSUPPORTED_ABI;try{auto input=path(config->input_path),ffmpeg=path(config->ffmpeg_path);std::atomic_bool cancel=false;*recovered=clypdat::recover_recording(input,ffmpeg,cancel)?1u:0u;return CD_OK;}catch(const std::invalid_argument&){return CD_E_INVALID_ARGUMENT;}catch(...){return CD_E_INTERNAL;}}
