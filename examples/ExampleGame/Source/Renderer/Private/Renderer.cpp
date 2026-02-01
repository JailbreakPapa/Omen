// Renderer Implementation

#include "Renderer.h"

namespace Renderer
{
    class NullRenderDevice : public RenderDevice
    {
    public:
        void BeginFrame() override {}
        void EndFrame() override {}
        void Present() override {}
        GraphicsAPI GetAPI() const override { return GraphicsAPI::None; }
    };
    
    Core::UniquePtr<RenderDevice> RenderDevice::Create(const RenderConfig& config)
    {
        CORE_LOG_INFO("Creating render device...");
        
        // For now, return a null device
        // Real implementation would create D3D12/Vulkan/Metal device
        return std::make_unique<NullRenderDevice>();
    }
}
