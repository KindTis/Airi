---
name: post-feature-test-build-gate
description: .NET projects quality gate workflow for after feature implementation. Add unit tests for changed behavior, run tests, and verify build status. Use when users ask for requests like "테스트 추가", "테스트 실행", "빌드 확인", or "변경사항 검증".
---

# Post Feature Test Build Gate

## Overview
Execute a consistent quality gate after feature work.
Add or update unit tests for the changed behavior, then run tests and build verification before final reporting.

## Workflow
1. Collect change context.
- Run `git status --short` and `git diff --name-only`.
- Prioritize test targets in changed services, view models, parsers, and business logic files.

2. Define verification criteria.
- Cover changed behavior with success and edge-case assertions.
- Follow existing xUnit naming style such as `Method_WhenCondition_ExpectedBehavior`.

3. Add tests surgically.
- Edit only files directly related to the request.
- Avoid unrelated refactoring.

4. Run tests.
- Default: `dotnet test tests/Airi.Tests/Airi.Tests.csproj -c Debug`
- Coverage when requested: `dotnet test tests/Airi.Tests/Airi.Tests.csproj --collect:"XPlat Code Coverage"`

5. Verify build.
- Default: `dotnet build Airi.sln -c Debug`

6. Report results.
- List changed test files.
- Include commands run and pass/fail summary.
- Explicitly state if any verification step was not executed.

## Command Script
Use `scripts/run_quality_gate.ps1` for repeatable execution.

```powershell
./scripts/run_quality_gate.ps1
./scripts/run_quality_gate.ps1 -CollectCoverage
./scripts/run_quality_gate.ps1 -Configuration Release
```
