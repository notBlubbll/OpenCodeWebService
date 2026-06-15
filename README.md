# OpencodeLauncher

Windows Service that launches and manages the [opencode](https://github.com/anomalyco/opencode) CLI as a persistent web server (`opencode web`).

## How it works

- Runs as a Windows Service named **OpencodeWeb**
- Spawns `opencode web --port <port> --hostname <hostname>` on startup
- Frees the configured port before starting:
  - kills any live process `LISTENING` on that port
  - forcibly deletes matching TCP control blocks from the kernel table if needed
- Sets `OPENCODE_SERVER_USERNAME`, `OPENCODE_SERVER_PASSWORD`, and `OPENCODE_EXPERIMENTAL_WEBSOCKETS` environment variables for the child process
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
    "Port": "80",
    "Hostname": "127.0.0.2",
    "Username": "opencode",
    "ExperimentalWebsockets": "TRUE"
  }
}
```

| Key | Description |
|-----|-------------|
| `Path` | Fallback command name if PATH resolution fails (default: `opencode`) |
| `Port` | TCP port for the web server (default: `80`) |
| `Hostname` | Bind hostname (default: `127.0.0.2`) |
| `Username` | HTTP Basic Auth username via `OPENCODE_SERVER_USERNAME` |
| `Password` | HTTP Basic Auth password via `OPENCODE_SERVER_PASSWORD` |
| `ExperimentalWebsockets` | Enable experimental websockets via `OPENCODE_EXPERIMENTAL_WEBSOCKETS` (default: `TRUE`) |

## Build

```powershell
dotnet build -c Release
```

## Publish (single-file, self-contained, win-x64)

```powershell
dotnet publish -c Release -o .\dist
```

## Install as a Windows Service

The service should run under the **Administrator** account so opencode stores sessions/config in `C:\Users\Administrator` instead of the `LocalSystem` profile.

1. Create the service:

```powershell
sc create OpencodeWeb binPath="<full-path>\dist\OpenCodeWebService.exe"
```

2. Set it to log on as Administrator (graphical way recommended):

- Open **Services** (`services.msc`).
- Find **OpencodeWeb** → right-click → **Properties** → **Log On**.
- Select **This account**, enter `Administrator` and the account password.
- Click **OK**, then:

```powershell
sc start OpencodeWeb
```

Or via `sc` (replace `YOUR_PASSWORD`):

```powershell
sc config OpencodeWeb obj= ".\Administrator" password= "YOUR_PASSWORD"
sc start OpencodeWeb
```

## Uninstall

```powershell
sc stop OpencodeWeb
sc delete OpencodeWeb
```
