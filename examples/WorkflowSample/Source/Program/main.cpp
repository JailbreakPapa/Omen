#include "config.h"
#include <cstdlib>
#include <iostream>
#include <string_view>

int main(int argc, char** argv)
{
    if (argc > 1)
    {
        const std::string_view mode = argv[1];
        if (mode == "--fail") return 7;
        if (mode == "--skip") return 77;
        if (mode == "--argument") return argc == 3 && std::string_view(argv[2]) == "value with spaces" ? 0 : 2;
        if (mode == "--environment")
        {
            const char* value = std::getenv("OMEN_SAMPLE_MESSAGE");
            return value && std::string_view(value) == "hello from test" ? 0 : 3;
        }
    }
    std::cout << "WorkflowSample " << WORKFLOW_SAMPLE_VERSION << '\n';
#if ENABLE_GREETING
    std::cout << "Configured greeting enabled\n";
#endif
    return 0;
}
