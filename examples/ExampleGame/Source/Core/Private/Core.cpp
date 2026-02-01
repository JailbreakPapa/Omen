// Core Module Implementation

#include "CorePCH.h"
#include "CoreTypes.h"
#include <iostream>
#include <chrono>
#include <iomanip>

namespace Core
{
    void Logger::Log(LogLevel level, StringView message)
    {
        const char* levelStr = "";
        
        switch (level)
        {
            case LogLevel::Trace:   levelStr = "TRACE"; break;
            case LogLevel::Debug:   levelStr = "DEBUG"; break;
            case LogLevel::Info:    levelStr = "INFO "; break;
            case LogLevel::Warning: levelStr = "WARN "; break;
            case LogLevel::Error:   levelStr = "ERROR"; break;
            case LogLevel::Fatal:   levelStr = "FATAL"; break;
        }
        
        auto now = std::chrono::system_clock::now();
        auto time = std::chrono::system_clock::to_time_t(now);
        
        std::cout << "[" << std::put_time(std::localtime(&time), "%H:%M:%S") << "]"
                  << "[" << levelStr << "] "
                  << message << std::endl;
    }
}
