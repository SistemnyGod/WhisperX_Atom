#include "whisperx_refiner_bridge.h"
#include "whisper.h"

#include <algorithm>
#include <cstring>
#include <string>
#include <vector>

struct whisperx_refiner_context {
    whisper_context * whisper = nullptr;
};

int whisperx_refiner_abi_version(void) {
    return WHISPERX_REFINER_ABI_VERSION;
}

void * whisperx_refiner_init(const char * model_path_utf8) {
    if (model_path_utf8 == nullptr || *model_path_utf8 == '\0') return nullptr;
    auto * context = new whisperx_refiner_context();
    auto params = whisper_context_default_params();
    params.use_gpu = false;
    params.flash_attn = false;
    context->whisper = whisper_init_from_file_with_params(model_path_utf8, params);
    if (context->whisper == nullptr) {
        delete context;
        return nullptr;
    }
    return context;
}

int whisperx_refiner_transcribe(void * raw_context, const int16_t * pcm16k_mono, int pcm_bytes, char * utf8_output, int output_capacity) {
    if (raw_context == nullptr || pcm16k_mono == nullptr || utf8_output == nullptr || output_capacity <= 1 || pcm_bytes <= 0 || (pcm_bytes % 2) != 0) return -1;
    auto * context = static_cast<whisperx_refiner_context *>(raw_context);
    const int samples_count = pcm_bytes / 2;
    std::vector<float> samples(static_cast<size_t>(samples_count));
    for (int i = 0; i < samples_count; ++i) samples[static_cast<size_t>(i)] = static_cast<float>(pcm16k_mono[i]) / 32768.0f;

    auto params = whisper_full_default_params(WHISPER_SAMPLING_GREEDY);
    params.n_threads = 1;
    params.no_context = true;
    params.no_timestamps = true;
    params.single_segment = false;
    params.print_progress = false;
    params.print_realtime = false;
    params.print_timestamps = false;
    params.language = "ru";
    params.translate = false;
    params.suppress_blank = true;
    params.suppress_nst = true;
    params.temperature = 0.0f;
    params.temperature_inc = 0.0f;

    if (whisper_full(context->whisper, params, samples.data(), samples_count) != 0) return -1;
    std::string text;
    const int segments = whisper_full_n_segments(context->whisper);
    for (int i = 0; i < segments; ++i) {
        const char * segment = whisper_full_get_segment_text(context->whisper, i);
        if (segment != nullptr) text.append(segment);
    }
    while (!text.empty() && (text.back() == '\n' || text.back() == '\r' || text.back() == ' ' || text.back() == '\t')) text.pop_back();
    if (text.empty()) return 0;
    const int bytes = static_cast<int>(text.size());
    const int copy_count = std::min(bytes, output_capacity - 1);
    std::memcpy(utf8_output, text.data(), static_cast<size_t>(copy_count));
    utf8_output[copy_count] = '\0';
    return copy_count;
}

void whisperx_refiner_free(void * raw_context) {
    auto * context = static_cast<whisperx_refiner_context *>(raw_context);
    if (context == nullptr) return;
    if (context->whisper != nullptr) whisper_free(context->whisper);
    delete context;
}
