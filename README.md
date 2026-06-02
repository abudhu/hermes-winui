# hermes-winui

Windows-native companion apps for [Hermes Agent](https://github.com/NousResearch/hermes-agent).

## Projects

| Project | Purpose |
|---------|---------|
| `src/Hermes.ApiClient` | Strongly-typed `HttpClient` wrapper over the local Hermes gateway REST API. Reusable by every UI surface (tray, WinUI 3, anything else). |
| `src/Hermes.TrayApp` | **Phase 1 — system tray app.** Surfaces live gateway status, current model, active-agent count, and per-platform bridge health. Polls `/health/detailed` every 5 s. |

## Requirements

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Hermes agent installed locally with the API server enabled in `~/.hermes/.env` (or `%LOCALAPPDATA%\hermes\.env`):
  ```env
  API_SERVER_ENABLED=true
  API_SERVER_HOST=127.0.0.1
  API_SERVER_PORT=8642
  API_SERVER_KEY=<your-key>
  ```

## Build & run the tray app

```pwsh
dotnet build Hermes.WinUI.slnx -c Debug
.\src\Hermes.TrayApp\bin\Debug\net10.0-windows\HermesTray.exe
```

The icon appears in the notification area. Right-click for status, refresh, folder shortcuts, and Exit. Left-click also opens the menu.

### Status colors

| Color | Meaning |
|-------|---------|
| 🟢 Green | Gateway running and every platform reports `connected` |
| 🟡 Amber | Gateway running but at least one platform is degraded, *or* gateway state ≠ `running` |
| 🔴 Red | Gateway unreachable (connection refused, timeout, or non-2xx response) |
| ⚪ Gray | Startup / unknown |

A white inner dot indicates `active_agents > 0` (the agent is mid-turn).
