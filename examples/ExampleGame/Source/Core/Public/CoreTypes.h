// Core Types and Definitions

#pragma once

#include "CorePCH.h"

namespace Core
{
    // Smart pointer aliases
    template<typename T>
    using UniquePtr = std::unique_ptr<T>;
    
    template<typename T>
    using SharedPtr = std::shared_ptr<T>;
    
    template<typename T>
    using WeakPtr = std::weak_ptr<T>;
    
    // String types
    using String = std::string;
    using StringView = std::string_view;
    using WString = std::wstring;
    
    // Container aliases
    template<typename T>
    using Array = std::vector<T>;
    
    template<typename K, typename V>
    using HashMap = std::unordered_map<K, V>;
    
    template<typename T>
    using HashSet = std::unordered_set<T>;
    
    // Result type for error handling
    template<typename T, typename E = String>
    class Result
    {
    public:
        static Result Ok(T value) { return Result(std::move(value)); }
        static Result Err(E error) { return Result(std::move(error), false); }
        
        bool IsOk() const { return m_IsOk; }
        bool IsErr() const { return !m_IsOk; }
        
        T& Value() { return std::get<T>(m_Data); }
        const T& Value() const { return std::get<T>(m_Data); }
        
        E& Error() { return std::get<E>(m_Data); }
        const E& Error() const { return std::get<E>(m_Data); }
        
    private:
        Result(T value) : m_Data(std::move(value)), m_IsOk(true) {}
        Result(E error, bool) : m_Data(std::move(error)), m_IsOk(false) {}
        
        std::variant<T, E> m_Data;
        bool m_IsOk;
    };
    
    // Logging
    enum class LogLevel
    {
        Trace,
        Debug,
        Info,
        Warning,
        Error,
        Fatal
    };
    
    class Logger
    {
    public:
        static void Log(LogLevel level, StringView message);
        static void Trace(StringView message) { Log(LogLevel::Trace, message); }
        static void Debug(StringView message) { Log(LogLevel::Debug, message); }
        static void Info(StringView message) { Log(LogLevel::Info, message); }
        static void Warning(StringView message) { Log(LogLevel::Warning, message); }
        static void Error(StringView message) { Log(LogLevel::Error, message); }
        static void Fatal(StringView message) { Log(LogLevel::Fatal, message); }
    };
}

// Macros
#define CORE_LOG_TRACE(msg) Core::Logger::Trace(msg)
#define CORE_LOG_DEBUG(msg) Core::Logger::Debug(msg)
#define CORE_LOG_INFO(msg) Core::Logger::Info(msg)
#define CORE_LOG_WARNING(msg) Core::Logger::Warning(msg)
#define CORE_LOG_ERROR(msg) Core::Logger::Error(msg)
#define CORE_LOG_FATAL(msg) Core::Logger::Fatal(msg)
