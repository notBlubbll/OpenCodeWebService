# AGENTS.md

Instructions for AI coding agents working on this project.

## Build & Verify

```powershell
dotnet build -c Release
```

There is no separate lint or typecheck step — `dotnet build` with `-c Release` (and `<Nullable>enable</Nullable>`) covers both.

## Project structure

```
Program.cs              # Host builder, registers Windows Service + OpencodeWorker
Worker.cs               # BackgroundService that spawns/restarts opencode web
build.cmd               # Build + publish to .dist\
OpenCodeWebService.csproj   # .NET 11 Worker SDK, win-x64, single-file self-contained
appsettings.json        # Production configuration
appsettings.Development.json  # Development configuration
Properties/
  launchSettings.json   # Dev launch profile
.dist/                  # Published output (single-file exe + configs)
```

## Conventions

- **Target framework**: `net11.0`
- **Runtime**: `win-x64`, self-contained, published as single file
- **Nullable reference types**: enabled
- **Implicit usings**: enabled
- **Namespace**: file-scoped (`namespace OpenCodeWebService;`)
- **Primary constructor**: not used — prefer explicit constructors
- **Logging**: use `ILogger<T>` (not `Console.WriteLine`)
- **Stream reading**: handled in `ReadStream` with `ObjectDisposedException` / `IOException` suppression for teardown safety
- **Configuration**: `IConfiguration` with the `"Opencode:"` prefix for opencode settings
- **Graceful shutdown**: support `CancellationToken` propagation; kill child process on stop
- **Auto-restart**: 5-second delay on unexpected exit, infinite loop while not cancelled

## Adding packages

Use the project's existing SDK (`Microsoft.NET.Sdk.Worker`). Add NuGet references via:

```powershell
dotnet add OpenCodeWebService.csproj package <PackageName>
```

## Deployment

The publish target is `win-x64`, single-file, self-contained. The `.dist` folder holds the published artifacts. To update `appsettings.json` for distribution, edit both `appsettings.json` **and** `.dist\appsettings.json` in sync.
