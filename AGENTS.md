# Repository Guidelines

## Project Structure & Module Organization
`Airi` is a .NET 9 WPF desktop app. Core UI entry points are `App.xaml` and `MainWindow.xaml`.

- `ViewModels/`: UI state and commands (MVVM layer).
- `Views/`: additional XAML windows and code-behind.
- `Services/`: file scanning, persistence, translation, and metadata workflows.
- `Domain/`: shared models used across services and UI.
- `Infrastructure/`: utilities (commands, converters, caching, logging helpers).
- `Web/`: external metadata crawling/parsing sources.
- `Themes/`, `resources/`, `Videos/`: styling and static assets.
- `tests/Airi.Tests/`: xUnit test project.

## Build, Test, and Development Commands
Use the .NET CLI from repository root:

```powershell
dotnet restore
dotnet build Airi.sln -c Debug
dotnet run --project Airi.csproj
dotnet test tests/Airi.Tests/Airi.Tests.csproj -c Debug
```

Coverage (via `coverlet.collector`):

```powershell
dotnet test tests/Airi.Tests/Airi.Tests.csproj --collect:"XPlat Code Coverage"
```

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

When the user types `/graphify`, use the installed graphify skill or instructions before doing anything else.

Rules:
- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- Dirty graphify-out/ files are expected after hooks or incremental updates; dirty graph files are not a reason to skip graphify. Only skip graphify if the task is about stale or incorrect graph output, or the user explicitly says not to use it.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
