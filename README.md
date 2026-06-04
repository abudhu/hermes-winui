# hermes-winui

Windows-native companion apps for [Hermes Agent](https://github.com/NousResearch/hermes-agent).

Two surfaces share a single typed API client:

- **Hermes.App** — a full WinUI 3 desktop client: chat, sessions, jobs, MCP server config, and a Ctrl+K command palette.
- **Hermes.TrayApp** — a system-tray companion that polls gateway health at a glance.

## Projects

| Project | Purpose |
|---------|---------|
| `src/Hermes.ApiClient` | Strongly-typed `HttpClient` wrapper over the local Hermes gateway REST API. Reusable by every UI surface. |
| `src/Hermes.ApiClient.Tests` | xUnit tests for the API client (URL building, JSON shape, error formatting). |
| `src/Hermes.App` | WinUI 3 desktop client. The main app. |
| `src/Hermes.TrayApp` | System-tray status indicator. |

## Hermes.App (WinUI 3 desktop client)

A native Windows desktop client for talking to your local Hermes agent. Built on WinUI 3 / Windows App SDK 2.1.3, .NET 10.

### Pages

| Page | What it does |
|---|---|
| **Chat** | Streamed chat with the agent. Markdown rendering (GFM tables, fenced code), styled cards for fenced ```mermaid``` blocks with an "Open in mermaid.live" share-link button, and KaTeX-friendly math blocks (`$$...$$`) with "Copy LaTeX". Inline math (`$x^2$`) renders as accent-tinted monospace. Wide tables scroll horizontally inside their bubble. Tool calls render as collapsible expanders with name + duration + pretty-printed JSON. |
| **Sessions** | Browsable list of past conversations grouped by date. Built-in **full-text search** (debounced 250 ms, server-side FTS5 via `/api/sessions/search`) shows metadata matches and message-content matches as two flat groups while you type. Click any row to preview, "Resume" to load it back into Chat. |
| **Memories** | Read/manage agent memories. |
| **Skills** | Manage skills / installed prompts. |
| **Jobs** | Scheduled jobs. **Master-detail layout** — left column lists jobs as cards, right column shows the selected job's status badge, schedule, prompt body, last-run timestamps, and any error InfoBars. Live polling (3 s while a job is active, 10 s after idle) keeps the UI fresh. OS toast notifications fire when a job transitions to a terminal state — clicking the toast cold-starts or foregrounds the app and deep-links to the row. |
| **Bridges** | Per-platform bridge health. |
| **Settings** | Gateway config, model selection, and **MCP servers** management (add, edit, enable/disable individual servers, point at a config file). |

### Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+K` | Open the command palette — fuzzy-find across navigation, sessions, jobs, and built-in commands |
| `Ctrl+N` | Navigate to Chat and start a new chat |
| `Ctrl+W` | (On Chat) close the current chat |
| `Ctrl+,` | Open Settings |
| `Ctrl+E` | (On Chat) copy current transcript as markdown |
| `Ctrl+/` or `Ctrl+NumpadDivide` | (On Chat) focus the composer |
| `Esc` | (On Chat, while streaming) cancel the in-flight stream |

The command palette also exposes one-tap actions for resuming recent sessions and running/pausing jobs.

### Export

From the Chat header overflow button: **Copy as markdown** (clipboard) or **Save as .md…** (file picker). Both include role-labelled blocks, preserved fences, and optional session metadata header. Disabled while a stream is in flight or the transcript is empty.

### Build & run (Hermes.App)

```pwsh
# Build (the explicit RID is required because the csproj sets PlatformTarget=x86 by default).
dotnet build src\Hermes.App\Hermes.App.csproj -c Debug -r win-arm64

# Or, on x64 dev machines:
dotnet build src\Hermes.App\Hermes.App.csproj -c Debug -r win-x64
```

`Hermes.App` ships as an MSIX-packaged WinUI 3 app. The simplest local install loop:

```pwsh
# Unregister any previous install, register the freshly built layout, launch via the shell.
Get-AppxPackage -Name "Hermes.App" | Remove-AppxPackage -ErrorAction SilentlyContinue
Add-AppxPackage -Register "src\Hermes.App\bin\Debug\net10.0-windows10.0.26100.0\win-arm64\AppxManifest.xml" -ForceApplicationShutdown
Start-Process "explorer.exe" -ArgumentList "shell:AppsFolder\Hermes.App_hyqgre7ye1qte!App"
```

(Substitute `win-arm64` with `win-x64` to match your `-r` flag.)

## Hermes.TrayApp (system-tray status)

Lightweight companion that lives in the notification area and polls `/health/detailed` every 5 s. Right-click (or left-click) for status, refresh, folder shortcuts, and Exit.

### Build & run (TrayApp)

```pwsh
dotnet build src\Hermes.TrayApp\Hermes.TrayApp.csproj -c Debug
.\src\Hermes.TrayApp\bin\Debug\net10.0-windows\HermesTray.exe
```

### Status colors

| Color | Meaning |
|-------|---------|
| 🟢 Green | Gateway running and every platform reports `connected` |
| 🟡 Amber | Gateway running but at least one platform is degraded, *or* gateway state ≠ `running` |
| 🔴 Red | Gateway unreachable (connection refused, timeout, or non-2xx response) |
| ⚪ Gray | Startup / unknown |

A white inner dot indicates `active_agents > 0` (the agent is mid-turn).

## Requirements

- Windows 10/11 (Windows App SDK 2.1.3 requires 10.0.17763+)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Hermes Agent](https://github.com/NousResearch/hermes-agent) installed locally with the API server enabled in `~/.hermes/.env` (or `%LOCALAPPDATA%\hermes\.env`):

  ```env
  API_SERVER_ENABLED=true
  API_SERVER_HOST=127.0.0.1
  API_SERVER_PORT=8642
  API_SERVER_KEY=<your-key>
  ```

## Build everything

```pwsh
dotnet build Hermes.WinUI.slnx -c Debug
```

The `Hermes.App` project requires an explicit `-r win-arm64` or `-r win-x64` when built individually (see the build snippet above). The solution-level build handles RID selection per-project.

## Tests

```pwsh
dotnet test src\Hermes.ApiClient.Tests\Hermes.ApiClient.Tests.csproj
```

Current: **75 tests** covering API client URL construction, JSON deserialization, error formatting, search path building, and job-envelope parsing.
