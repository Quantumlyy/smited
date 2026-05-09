# Building smited

The build script is a regular C# project under `build/` (Cake.Frosting), debuggable in your IDE, no DSL to learn.

## Day-to-day

```sh
./build.sh                       # default target: clean + restore + build + test
./build.sh --target Build
./build.sh --target Test
./build.sh --target Clean
```

`./build.ps1` is the PowerShell equivalent. Both wrap `dotnet run --project build/Build.csproj`.

## Producing binaries

Self-contained, single-file executables for each platform. Each binary runs without requiring the .NET runtime to be installed.

```sh
./build.sh --target Publish-Linux-x64    # artifacts/linux-x64/Smited.Daemon
./build.sh --target Publish-Win-x64      # artifacts/win-x64/Smited.Daemon.exe
./build.sh --target Publish-OSX-arm64    # artifacts/osx-arm64/Smited.Daemon
./build.sh --target Publish-All          # all three
```

The Windows binary is what to deploy to a stream rig that doesn't have the SDK installed.

## Configuration

Pass `--configuration` to switch from the default `Release`:

```sh
./build.sh --target Test --configuration Debug
```

## Why Cake

Started thin so the scaffolding is in place when ceremonies (signing, packaging, release tags, GitHub uploads) get added later. Each task is a small C# class — easy to debug, easy to add new ones.
