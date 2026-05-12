# OpencodeLauncher

Windows Service that launches and manages the [opencode](https://github.com/anomalyco/opencode) CLI as a persistent web server (`opencode web`).

## How it works

- Runs as a Windows Service named **OpencodeWeb**
- Spawns `opencode web --port <port> --hostname <hostname>` on startup
- Sets `OPENCODE_SERVER_USERNAME` and `OPENCODE_SERVER_PASSWORD` environment variables for the child process
- Captures stdout/stderr and forwards them to the Windows Event Log
- Auto-restarts opencode after a 5-second delay if the process exits unexpectedly

## Prerequisites

- [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0) (preview)
- [opencode](https://github.com/anomalyco/opencode) CLI installed and on `PATH` (or specify a full path in config)

## Configuration

Edit `appsettings.json` in the publish output (or `appsettings.Development.json` for debug runs):

```json
{
  "Opencode": {
    "Path": "opencode",
    "Port": "4096",
    "Hostname": "127.0.0.1",
    "Username": "opencode",
    "Password": ""
  }
}
```

| Key | Description |
|-----|-------------|
| `Path` | Fallback command name if PATH resolution fails (default: `opencode`) |
| `Port` | TCP port for the web server (default: `4096`) |
| `Hostname` | Bind hostname (default: `127.0.0.1`) |
| `Username` | HTTP Basic Auth username via `OPENCODE_SERVER_USERNAME` |
| `Password` | HTTP Basic Auth password via `OPENCODE_SERVER_PASSWORD` |

## Build

```powershell
dotnet build -c Release
```

## Publish (single-file, self-contained, win-x64)

```powershell
dotnet publish -c Release -o .\dist
```

## Install as a Windows Service

```powershell
sc create OpencodeWeb binPath="<full-path>\dist\OpenCodeWebService.exe"
sc start OpencodeWeb
```

## Uninstall

```powershell
sc stop OpencodeWeb
sc delete OpencodeWeb
```
