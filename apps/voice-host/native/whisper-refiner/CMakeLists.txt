cmake_minimum_required(VERSION 3.20)
project(whisperx_refiner_bridge LANGUAGES C CXX)

include(FetchContent)

set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_SERVER OFF CACHE BOOL "" FORCE)
set(WHISPER_CURL OFF CACHE BOOL "" FORCE)
set(WHISPER_SDL2 OFF CACHE BOOL "" FORCE)
set(GGML_CUDA OFF CACHE BOOL "" FORCE)
set(GGML_NATIVE OFF CACHE BOOL "" FORCE)
set(GGML_OPENMP OFF CACHE BOOL "" FORCE)
set(GGML_VULKAN OFF CACHE BOOL "" FORCE)
set(GGML_SYCL OFF CACHE BOOL "" FORCE)
set(GGML_METAL OFF CACHE BOOL "" FORCE)

# whisper.cpp v1.9.1 (f049fff95a089aa9969deb009cdd4892b3e74916).
set(WHISPER_CPP_REVISION "f049fff95a089aa9969deb009cdd4892b3e74916" CACHE STRING "Pinned whisper.cpp revision")
set(WHISPER_CPP_SOURCE_DIR "" CACHE PATH "Optional pre-fetched whisper.cpp source at WHISPER_CPP_REVISION")
if (WHISPER_CPP_SOURCE_DIR)
    if (NOT EXISTS "${WHISPER_CPP_SOURCE_DIR}/CMakeLists.txt")
        message(FATAL_ERROR "WHISPER_CPP_SOURCE_DIR does not contain whisper.cpp")
    endif()
    set(whispercpp_SOURCE_DIR "${WHISPER_CPP_SOURCE_DIR}")
    add_subdirectory("${WHISPER_CPP_SOURCE_DIR}" "${CMAKE_BINARY_DIR}/whispercpp-build" EXCLUDE_FROM_ALL)
else()
    FetchContent_Declare(whispercpp
        GIT_REPOSITORY https://github.com/ggml-org/whisper.cpp.git
        GIT_TAG ${WHISPER_CPP_REVISION}
        GIT_SHALLOW FALSE)
    FetchContent_MakeAvailable(whispercpp)
endif()

add_library(whisperx-refiner SHARED whisperx_refiner_bridge.cpp)
target_compile_features(whisperx-refiner PRIVATE cxx_std_17)
target_include_directories(whisperx-refiner PRIVATE ${whispercpp_SOURCE_DIR}/include)
target_link_libraries(whisperx-refiner PRIVATE whisper)
target_compile_definitions(whisperx-refiner PRIVATE WHISPERX_REFINER_ABI_VERSION=1)
set_target_properties(whisperx-refiner PROPERTIES OUTPUT_NAME "whisperx-refiner")

if (MSVC)
    target_compile_options(whisperx-refiner PRIVATE /W4 /EHsc)
endif()
