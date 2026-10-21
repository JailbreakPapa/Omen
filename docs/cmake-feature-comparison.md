# CMake feature comparison and native Omen workflows

This audit compares the recovered Omen source with CMake's documented build/configure/test/install
workflows. It records implemented behavior, rather than equating an API name or an unfinished
prototype with feature parity. The current implementation adds the common workflow gaps below;
it does **not** reproduce every CMake command, package, generator, language or installer backend.

## Coverage

| Capability | Current Omen behavior | Coverage |
| --- | --- | --- |
| C/C++ executable, static/shared and interface libraries | TargetRules, ModuleRules, independent module binaries, header-only modules, object aggregation | Existing; covered by action-graph tests |
| Transitive usage requirements | Public/private dependencies, includes, definitions, external headers and libraries; shared compile-environment resolver | Existing |
| Platform/configuration and custom dimensions | SupportedTargets matrices, per-row module gating and custom enum dimensions | Existing |
| Incremental builds, custom commands, PCH and unity builds | Action graphs, declared code-generation inputs/outputs, content digests, separate C/C++ unity objects | Existing |
| Parallel/distributed execution and build caching | Local/hybrid executors, shared build server, coordinator/agent source | Existing; distributed deployment is not established by this audit |
| Configure options | Typed declarations, persistent cache, CLI/GUI editing; configuration/platform/architecture selectors now reach option discovery | Extended |
| Presets and ordered workflows | OmenPresets.json plus OmenUserPresets.json; includes, multiple inheritance, hidden bases, host restrictions, environment/macro expansion, CLI overrides, stop-on-failure workflows | Added; native schema, not a CMakePresets.json interpreter |
| Independent source/build trees | Configuration-specific Intermediate/Binaries directories, target output overrides and --output-dir for consuming existing artifacts | Partial; arbitrary per-preset binaryDir and isolated option caches are not implemented |
| Configuration headers and copied configuration files | Project/target ConfigureFiles, @VAR@ and ${VAR}, #cmakedefine/#omendefine and 01 forms, @ONLY, strict variables, quote escaping, newline control, binary CopyOnly, write-if-changed | Added; generated during configure and before a build |
| Process-test registration/execution | Project/target Tests, name/label filters, bounded jobs, serial tests, prerequisite ordering, timeouts, expected/skip exit codes, output regex expectations, disabled tests, list-only and JUnit | Added; a useful CTest-style subset |
| Installation | Explicit project/target InstallRules, files/directory contents, patterns/exclusions, rename, optional files, components, prefix override, dry-run and SHA-256 manifest | Added; no automatic runtime dependency/RPATH analysis |
| Packaging | Sorted ZIP entries, fixed archive timestamps, payload hashes, atomic archive replacement and relocatable layout | Added; ZIP only, not all CPack generators |
| Capability checks | omen check compiles a source file or header, optionally links/runs, supports includes/definitions/flags/libraries, timeouts and JSON reports | Added as CLI/API checks; no automatic configure-time check-result cache |
| Target discovery | omen targets lists target names and build matrices without building | Added; not the CMake File API |
| IDE files | VS2019/2022, VS2026 SLNX plus compatibility SLN, VS Code, Rider, and CMake export | Existing/restored; Visual Studio selects Windows matrix rows; no direct Xcode or Ninja project exporter |
| Dependency acquisition/imports | Git/archive content fetching, installed vcpkg ports, Qt discovery and a limited O3DE package parser | Existing partial coverage; not general find_package compatibility |
| Installed library exports | Explicit files can be packaged, but imported-target export/version-config generation is absent | Remaining gap |
| Additional compiler languages/modules | Native C/C++ focus; C# IDE model exists | CUDA/Fortran/Objective-C/ISPC and C++ module dependency scanning are not complete |

