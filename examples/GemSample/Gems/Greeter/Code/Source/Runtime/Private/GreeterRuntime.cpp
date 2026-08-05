#include "GreeterRuntime.h"
#include "Greeter.h"

extern "C" GREETER_API const char* Greeter_GetGreeting()
{
    return Greeter::GetGreeting();
}
