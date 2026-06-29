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
build.cmd                  # Build + publish to .dist\
build-and-run.cmd          # Build + publish + run .dist\OpenCodeWebService.exe interactively
build-and-start-service.cmd # Build + publish + start the installed Windows service
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
- **Configuration**: `IConfiguration` with the `"Opencode:"` prefix for opencode settings (`Port`, `Hostname`, `Username`, `Password`, `ExperimentalWebsockets`, `SupportZeroTier`); defaults are `Port: 80`, `Hostname: 127.0.0.2`, `SupportZeroTier: false`
- **ZeroTier support**: when `Opencode:SupportZeroTier` is `true`, the TCP proxy additionally binds every IPv4 on up ZeroTier adapters (detected by name/description containing "ZeroTier"), and the worker auto-creates a Windows Firewall inbound rule (`OpenCode Web (ZeroTier Inbound)`) scoped to `RemoteAddress 10.0.0.0/8`. All candidate listen IPs pass `IsSafeToListen` (loopback + RFC1918 only), so a public IP is never bound.
- **Graceful shutdown**: support `CancellationToken` propagation; kill child process on stop
- **Auto-restart**: 5-second delay on unexpected exit, infinite loop while not cancelled
- **Port cleanup**: before each start, `Worker.cs` frees the configured port by killing the owning process and, if necessary, deleting matching entries from the IPv4 TCP table via `iphlpapi.dll`

## Adding packages

Use the project's existing SDK (`Microsoft.NET.Sdk.Worker`). Add NuGet references via:

```powershell
dotnet add OpenCodeWebService.csproj package <PackageName>
```

## Deployment

The publish target is `win-x64`, single-file, self-contained. The `.dist` folder holds the published artifacts. To update `appsettings.json` for distribution, edit both `appsettings.json` **and** `.dist\appsettings.json` in sync.
