// Renderer Public Header

#pragma once

#include "CoreTypes.h"

namespace Renderer
{
    enum class GraphicsAPI
    {
        None,
        Direct3D12,
        Vulkan,
        Metal
    };
    
    struct RenderConfig
    {
        int Width = 1920;
        int Height = 1080;
        bool Fullscreen = false;
        bool VSync = true;
        GraphicsAPI API = GraphicsAPI::None;
    };
    
    class RenderDevice
    {
    public:
        static Core::UniquePtr<RenderDevice> Create(const RenderConfig& config);
        
        virtual ~RenderDevice() = default;
        
        virtual void BeginFrame() = 0;
        virtual void EndFrame() = 0;
        virtual void Present() = 0;
        
        virtual GraphicsAPI GetAPI() const = 0;
    };
}
