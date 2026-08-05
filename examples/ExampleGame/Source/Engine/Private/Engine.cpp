// Engine Implementation

#include "Engine.h"
#include <chrono>
#include <thread>

namespace Engine
{
    namespace
    {
        constexpr float kTargetFrameSeconds = 1.0f / 60.0f;
    }

    Application::Application()
    {
        CORE_LOG_INFO("Application created");
    }

    Application::~Application()
    {
        CORE_LOG_INFO("Application destroyed");
    }

    void Application::Run()
    {
        CORE_LOG_INFO("Application starting...");

        OnInit();

        auto lastTime = std::chrono::high_resolution_clock::now();

        while (m_Running)
        {
            auto frameStart = std::chrono::high_resolution_clock::now();
            float deltaTime = std::chrono::duration<float>(frameStart - lastTime).count();
            lastTime = frameStart;

            OnUpdate(deltaTime);
            OnRender();

            // Cap the loop to a sane frame rate rather than spinning a core at 100% -
            // this is a demo app, not a real-time engine with vsync/present-driven pacing.
            auto frameEnd = std::chrono::high_resolution_clock::now();
            float frameSeconds = std::chrono::duration<float>(frameEnd - frameStart).count();
            if (frameSeconds < kTargetFrameSeconds)
            {
                std::this_thread::sleep_for(std::chrono::duration<float>(kTargetFrameSeconds - frameSeconds));
            }
        }

        OnShutdown();

        CORE_LOG_INFO("Application exiting");
    }
    
    void Application::RequestExit()
    {
        m_Running = false;
    }
}

// Entry point
int main(int argc, char** argv)
{
    auto app = Engine::CreateApplication();
    app->Run();
    delete app;
    return 0;
}
