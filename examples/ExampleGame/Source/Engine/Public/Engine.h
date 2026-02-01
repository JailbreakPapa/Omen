// Engine Public Header

#pragma once

#include "CoreTypes.h"

namespace Engine
{
    class Application
    {
    public:
        Application();
        virtual ~Application();
        
        void Run();
        void RequestExit();
        
        virtual void OnInit() {}
        virtual void OnUpdate(float deltaTime) {}
        virtual void OnRender() {}
        virtual void OnShutdown() {}
        
        bool IsRunning() const { return m_Running; }
        
    protected:
        bool m_Running = true;
    };
    
    // Create application - defined by game
    Application* CreateApplication();
}

#define DEFINE_APPLICATION(AppClass) \
    Engine::Application* Engine::CreateApplication() { return new AppClass(); }
