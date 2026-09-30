<p align="center"><img src="src/Dispatch.App/Assets/dispatch-logo.svg" width="96" alt="Dispatch logo" /></p>

# Dispatch

A clean, fast, cross-platform API client (Postman-style) built on **.NET 10** and **Avalonia 11**.

## Features (v0.1)

| Area | What you get |
|---|---|
| Request builder | GET / POST / PUT / PATCH / DELETE / HEAD / OPTIONS, URL bar ⇄ Params table two-way sync, headers, per-row enable/disable; new requests start with editable default headers (`Accept`, `User-Agent`, `Accept-Encoding`, `Connection`, `Cache-Control`); **Bulk Edit** for params, headers and form fields (`key: value` per line, `//` disables a line); rename a request in place (name field or double-click its tab) |
| Body | JSON, Text, XML (with **Beautify**), `x-www-form-urlencoded`; choosing a body type keeps the `Content-Type` header in sync unless you set a custom one |
| Auth | Bearer token, Basic, API key (header or query) |
| Response | Status (color-coded), time, size, pretty/raw body formatted by `Content-Type` (JSON, XML, HTML, JavaScript, form, text; switchable), headers, copy; binary and >5 MB bodies handled safely |
| Collections | Create / rename / delete, save requests (Ctrl+S), duplicate, search |
| Environments | `{{variable}}` substitution in URL, params, headers, body and auth; nested variables; active env remembered |
| History | Last 500 sends, re-open in a new tab |
| Workspace | Multiple request tabs with unsaved-changes indicator, light/dark theme |

### Shortcuts

| Keys | Action |
|---|---|
| `Ctrl+Enter` / `Enter` in URL | Send |
| `Ctrl+S` | Save |
| `Ctrl+T` | New tab |
| `Ctrl+W` | Close tab |

## Architecture

Clean Architecture, dependencies point inwards:

```
Dispatch.App             Avalonia UI, MVVM (CommunityToolkit.Mvvm), composition root
   └─ Dispatch.Infrastructure   HttpClient executor, EF Core SQLite repositories, DI
         └─ Dispatch.Application   Use cases: request building, variables, query sync, formatting, ports
               └─ Dispatch.Domain     Entities & value objects (no dependencies)
```

* **Domain** – `ApiRequest`, `RequestCollection`, `ApiEnvironment`, `HistoryEntry`, `ApiResponse`.
* **Application** – `RequestMessageBuilder` (definition → `HttpRequestMessage`), `VariableResolver`,
  `QueryString` (lossless URL/params sync), `RequestSender` (send + history), repository/executor interfaces.
  Has no NuGet dependencies.
* **Infrastructure** – `HttpRequestExecutor` (via `IHttpClientFactory`, `SocketsHttpHandler`, no shared cookie jar,
  auto-decompression), `DispatchDbContext` (complex values stored as JSON columns), short-lived contexts via
  `IDbContextFactory`.
* **App** – views are dumb XAML with compiled bindings; all behaviour lives in view models.

Data is stored in `%LOCALAPPDATA%\Dispatch\dispatch.db` (`~/.local/share/Dispatch` on Linux/macOS).

## Build & run

```bash
dotnet build Dispatch.slnx
dotnet test Dispatch.slnx
dotnet run --project src/Dispatch.App
```

Requires the .NET 10 SDK.

## Roadmap

- Postman collection v2.1 and cURL import/export
- Multipart form-data and binary file bodies
- Syntax-highlighted editors (AvaloniaEdit), response search
- Per-request settings: timeout, redirects, SSL verification toggle
- Pre-request / test scripts, cookies manager, code snippet generation
- Secret variables stored with OS-level protection (DPAPI / Keychain)
