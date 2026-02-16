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

## Coding Style & Naming Conventions
- Use 4-space indentation and standard C# formatting.
- `Nullable` is enabled; avoid nullable warnings in new code.
- Public types/members: `PascalCase`.
- Local variables/parameters: `camelCase`.
- Keep MVVM separation clear: UI logic in `ViewModels/`, not in `Views/` code-behind unless strictly view-specific.
- Match existing XAML resource naming patterns in `Themes/` (for example `AColour.*`, `ABrush.*`).

## Testing Guidelines
- Framework: xUnit (`tests/Airi.Tests`).
- Name tests by behavior, grouped per feature class (for example `LibraryScannerTests`, `WebMetadataServiceTests`).
- Add/adjust tests for any service, parser, or viewmodel behavior change.
- Run the full test project before opening a PR.

## Commit & Pull Request Guidelines
Recent history follows Conventional Commit style with scope, often in Korean, e.g. `feat(메타데이터 편집): ...`, `fix(메인 윈도우): ...`.

- Prefer: `<type>(<scope>): <summary>` where type is `feat`, `fix`, `refactor`, `test`, or `chore`.
- Keep commits focused and single-purpose.
- PRs should include: concise change summary, affected areas, test evidence (`dotnet test` output), and screenshots/GIFs for UI changes.

## Agent-Specific Instructions
- 답변은 항상 사용자의 언어인 한국어로 답변을 해야 합니다.

## Engineering Execution Principles
아래 원칙을 따라야 합니다.

### 1. 구현 전에 사고
- 가정은 명시하고, 불확실하면 질문합니다.
- 해석이 여러 개인 요구사항은 선택지를 먼저 제시합니다.
- 더 단순한 대안이 있으면 먼저 제안합니다.
- 모호한 상태에서 임의 구현하지 않습니다.

### 2. 단순성 우선
- 요청된 범위만 구현하고, 추측성 기능은 추가하지 않습니다.
- 단일 사용 코드에 불필요한 추상화를 만들지 않습니다.
- 요청되지 않은 확장성/설정성은 도입하지 않습니다.
- 과한 코드가 되면 더 작은 구현으로 다시 정리합니다.

### 3. 외과적 변경
- 요청과 직접 관련된 파일/라인만 수정합니다.
- 인접 코드의 임의 리팩터링/포맷 변경은 하지 않습니다.
- 기존 스타일을 우선 따릅니다.
- 변경으로 인해 생긴 미사용 코드는 정리하되, 기존 레거시 정리는 요청 시에만 수행합니다.

### 4. 목표 기반 실행
- 작업을 검증 가능한 성공 조건으로 변환합니다.
- 버그 수정은 재현/검증 기준(테스트 또는 명시적 확인 절차)과 함께 진행합니다.
- 다단계 작업은 단계별 검증 항목을 포함해 수행합니다.