Official comparison references: [build model](https://cmake.org/cmake/help/latest/manual/cmake-buildsystem.7.html),
[presets](https://cmake.org/cmake/help/latest/manual/cmake-presets.7.html),
[configure_file](https://cmake.org/cmake/help/latest/command/configure_file.html),
[CTest](https://cmake.org/cmake/help/latest/manual/ctest.1.html),
[installation](https://cmake.org/cmake/help/latest/command/install.html),
[package discovery/exports](https://cmake.org/cmake/help/latest/manual/cmake-packages.7.html), and
[File API](https://cmake.org/cmake/help/latest/manual/cmake-file-api.7.html).

## Presets

Use `omen --list-presets` to list visible presets. `OmenUserPresets.json` is optional and should
be ignored by version control. Both files use schema version 1; an IDE schema is provided at
`schemas/omen-presets.schema.json`.

```json
{
  "version": 1,
  "presets": [
    {
      "name": "base",
      "hidden": true,
      "commands": {
        "configure": ["MyApp", "--set", "FEATURE=true"],
        "build": ["MyApp", "-c", "Development", "--jobs", "4"],
        "test": ["MyApp", "-c", "Development", "--junit", "${sourceDir}/Intermediate/tests.xml"],
        "package": ["MyApp", "--output", "${sourceDir}/Packages/MyApp.zip"],
        "generate.project": ["--ide", "vs2026"]
      }
    },
    { "name": "dev", "inherits": ["base"], "environment": { "MY_TOOL_PATH": "${sourceDir}/Tools" } }
  ],
  "workflows": [
    { "name": "ci", "steps": [["configure", "--preset", "dev"], ["build", "--preset", "dev"], ["test", "--preset", "dev"], ["package", "--preset", "dev"]] }
  ]
}
```

Run `omen build --preset dev`, or `omen workflow ci`. Explicit scalar arguments override preset
defaults, including aliases such as `-c` versus `--configuration`. Explicit `--set NAME=VALUE`
or `--dim Name=Value` assignments replace the corresponding preset assignment and retain other
assignments. Command arrays contain argument tokens, so paths with spaces stay one argument.
An inherited command array is replaced as a whole when the child supplies that command.

Includes are relative to their containing file. Earlier parents take precedence in multiple
inheritance. Supported macros are `${sourceDir}`, `${fileDir}`, `${presetName}`,
`${hostSystemName}`, `${dollar}`, `$env{NAME}` and `$penv{NAME}`. `fileDir` belongs to the file
where an inherited value was declared. Environment null removes a variable; preset environments
are restored after each invocation, including failed workflow steps. Includes, inheritance and
environment references reject cycles. Hidden presets can be inherited but not invoked directly.

## Tests

Register tests on a project or target:

```csharp
Tests.Add(new TestDefinition
{
    Name = "MyApp.self", Command = "$(TargetFile)",
    Arguments = ["--self-test", "argument with spaces"],
    Labels = ["smoke"], TimeoutSeconds = 30
});
```

`omen test MyApp -c Development --label smoke --jobs 4 --junit results.xml` runs existing binaries.
Add `--build` to build selected targets first. `--list` lists tests without starting processes;
`--output-dir` selects existing artifacts and cannot be combined with `--build`.
`--filter` and `--exclude` match names, and `--label`/`--exclude-label` match labels using regex.
No selected tests is an error unless `--allow-no-tests` is set. Test names must be globally unique.

Commands execute directly with an argument list; shell syntax requires an explicit shell command.
Environment and working-directory overrides apply to the child process. `DependsOn` adds and orders
prerequisites, including when a filter selects only the dependent; a failed/disabled/skipped prerequisite
skips its dependent. `RunSerial` prevents overlap with other tests. Exit-code and output-pattern
checks combine; a timeout or process-start error fails even when a nonzero exit was expected.
Cancellation and timeouts terminate child process trees. Output capture is bounded to 1 MiB per stream.

The GUI exposes **Build > Run Tests**, with output, cancellation, success/failure state and a JUnit
report under `Intermediate/TestResults`. Tests, installation and packaging use one selected build row;
when the Build Matrix is active, choose one value for each dimension first. Advanced CTest fixtures,
resource groups, retries, coverage and dashboard integration are not implemented.

## Configuration templates

```csharp
ConfigureFiles.Add(new ConfiguredFile
{
    Input = "config.h.in", Output = "$(IntermediateDir)/Generated/config.h",
    Variables = new() { ["API_VERSION"] = "3" },
    NewlineStyle = ConfiguredNewlineStyle.LF, Strict = true
});
```

Variables include declared/cached options, ProjectName, ProjectVersion, Platform, Architecture,
Configuration and, for target templates, TargetName. Explicit Variables override these values.
Substituted values are literal and are not recursively expanded. Missing substitutions become empty
unless Strict is true. Boolean defines recognize common CMake false values. CopyOnly preserves binary
bytes and cannot normalize newlines. Input and output must differ; unchanged output keeps its timestamp.
Add the generated directory to the consuming module's include paths. Configuration-file permissions
and automatic compiler-driven feature-test assignments are not implemented.

## Install and package

```csharp
InstallRules.Add(new InstallRule { Source = "$(TargetFile)", Destination = "bin", Component = "Runtime" });
InstallRules.Add(new InstallRule
{
    Source = "include", Destination = "include/MyApp", Directory = true,
    Patterns = ["*.h", "*.hpp"], Component = "Development"
});
```

`omen install MyApp --prefix path --component Runtime` stages existing outputs. `omen package MyApp
--output MyApp.zip` uses the same rules. Neither implicitly builds. Both accept `--dry-run` and
`--output-dir` to select an existing artifact directory. Directory rules copy contents with relative
subdirectory layout; patterns match source-relative paths. Optional missing inputs are ignored.
Conflicting destinations, missing required inputs and destination traversal are rejected before copying.
Installation rejects existing destination links, and directory copies skip reparse points.

The JSON manifest records payload paths, components, lengths and SHA-256 values. ZIP payloads and the
manifest use relative paths and fixed timestamps; identical inputs produce identical archive bytes.
Cancellation preserves an existing archive. Install copies are per-file atomic but the entire prefix
is not a transactional update. Native installer formats, symlink-preserving packages, strip/sign/RPATH
processing, package version selection and imported-target exports remain separate gaps.

The GUI exposes **Project > Install Project...**, **Project > Package Project (.zip)...**, and
**Tools > Check C++ Toolchain**, with the same service implementations as the CLI.

## Verification and boundaries

`examples/WorkflowSample` contains one C++20 application with both native Omen rules/presets and an
independent CMakeLists.txt reference. The Omen workflow compiles/links/runs a header capability check,
renders a configuration header, builds, runs tests, stages three components, and packages them.
The reference CMake/CTest/CPack workflow builds the same application and has the same six tests and
three payload files. Expected nonzero exit and skipped/disabled test cases are included.

Validation checks filter/argument overrides, unchanged incremental objects, package determinism,
manifest hashes, relocation and runtime output, plus configuration headers and README contents against
the CMake installation. Failure cases include missing headers, probe timeout, prerequisite cycles,
invalid presets, empty test selections and install destinations that traverse a junction.

The reference uses CMake 4.3.1 with Ninja and VS2026 MSVC. The Visual Studio CMake generator initially
failed because this machine's global user properties inject a missing retired NightFox header;
the reference switched to Ninja with an isolated VS developer environment. No user property files
were changed. No performance comparison or complete CMake compatibility is claimed.

Remaining priorities are independent preset build trees/caches, general package discovery/exports, an IDE-facing JSON code model, broader
compiler-language/module support, richer test scheduling and additional package formats. The restored
GUI's pre-existing OmenNet launch/cache placeholders are unrelated to the CMake workflow additions.

The completed solution suite passes 575 tests (403 Core, 149 Executors, 23 GUI). The real LunaEngine
VS2026 generation also passes: 146 projects, 75 combinations and parser-verified SLNX/SLN mappings.
Visual Studio export excludes non-Windows matrix rows before evaluating their platform-specific
rules; it does not implement remote Linux Visual Studio projects.
