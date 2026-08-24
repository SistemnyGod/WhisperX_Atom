#pragma once

#include <stdint.h>

#if defined(_WIN32)
#  define WHISPERX_REFINER_API __declspec(dllexport)
#else
#  define WHISPERX_REFINER_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

WHISPERX_REFINER_API int whisperx_refiner_abi_version(void);
WHISPERX_REFINER_API void * whisperx_refiner_init(const char * model_path_utf8);
WHISPERX_REFINER_API int whisperx_refiner_transcribe(void * context, const int16_t * pcm16k_mono, int pcm_bytes, char * utf8_output, int output_capacity);
WHISPERX_REFINER_API void whisperx_refiner_free(void * context);

#ifdef __cplusplus
}
#endif
