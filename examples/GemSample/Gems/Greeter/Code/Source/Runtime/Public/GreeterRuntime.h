#pragma once

#if defined(GREETER_RUNTIME_EXPORTS)
    #define GREETER_API __declspec(dllexport)
#else
    #define GREETER_API __declspec(dllimport)
#endif

extern "C" GREETER_API const char* Greeter_GetGreeting();
