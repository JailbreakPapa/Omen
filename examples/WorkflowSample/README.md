# Omen workflow sample

This C++20 example demonstrates native Omen presets, configuration headers, process tests,
component installation and reproducible ZIP packaging. Use the newly built Omen CLI, then run:

```powershell
omen --list-presets
omen workflow ci
```

Or run the steps individually:

```powershell
omen check --header vector --link --run
omen configure --preset dev
omen build --preset dev
omen test --preset dev
omen test --preset dev --label smoke
omen install --preset dev --component Runtime
omen package --preset dev
omen generate project --preset dev
```

Tests include argument boundaries, a child-only environment variable, an expected nonzero exit,
a skipped exit code, and a disabled test. The JUnit report is written under `Intermediate/TestResults`.
The install prefix defaults to this example's `Install` directory. The ZIP in `Packages` contains
the executable, generated header, this README and a SHA-256 install manifest. It can be extracted
under another directory and run from there.

`ENABLE_GREETING` demonstrates `#cmakedefine01`; `#omendefine` and `#omendefine01` templates are
also accepted. Unchanged configuration headers keep their timestamps. Local overrides belong in
`OmenUserPresets.json` and should be ignored by source control.

The accompanying `CMakeLists.txt` provides the same native application, six tests and three
install components as a reference comparison. It is independent of Omen's CMake exporter.
