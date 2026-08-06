#include "Greeter.h"

namespace Greeter
{
    const char* GetGreeting()
    {
#ifdef VERBOSE_GREETING
        return "Hello from the Greeter gem's Static flavor! (VERBOSE_GREETING build option is enabled)";
#else
        return "Hello from the Greeter gem's Static flavor!";
#endif
    }
}
