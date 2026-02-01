// ExampleGame - Main Game Application

#include "Engine.h"

class ExampleGame : public Engine::Application
{
public:
    void OnInit() override
    {
        CORE_LOG_INFO("ExampleGame initialized");
    }
    
    void OnUpdate(float deltaTime) override
    {
        m_ElapsedTime += deltaTime;
        
        // Exit after 5 seconds for demo purposes
        if (m_ElapsedTime > 5.0f)
        {
            RequestExit();
        }
    }
    
    void OnRender() override
    {
        // Render game
    }
    
    void OnShutdown() override
    {
        CORE_LOG_INFO("ExampleGame shutting down");
    }
    
private:
    float m_ElapsedTime = 0.0f;
};

DEFINE_APPLICATION(ExampleGame)
