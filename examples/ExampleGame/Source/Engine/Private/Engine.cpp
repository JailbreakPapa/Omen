// Engine Implementation

#include "Engine.h"
#include <chrono>

namespace Engine
{
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
            auto now = std::chrono::high_resolution_clock::now();
            float deltaTime = std::chrono::duration<float>(now - lastTime).count();
            lastTime = now;
            
            OnUpdate(deltaTime);
            OnRender();
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
