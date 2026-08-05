// ExampleGame - Main Game Application

#include "Engine.h"
#include "Renderer.h"

// Game is the only module that depends on Core, Engine, AND Renderer - it owns the
// render device rather than Engine, so the module layering stays Core -> Engine ->
// Renderer -> Game and Engine never has to know a renderer exists.
class ExampleGame : public Engine::Application
{
public:
    void OnInit() override
    {
        Renderer::RenderConfig config;
        m_RenderDevice = Renderer::RenderDevice::Create(config);

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
        m_RenderDevice->BeginFrame();
        m_RenderDevice->EndFrame();
        m_RenderDevice->Present();

        ++m_FrameCount;
        if (m_FrameCount % 60 == 0)
        {
            CORE_LOG_INFO("Rendered " + std::to_string(m_FrameCount) + " frames");
        }
    }

    void OnShutdown() override
    {
        m_RenderDevice.reset();
        CORE_LOG_INFO("ExampleGame shutting down");
    }

private:
    Core::UniquePtr<Renderer::RenderDevice> m_RenderDevice;
    float m_ElapsedTime = 0.0f;
    int m_FrameCount = 0;
};

DEFINE_APPLICATION(ExampleGame)
