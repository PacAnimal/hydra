# Building Hydra

Requirements:

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- macOS builds: a Mac with the Xcode Command Line Tools (`xcode-select --install`), which compile the Swift helper
  bundled with Hydra. Windows and Linux builds can be made on any OS.

```bash
git clone https://github.com/PacAnimal/hydra.git
cd hydra
dotnet build Hydra.sln
dotnet test Hydra.sln
```

Publish a self-contained single-file executable:

```bash
dotnet publish Hydra --runtime osx-arm64   --self-contained   # macOS Apple Silicon
dotnet publish Hydra --runtime osx-x64     --self-contained   # macOS Intel
dotnet publish Hydra --runtime win-x64     --self-contained   # Windows x64
dotnet publish Hydra --runtime linux-x64   --self-contained   # Linux x64
dotnet publish Hydra --runtime linux-arm64 --self-contained   # Linux arm64 (e.g. Raspberry Pi)
```

Output lands in `Hydra/bin/Release/net10.0/<rid>/publish/`. Both macOS builds run on macOS 13 or later.

`dotnet test` skips the Linux X11 and Windows-only tests on other platforms. `./run-tests.sh linux` runs the X11
tests under Xvfb in Docker.
