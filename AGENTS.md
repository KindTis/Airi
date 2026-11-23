## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

When the user types `/graphify`, use the installed graphify skill or instructions before doing anything else.

Rules:
- When graphify-out/graph.json exists, run `graphify query "<question>"` before your first grep, search or source-file read for a codebase question, and put this rule in every subagent prompt that explores code. Read raw files only for lines the graph or an earlier read already located. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- Dirty graphify-out/ files are expected after hooks or incremental updates; dirty graph files are not a reason to skip graphify. Only skip graphify if the task is about stale or incorrect graph output, or the user explicitly says not to use it.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
- A committed graphify-out/graph.json is rewritten whole on every build, so a large diff to it in a pull request is expected. Do not flag it as churn or suggest skipping the graph rebuild or its hooks.

## Jira 보드

- 이 프로젝트에서 사용하는 Jira 보드는 [AIRI 보드](https://kindtis.atlassian.net/jira/software/projects/AIRI/boards/3)다. 프로젝트 키는 `AIRI`, 보드 ID는 `40`이다.
- 이 프로젝트에서 사용자의 Jira 관련 요청을 처리할 때는 항상 위 보드를 참조한다.

## 구현 완료 후 빌드 검증

기능 구현·수정·리팩터링을 완료한 뒤에는 저장소 루트에서 Debug와 Release 빌드를 모두 수행하고, 두 구성의 빌드 결과를 확인한다.

```powershell
dotnet build Airi.sln -c Debug
dotnet build Airi.sln -c Release
```
