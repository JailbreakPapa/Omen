# Omen
![Logo](images/omen-horizontal.png)
**Omen** is a fast, scalable, and highly customizable C/C++ build system inspired by Unreal Engine's Build Tool (UBT). It features pure C# rule definitions, a distributed build system (OmenNet), a GUI for configuring & generating projects.

## Features

- **Pure C# Build Rules**: Define your modules and targets using C# classes (`*.module.cs`, `*.target.cs`)
- **Cross-Platform Support**: Build for Windows, Linux, FreeBSD, Android, and iOS
- **Distributed Builds**: Scale your builds across multiple machines with OmenNet
- **Build Optimizations**: Unity builds, precompiled headers, and action caching
- **Modern CLI**: Rich command-line interface with Spectre.Console
- **Incremental Builds**: Content-addressed caching for fast rebuilds

## Quick Start

### Building Omen

```powershell
# Clone the repository
git clone https://github.com/your-org/omen.git
cd omen

# Build the solution
dotnet build Omen.sln

# Install as a global tool
dotnet pack src/Omen.CLI -o ./artifacts
dotnet tool install --global --add-source ./artifacts Omen.CLI
```

### Creating a New Project

```powershell
# Generate a new target
omen generate target MyGame --type Game

# Generate a new module
omen generate module Core --type Runtime

# Build the project
omen build
```

### Example Target File (`MyGame.target.cs`)

```csharp
using Omen.Core.Configuration;
using Omen.Core.Rules;

public class MyGameTarget : TargetRules
{
    public MyGameTarget()
    {
        Name = "MyGame";
        Type = TargetType.Game;
        
        SupportedPlatforms.Add(TargetPlatform.Windows);
        SupportedPlatforms.Add(TargetPlatform.Linux);
        
        bUsePCH = true;
        bUseUnityBuilds = true;
        
        ExtraModules.Add("Core");
        ExtraModules.Add("Game");
    }
}
```

### Example Module File (`Core.module.cs`)

```csharp
using Omen.Core.Configuration;
using Omen.Core.Rules;

public class CoreModule : ModuleRules
{
    public CoreModule()
    {
        Name = "Core";
        Type = ModuleType.Runtime;
        
        PublicIncludePaths.Add("Public");
        PrivateIncludePaths.Add("Private");
        
        PublicDependencyModules.Add("Engine");
        
        CppStandard = CppStandard.Cpp20;
    }
}
```

## CLI Commands

| Command | Description |
|---------|-------------|
| `omen build [target]` | Build a target |
| `omen rebuild [target]` | Clean and rebuild |
| `omen clean [target]` | Clean build outputs |
| `omen generate project` | Generate IDE project files |
| `omen generate module <name>` | Create a new module |
| `omen generate target <name>` | Create a new target |
| `omen agent start` | Start a build agent |
| `omen coordinator start` | Start the coordinator |
| `omen info` | Show system information |

### Build Options

```powershell
omen build MyGame --platform Windows --configuration Shipping --jobs 16
omen build --distribute --coordinator 192.168.1.100:5051
```

## Architecture

```
Omen/
├── src/
│   ├── Omen.Core/           # Core build logic
│   │   ├── Configuration/   # Enums, BuildContext
│   │   ├── Rules/           # ModuleRules, TargetRules, RuleCompiler
│   │   ├── Graph/           # Action graph, DAG processing
│   │   └── Interfaces/      # Abstractions for toolchains, caching
│   │
│   ├── Omen.Platforms/      # Platform-specific toolchains
│   │   ├── Windows/         # MSVC toolchain
│   │   ├── Unix/            # Clang for Linux/FreeBSD
│   │   ├── Android/         # NDK toolchain
│   │   └── Apple/           # iOS/macOS toolchain
│   │
│   ├── Omen.Distributed/    # OmenNet distributed build system
│   │   ├── Protos/          # gRPC service definitions
│   │   ├── Coordinator/     # Centralized job scheduler
│   │   ├── CAS/             # Content-addressable storage
│   │   └── Cache/           # Action result caching
│   │
│   ├── Omen.Executors/      # Build execution strategies
│   │   ├── LocalExecutor    # Single-threaded execution
│   │   ├── ParallelExecutor # Multi-threaded local execution
│   │   └── HybridExecutor   # Local + distributed execution
│   │
│   ├── Omen.Optimizations/  # Build optimizations
│   │   ├── UnityBuildGenerator
│   │   ├── PCHManager
│   │   └── IncludeAnalyzer
│   │
│   ├── Omen.CLI/            # Command-line interface
│   │
│   └── Omen.UI/             # WPF graphical interface
│
└── examples/
    └── ExampleGame/         # Example project
```

## Distributed Builds (OmenNet)

OmenNet enables distributing build actions across multiple machines for faster builds.

### Starting the Coordinator

```powershell
# Start on the main machine
omen coordinator start --port 5051

# With Redis for state persistence
omen coordinator start --redis localhost:6379
```

### Starting Agents

```powershell
# On each build machine
omen agent start --coordinator 192.168.1.100:5051 --jobs 16
```

### Building with Distribution

```powershell
omen build MyGame --distribute --coordinator 192.168.1.100:5051
```

## Configuration

### Module Properties

| Property | Description |
|----------|-------------|
| `Name` | Module name |
| `Type` | ModuleType (Runtime, Developer, Editor, ThirdParty) |
| `PCHUsage` | Precompiled header mode |
| `CppStandard` | C++ standard (Cpp17, Cpp20, Cpp23) |
| `PublicIncludePaths` | Public include directories |
| `PrivateIncludePaths` | Private include directories |
| `PublicDependencyModules` | Public module dependencies |
| `PrivateDependencyModules` | Private module dependencies |
| `PublicDefinitions` | Public preprocessor defines |
| `PublicSystemLibraries` | System libraries to link |

### Target Properties

| Property | Description |
|----------|-------------|
| `Name` | Target name |
| `Type` | TargetType (Game, Editor, Client, Server, Program) |
| `SupportedPlatforms` | Platforms to build for |
| `SupportedConfigurations` | Build configurations |
| `bUsePCH` | Enable precompiled headers |
| `bUseUnityBuilds` | Enable unity builds |
| `bUseLTO` | Enable link-time optimization |
| `ExtraModules` | Modules to include |

## Build Configurations

- **Debug**: Full debugging, no optimizations
- **Development**: Debugging + optimizations
- **Shipping**: Maximum optimizations, no debugging

## Requirements

- .NET 8.0 SDK
- For Windows builds: Visual Studio 2022 with C++ workload
- For Linux builds: Clang 15+
- For Android builds: Android NDK r25+
- For iOS builds: Xcode 15+ (macOS only)

## License

MIT License - see LICENSE file for details.

## Contributing

Contributions are welcome! Please read our contributing guidelines before submitting PRs.
