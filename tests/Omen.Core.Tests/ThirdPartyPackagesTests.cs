// Omen Build System - Unit Tests
// Copyright (c) WD Studios Corp., Mikael K. Aboagye, and Contributors. All Rights Reserved.

using Omen.Core.Configuration;
using Omen.Core.Rules;

namespace Omen.Core.Tests;

/// <summary>
/// One class on purpose: these tests set the process-wide LY_3RDPARTY_PATH variable, and
/// xUnit only serializes tests within a class.
/// </summary>
public class ThirdPartyPackagesTests : IDisposable
{
    private readonly string _root;

    public ThirdPartyPackagesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "OmenTests", nameof(ThirdPartyPackagesTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LY_3RDPARTY_PATH", null);
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private BuildContext CreateContext(BuildConfiguration configuration = BuildConfiguration.Development) => new()
    {
        Platform = TargetPlatform.Windows,
        Architecture = TargetArchitecture.X64,
        Configuration = configuration,
        ProjectRoot = _root,
        OutputDirectory = Path.Combine(_root, "Binaries"),
        IntermediateDirectory = Path.Combine(_root, "Intermediate")
    };

    private sealed class ConsumerModule(BuildContext context) : ModuleRules(context);

    /// <summary>Writes a package in the real on-disk O3DE shape and points LY_3RDPARTY_PATH at it.</summary>
    private string WritePackage(string packageName, string findFileName, string findContent)
    {
        var storeRoot = Path.Combine(_root, "3p");
        var packageDir = Path.Combine(storeRoot, "packages", packageName);
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, findFileName), findContent);
        Environment.SetEnvironmentVariable("LY_3RDPARTY_PATH", storeRoot);
        return packageDir;
    }

    private void WriteAssociation(string targetName, string packageName)
    {
        var manifestDir = Path.Combine(_root, "cmake", "3rdParty");
        Directory.CreateDirectory(manifestDir);
        File.AppendAllText(Path.Combine(manifestDir, "BuiltInPackages.cmake"),
            $"ly_associate_package(PACKAGE_NAME {packageName} TARGETS {targetName} PACKAGE_HASH 0123abc)\n");
    }

    // The zlib/Lua-shaped standard template, all the constructs the tier-one parser must eat.
    private const string TemplateFind = """
        # include guard
        set(TARGET_WITH_NAMESPACE "3rdParty::Widget")
        if (TARGET ${TARGET_WITH_NAMESPACE})
            return()
        endif()

        set(MY_NAME "Widget")
        set(${MY_NAME}_INCLUDE_DIR ${CMAKE_CURRENT_LIST_DIR}/Widget/include)
        set(${MY_NAME}_LIBS_DIR ${CMAKE_CURRENT_LIST_DIR}/Widget/lib)

        if(${CMAKE_SYSTEM_NAME} STREQUAL "Windows")
            set(${MY_NAME}_LIBRARY ${${MY_NAME}_LIBS_DIR}/$<IF:$<CONFIG:debug>,debug,release>/${CMAKE_STATIC_LIBRARY_PREFIX}widget${CMAKE_STATIC_LIBRARY_SUFFIX})
        else()
            set(${MY_NAME}_LIBRARY ${${MY_NAME}_LIBS_DIR}/libwidget.a)
        endif()

        add_library(${TARGET_WITH_NAMESPACE} STATIC IMPORTED GLOBAL)
        add_library(Widget::Widget ALIAS ${TARGET_WITH_NAMESPACE})

        if (COMMAND ly_target_include_system_directories)
            ly_target_include_system_directories(TARGET ${TARGET_WITH_NAMESPACE} INTERFACE ${${MY_NAME}_INCLUDE_DIR})
        else()
            target_include_directories(${TARGET_WITH_NAMESPACE} SYSTEM INTERFACE ${${MY_NAME}_INCLUDE_DIR})
        endif()

        set_target_properties(${TARGET_WITH_NAMESPACE} PROPERTIES IMPORTED_LOCATION "${${MY_NAME}_LIBRARY}")
        target_compile_definitions(${TARGET_WITH_NAMESPACE} INTERFACE WIDGET_STATIC=1)
        target_link_libraries(${TARGET_WITH_NAMESPACE} INTERFACE ${CMAKE_DL_LIBS} 3rdParty::Gadget)

        if (NOT LY_VERSION_ENGINE_NAME)
            message(STATUS "outside O3DE")
        endif()
        set(${MY_NAME}_FOUND True)
        """;

    [Fact]
    public void FindModuleParser_ParsesTheStandardTemplate()
    {
        var packageDir = WritePackage("Widget-1.0-rev1-windows", "FindWidget.cmake", TemplateFind);

        var targets = new FindModuleParser(packageDir, CreateContext(BuildConfiguration.Debug)).ParsePackage();

        var widget = targets.Should().ContainKey("3rdParty::Widget").WhoseValue;
        widget.IncludeDirectories.Should().ContainSingle()
            .Which.Replace('\\', '/').Should().EndWith("Widget-1.0-rev1-windows/Widget/include");
        widget.Libraries.Should().ContainSingle()
            .Which.Replace('\\', '/').Should().EndWith("Widget/lib/debug/widget.lib");
        widget.Definitions.Should().Contain("WIDGET_STATIC=1");
        widget.Dependencies.Should().ContainSingle().Which.Should().Be("3rdParty::Gadget");
        // ${CMAKE_DL_LIBS} is empty on Windows and must not become a system library.
        widget.SystemLibraries.Should().BeEmpty();
        // The Widget::Widget alias resolves to the same definition.
        targets.Should().ContainKey("Widget::Widget");
    }

    [Fact]
    public void FindModuleParser_ConfigGenexpr_SelectsReleaseForDevelopment()
    {
        // Omen's Development maps to O3DE's profile, which is not debug - the $<IF:$<CONFIG:debug>>
        // above must pick the release path.
        var packageDir = WritePackage("Widget-1.0-rev1-windows", "FindWidget.cmake", TemplateFind);

        var targets = new FindModuleParser(packageDir, CreateContext(BuildConfiguration.Development)).ParsePackage();

        targets["3rdParty::Widget"].Libraries.Should().ContainSingle()
            .Which.Replace('\\', '/').Should().EndWith("Widget/lib/release/widget.lib");
    }

    [Fact]
    public void FindModuleParser_UnsupportedConstruct_ThrowsLoudlyNamingTheOverride()
    {
        var packageDir = WritePackage("Weird-1.0-rev1-windows", "FindWeird.cmake", """
            add_library(3rdParty::Weird STATIC IMPORTED GLOBAL)
            execute_process(COMMAND some-tool)
            """);

        var act = () => new FindModuleParser(packageDir, CreateContext()).ParsePackage();

        act.Should().Throw<FindModuleParseException>()
            .WithMessage("*execute_process*")
            .WithMessage("*hand-write a module*");
    }

    [Fact]
    public void BuiltInPackagesReader_ParsesAssociations_IncludingMultipleTargets()
    {
        var manifestDir = Path.Combine(_root, "cmake", "3rdParty", "Platform", "Windows");
        Directory.CreateDirectory(manifestDir);
        File.WriteAllText(Path.Combine(manifestDir, "BuiltInPackages_windows.cmake"), """
            ly_associate_package(PACKAGE_NAME Lua-5.4.4-rev1-windows       TARGETS Lua           PACKAGE_HASH 8ac85)
            ly_associate_package(PACKAGE_NAME OpenEXR-3.1.3-rev5-windows  TARGETS OpenEXR Imath PACKAGE_HASH bff6d)
            """);

        var associations = BuiltInPackagesReader.Read(_root);

        associations["Lua"].Should().Be("Lua-5.4.4-rev1-windows");
        associations["OpenEXR"].Should().Be("OpenEXR-3.1.3-rev5-windows");
        associations["Imath"].Should().Be("OpenEXR-3.1.3-rev5-windows");
    }

    [Fact]
    public void Resolver_SynthesizesModuleForReferencedPackage_AndChasesCrossPackageDeps()
    {
        var context = CreateContext();
        WritePackage("Widget-1.0-rev1-windows", "FindWidget.cmake", TemplateFind);
        WritePackage("Gadget-2.0-rev1-windows", "FindGadget.cmake", """
            add_library(3rdParty::Gadget STATIC IMPORTED GLOBAL)
            ly_target_include_system_directories(TARGET 3rdParty::Gadget INTERFACE ${CMAKE_CURRENT_LIST_DIR}/Gadget/include)
            """);
        WriteAssociation("Widget", "Widget-1.0-rev1-windows");
        WriteAssociation("Gadget", "Gadget-2.0-rev1-windows");

        var consumer = new ConsumerModule(context);
        consumer.ThirdPartyDependencies.Add("3rdParty::Widget");

        var synthesized = ThirdPartyPackageResolver.Synthesize(context, [consumer]);

        synthesized.Should().HaveCount(2);
        var widget = synthesized.Single(m => m.Name == "3rdParty::Widget");
        widget.Type.Should().Be(ModuleType.ThirdParty);
        widget.BinaryType.Should().Be(TargetType.HeaderOnly);
        widget.PublicLibraries.Should().ContainSingle(l => l.EndsWith("widget.lib"));
        // Widget's INTERFACE link to 3rdParty::Gadget pulled Gadget in transitively.
        synthesized.Should().Contain(m => m.Name == "3rdParty::Gadget");
    }

    [Fact]
    public void Resolver_HandWrittenModuleWithThePackageName_WinsOverParsing()
    {
        // The override tier: a module already named "3rdParty::Widget" means the package is
        // hand-described; the resolver must not synthesize a duplicate (which would be a
        // duplicate-module-name hard error downstream).
        var context = CreateContext();
        WritePackage("Widget-1.0-rev1-windows", "FindWidget.cmake", TemplateFind);
        WriteAssociation("Widget", "Widget-1.0-rev1-windows");

        var consumer = new ConsumerModule(context);
        consumer.ThirdPartyDependencies.Add("3rdParty::Widget");
        var handWritten = new OverrideModule(context);

        var synthesized = ThirdPartyPackageResolver.Synthesize(context, [consumer, handWritten]);

        synthesized.Should().BeEmpty();
    }

    private sealed class OverrideModule(BuildContext context) : ModuleRules(context, "3rdParty::Widget");

    /// <summary>Writes a minimal vcpkg installed tree (manifest-mode vcpkg_installed/ under the
    /// project root, which FindInstalledRoot prefers over VCPKG_ROOT - keeps the test hermetic
    /// on machines with a real vcpkg).</summary>
    private string WriteVcpkgTree()
    {
        var installed = Path.Combine(_root, "vcpkg_installed");
        var infoDir = Path.Combine(installed, "vcpkg", "info");
        Directory.CreateDirectory(infoDir);
        Directory.CreateDirectory(Path.Combine(installed, "x64-windows", "include"));

        File.WriteAllText(Path.Combine(infoDir, "zlib_1.3.1_x64-windows.list"), """
            x64-windows/bin/zlib1.dll
            x64-windows/debug/bin/zlibd1.dll
            x64-windows/debug/lib/zlibd.lib
            x64-windows/include/
            x64-windows/include/zlib.h
            x64-windows/lib/zlib.lib
            """);
        File.WriteAllText(Path.Combine(infoDir, "libpng_1.6.43_x64-windows.list"), """
            x64-windows/lib/libpng16.lib
            x64-windows/debug/lib/libpng16d.lib
            """);
        File.WriteAllText(Path.Combine(installed, "vcpkg", "status"), """
            Package: zlib
            Version: 1.3.1
            Depends: vcpkg-cmake
            Architecture: x64-windows
            Status: install ok installed

            Package: libpng
            Version: 1.6.43
            Depends: vcpkg-cmake, zlib
            Architecture: x64-windows
            Status: install ok installed
            """);
        return installed;
    }

    [Fact]
    public void Vcpkg_ResolvesPortFromListFile_PerConfiguration()
    {
        var installed = WriteVcpkgTree();
        var context = CreateContext(BuildConfiguration.Development);
        var depends = VcpkgResolver.ReadStatusDependencies(installed);

        var release = VcpkgResolver.Resolve("Vcpkg::zlib", context, installed, depends)!;
        release.Libraries.Should().ContainSingle().Which.Should().EndWith("zlib.lib");
        release.RuntimeFiles.Should().ContainSingle().Which.Should().EndWith("zlib1.dll");
        release.IncludeDirectories.Should().ContainSingle()
            .Which.Should().Be(Path.Combine(installed, "x64-windows", "include"));
        // vcpkg-cmake is a build-time helper port, never a link dependency.
        release.Dependencies.Should().BeEmpty();

        var debug = VcpkgResolver.Resolve("Vcpkg::zlib", CreateContext(BuildConfiguration.Debug), installed, depends)!;
        debug.Libraries.Should().ContainSingle().Which.Should().EndWith("zlibd.lib");
        debug.RuntimeFiles.Should().ContainSingle().Which.Should().EndWith("zlibd1.dll");
    }

    [Fact]
    public void Vcpkg_Synthesize_ChasesPortDependenciesTransitively()
    {
        WriteVcpkgTree();
        var context = CreateContext();
        var consumer = new ConsumerModule(context);
        consumer.ThirdPartyDependencies.Add("Vcpkg::libpng");

        var synthesized = ThirdPartyPackageResolver.Synthesize(context, [consumer]);

        synthesized.Select(m => m.Name).Should().BeEquivalentTo("Vcpkg::libpng", "Vcpkg::zlib");
        var libpng = synthesized.Single(m => m.Name == "Vcpkg::libpng");
        libpng.Type.Should().Be(ModuleType.ThirdParty);
        libpng.ThirdPartyDependencies.Should().Contain("Vcpkg::zlib");
        libpng.PublicLibraries.Should().ContainSingle().Which.Should().EndWith("libpng16.lib");
    }

    [Fact]
    public void Vcpkg_PortNotInstalled_SkipsQuietly()
    {
        WriteVcpkgTree();
        var context = CreateContext();
        var consumer = new ConsumerModule(context);
        consumer.ThirdPartyDependencies.Add("Vcpkg::not-a-port");

        ThirdPartyPackageResolver.Synthesize(context, [consumer]).Should().BeEmpty();
    }

    [Fact]
    public void Resolver_MissingAssociationOrPackage_SkipsQuietly()
    {
        // No manifest, no package on disk: the name stays unresolved and the existing
        // unresolved-dependency warning is the diagnostic - no throw, no module.
        var context = CreateContext();
        var consumer = new ConsumerModule(context);
        consumer.ThirdPartyDependencies.Add("3rdParty::NotDownloaded");
        consumer.ThirdPartyDependencies.Add("3rdParty::Qt::Core"); // Qt never resolves here

        var synthesized = ThirdPartyPackageResolver.Synthesize(context, [consumer]);

        synthesized.Should().BeEmpty();
    }
}
