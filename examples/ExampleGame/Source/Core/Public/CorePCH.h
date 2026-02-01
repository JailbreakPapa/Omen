// Core Module Precompiled Header

#pragma once

// Platform detection
#if defined(_WIN32) || defined(_WIN64)
    #define PLATFORM_WINDOWS 1
#elif defined(__linux__)
    #define PLATFORM_LINUX 1
#elif defined(__FreeBSD__)
    #define PLATFORM_FREEBSD 1
#elif defined(__APPLE__)
    #include <TargetConditionals.h>
    #if TARGET_OS_IOS
        #define PLATFORM_IOS 1
    #endif
#elif defined(__ANDROID__)
    #define PLATFORM_ANDROID 1
#endif

// Standard library includes
#include <cstdint>
#include <cstddef>
#include <cstring>
#include <cmath>

#include <memory>
#include <string>
#include <string_view>
#include <vector>
#include <array>
#include <unordered_map>
#include <unordered_set>
#include <optional>
#include <variant>
#include <functional>
#include <algorithm>
#include <utility>

// Platform-specific includes
#if PLATFORM_WINDOWS
    #ifndef WIN32_LEAN_AND_MEAN
        #define WIN32_LEAN_AND_MEAN
    #endif
    #ifndef NOMINMAX
        #define NOMINMAX
    #endif
    #include <Windows.h>
#elif PLATFORM_LINUX || PLATFORM_FREEBSD
    #include <pthread.h>
    #include <unistd.h>
    #include <sys/types.h>
#endif

// Core types
namespace Core
{
    using int8 = std::int8_t;
    using int16 = std::int16_t;
    using int32 = std::int32_t;
    using int64 = std::int64_t;
    
    using uint8 = std::uint8_t;
    using uint16 = std::uint16_t;
    using uint32 = std::uint32_t;
    using uint64 = std::uint64_t;
    
    using float32 = float;
    using float64 = double;
}
