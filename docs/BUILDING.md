# Building Hydra

Requirements:

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- macOS builds: a Mac with the Xcode Command Line Tools (`xcode-select --install`), which compile the Swift helper
  bundled with Hydra. Windows and Linux builds can be made on any OS.
- Tests: `python3` (or `python`), `node` and `cargo` on `PATH`. `StyxDocumentationTests` runs the Python,
  JavaScript, C# and Rust reference code in [Styx.md](../Styx.md) and fails without them. The first run on a machine
  compiles the C# and Rust dependencies and can take a few minutes.

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

Output lands in `Hydra/bin/Release/net10.0/<rid>/publish/`. Both macOS builds need macOS 14 or later, the oldest .NET 10 supports.

`dotnet test` skips the Linux X11 and Windows-only tests on other platforms. `./run-tests.sh linux` runs the X11
tests under Xvfb in Docker.

The Rust reference code builds against a pinned `Tests/Styx/StyxDocs.Cargo.lock`. After changing Styx.md's
`styx-test:cargo` block, regenerate it from the repository root:

```bash
lock=$(mktemp -d) && mkdir "$lock/src"
printf '[package]\nname = "styx-docs"\nversion = "0.1.0"\nedition = "2021"\n\n' > "$lock/Cargo.toml"
sed -n '/<!-- styx-test:cargo -->/,/^```$/p' Styx.md | sed '1,2d;$d' >> "$lock/Cargo.toml"
echo 'fn main() {}' > "$lock/src/main.rs"
cargo generate-lockfile --manifest-path "$lock/Cargo.toml"
cp "$lock/Cargo.lock" Tests/Styx/StyxDocs.Cargo.lock
```
