# 비디오 썸네일 생성 구현 항목 매핑

- 구현 계획서: `C:/Users/tatis/Repos/Airi/docs/superpowers/plans/2026-07-18-video-thumbnail-generation.md`
- 기준 설계: `C:/Users/tatis/Repos/Airi/docs/superpowers/specs/2026-07-18-video-thumbnail-generation-design.md`
- 구현 저장소: `C:/Users/tatis/Repos/Airi/.worktrees/video-thumbnail-generation`
- 작업 브랜치: `feature/video-thumbnail-generation`

## 구현 항목 매핑표

| 구현 항목 ID | 필수 여부 | 구현 계획서 항목 | 계획서 근거 | 수용 기준 | 구현 대상 | 구현 상태 | 검증 방법 | 검증 결과 | 항목 판정 | 보류 사유 |
|---|---|---|---|---|---|---|---|---|---|---|
| IMP-001 | 필수 | 제한된 경로 삭제 재시도 | Task 1 Step 1~4 | 파일·폴더 삭제를 즉시, 100ms, 250ms, 500ms 순으로 최대 4회 시도하고 마지막 복구 가능 예외를 반환한다. 누락 경로는 삭제·지연하지 않는다. | `Infrastructure/PathCleanup.cs`, `tests/Airi.Tests/PathCleanupTests.cs` | 구현 | `dotnet test ... --filter FullyQualifiedName~PathCleanupTests`에서 5개 계약 통과 | 통과 | 미판정 | |
| IMP-002 | 필수 | 후보 시점 계산 | Task 2 Step 1~2, 승인 결정 | 양수 길이의 앞뒤 5% 제외 범위를 5등분하고 `Random`으로 구간별 한 시점을 선택해 오름차순 5개를 반환하며 비양수 길이는 거부한다. | `Services/VideoThumbnailExtractor.cs`, `tests/Airi.Tests/VideoThumbnailExtractorTests.cs` | 구현 | 고정 `Random(1979)` 구간·순서·비양수 테스트 | 통과 | 미판정 | |
| IMP-003 | 필수 | probe 재사용과 FFmpeg 요청 | Task 2 Step 2, 인터페이스 | 배치마다 기존 `FfmpegCorePreviewBackend.ProbeAsync`를 1회 호출하고 기존 `IMediaProcessRunner`로 정확히 5개의 CPU JPEG 요청을 독립 `ArgumentList` 항목으로 실행한다. `-ss`는 `-i` 앞, 단일 프레임, 무음·무자막·무데이터, 원본 비율·가로 최대 1280px, 고정 출력명이어야 한다. | `Services/VideoThumbnailExtractor.cs`, 기존 `Services/VideoPreview/*`, `tests/Airi.Tests/VideoThumbnailExtractorTests.cs` | 구현 | 요청 기록 fake로 probe/FFmpeg 횟수와 인자·출력 번호 검증 | 통과 | 미판정 | |
| IMP-004 | 필수 | FFmpeg 동시 실행 상한 | Task 2 Step 3~5 | 다섯 후보 작업을 시작하되 활성 FFmpeg는 실제로 2개까지 겹치고 최대 2개를 넘지 않는다. | `VideoThumbnailExtractor.ExtractAsync`, extractor 단위 테스트 | 구현 | 활성 수를 `Interlocked`로 기록하는 fake 테스트 | 통과 | 미판정 | |
| IMP-005 | 필수 | 배치 전부 성공/전부 실패와 취소 | Task 2 Step 3~5 | 후보 하나 실패 시 활성·대기 형제를 연결 토큰으로 취소하고 모두 종료한 뒤 최초 비취소 실패를 보존한다. 누락·빈 출력과 시작 실패도 전체 실패이며 사용자 취소는 `OperationCanceledException`이다. 부분 후보는 반환하지 않는다. | `VideoThumbnailExtractor.ExtractAsync`, extractor 단위 테스트 | 구현 | 실패·출력 누락·시작 실패·사용자 취소 테스트 | 통과 | 미판정 | |
| IMP-006 | 필수 | 추출기 단위 회귀 | Task 2 Step 1~6 | 계획서가 명명한 추출기 단위 테스트 전부 통과한다. | `tests/Airi.Tests/VideoThumbnailExtractorTests.cs` | 구현 | 필터된 단위 테스트 실행 | 통과 | 미판정 | |
| IMP-007 | 필수 | 실제 FFmpeg JPEG 통합 | Task 2 Step 7 | 번들 FFmpeg와 H.264 fixture로 시간순 JPEG 5개를 만들고 파일·길이·JPEG 디코딩·가로 1~1280·16:9를 확인하며 테스트 임시 폴더를 정리한다. 제품 timeout은 추가하지 않는다. | `tests/Airi.Tests/VideoThumbnailExtractorIntegrationTests.cs` | 구현 | 15초 테스트 토큰을 둔 실제 통합 테스트 | 통과 | 미판정 | |
| IMP-008 | 필수 | 선택 결과와 이미지 메모리 로드 | Task 3 인터페이스, Step 1·4 | 정확히 5개 후보만 받고 `Cancel`·`Select`·`Regenerate`를 구조화해 반환한다. 후보는 `OnLoad`로 읽고 가능하면 `Freeze`하여 원본 파일 핸들을 남기지 않는다. | `Views/ThumbnailSelectionWindow.xaml.cs`, 선택 창 테스트 | 구현 | 생성 직후 원본 삭제, 후보 수 거부, 세 결과 테스트 | 통과 | 미판정 | |
| IMP-009 | 필수 | 선택 창 UI·키보드·접근성 | Task 3 Step 1·3·4 | 후보 5개 단일 선택, 선택/포커스 구분, Space/Enter 선택, 마지막 후보 다음 Tab이 다시 생성으로 이동, 버튼 순서 `다시 생성`·`취소`·`선택`, 시간순 위치와 선택 상태 접근성 이름을 제공한다. | `Views/ThumbnailSelectionWindow.xaml`, `.xaml.cs`, 선택 창 테스트 | 구현 | WPF 포커스·Tab·테두리·AutomationProperties 테스트 | 통과 | 미판정 | |
| IMP-010 | 필수 | 작업 모달 비동기 수명주기 | Task 3 Step 4 | 소유된 `Show()` 기반 `SelectAsync`는 한 번만 시작되고 닫힘까지 비동기 결과를 유지한다. 취소 토큰, 취소 버튼, X, Escape는 오류 없이 `Cancel`을 반환한다. | `ThumbnailSelectionWindow.SelectAsync`, `MetadataEditorWindow` 기본 selector | 구현 | `ShowDialog` 미사용, 재호출 거부와 취소 행렬 테스트 | 통과 | 미판정 | |
| IMP-011 | 필수 | 선택 창 회귀 | Task 3 Step 1~5 | 계획서가 명명한 선택 창 WPF 테스트가 모두 통과한다. | `tests/Airi.Tests/ThumbnailSelectionWindowTests.cs` | 구현 | 필터된 WPF 테스트 실행 | 통과 | 미판정 | |
| IMP-012 | 필수 | 기존 캐시 적용 회귀 고정 | Task 4 Step 1 | 작은 실제 JPEG 파일을 기존 `UpdateThumbnailFromFileAsync`가 cache로 복사하고 `ThumbnailPath`, 절대 미리보기 URI, 원본 표시명을 갱신한다. | `tests/Airi.Tests/MetadataEditorViewModelTests.cs` | 구현 | 신규 ViewModel 회귀 테스트 단독 통과 | 통과 | 미판정 | |
| IMP-013 | 필수 | F1 썸네일 버튼 배치 | Task 4 Step 2 | 버튼 이름과 표시 순서가 `파일 선택` → `썸네일 생성` → `초기화`이며 기존 스타일·폭을 따른다. | `Views/MetadataEditorWindow.xaml` | 구현 | XAML 인스턴스와 UI 스모크 확인 | 통과 | 미판정 | |
| IMP-014 | 필수 | 생성 세션 시작과 진단점 | Task 4 인터페이스, Step 6 | public 생성자는 번들 extractor·실제 selector·PathCleanup·MessageBox를 연결하고 internal 생성자는 계획의 seam을 받는다. SourcePath 누락은 probe/FFmpeg 0회·기존 썸네일 유지·안내, 중복 클릭/닫기 시작 뒤 클릭은 새 세션을 만들지 않으며 `ThumbnailGenerationTask`·`CloseTask`를 관찰할 수 있다. | `Views/MetadataEditorWindow.xaml.cs`, 수명주기 테스트 | 구현 | source 누락·중복 클릭·진단 Task 테스트 | 통과 | 미판정 | |
| IMP-015 | 필수 | 원자적 다시 생성 루프 | Task 4 Step 3·6 | 세션 고유 루트 아래 배치 폴더를 순번으로 만들고 다시 생성 횟수 제한 없이 새 5개 전부 성공한 뒤에만 후보를 교체한다. 다시 생성 실패는 오류 후 최초 후보를 그대로 재표시하며 부분 새 경로를 selector에 넘기지 않는다. | `MetadataEditorWindow.GenerateThumbnailAsync`, 수명주기 테스트 | 구현 | 다시 생성 성공·실패 selector/runner 테스트 | 통과 | 미판정 | |
| IMP-016 | 필수 | 생성 중 충돌 동작 차단 | Task 4 Step 7 | 생성부터 임시 정리 종료까지 파일 선택·생성·초기화·141jav 적용·저장만 비활성화하고 handler guard를 둔다. F1 취소·X·Escape는 계속 가능하며 창이 열려 있으면 종료 후 재활성화한다. | `MetadataEditorWindow.xaml(.cs)`, 수명주기 테스트 | 구현 | 다섯 버튼 상태·Cancel 활성·handler guard·crawler→생성 역순 차단·상태 조합 해제 테스트 | 통과 | 미판정 | |
| IMP-017 | 필수 | 세션 임시 정리와 복합 오류 | Task 4 Step 3·6 | 선택·취소·초기 생성 실패·재생성 실패 뒤 종료·캐시 적용 실패·F1 닫기 모두 세션 루트를 승인 재시도로 정리한다. 취소는 오류를 표시하지 않고, 원래 오류와 정리 실패가 함께면 단계 오류와 잔존 절대 경로를 한 메시지에 보존하며 정리만 실패하면 중립 경고한다. | `MetadataEditorWindow.GenerateThumbnailAsync`, 수명주기 테스트 | 구현 | 임시 루트 삭제·정리 실패·복합 실패 테스트 | 통과 | 미판정 | |
| IMP-018 | 필수 | 생성 캐시 정확 추적 | Task 4 Step 4·6·7 | 생성 selector에서 선택해 기존 ViewModel 캐시 복사가 성공한 직후 그 절대 경로만 대소문자 무시 집합에 추적한다. 파일 선택·crawler·초기 캐시는 추적하거나 삭제하지 않는다. 캐시 복사 구간은 `CancellationToken.None`으로 완료 후 추적한다. | `MetadataEditorWindow` 생성 루프·추적 집합, 수명주기 테스트 | 구현 | 두 생성 선택·다른 출처 전환·닫기 경합 테스트 | 통과 | 미판정 | |
| IMP-019 | 필수 | 저장·취소 캐시 정리 행렬 | Task 4 Step 4·7 | 저장 시 최종 경로가 추적 캐시면 그 하나만 유지하고 나머지를 동시에 삭제한다. 다른 출처면 추적 캐시 전부, 취소/X/Escape면 전부 삭제한다. 정확한 경로만 삭제하며 성공 경로만 집합에서 제거한다. 실패는 절대 잔존 경로를 경고해도 저장·취소 의미를 유지한다. | `CleanupGeneratedCachesAsync`, `PathCleanup.DeleteFileAsync`, `OnLoadImageSourceConverter`, 수명주기 테스트 | 구현 | 계획서의 캐시 행렬과 실제 미리보기 파일 핸들 회귀 테스트 | 통과 | 미판정 | |
| IMP-020 | 필수 | F1 비동기 닫기 통합 | Task 4 Step 8 | 저장은 `TryBuildResult` 뒤, 취소·X·Escape는 같은 `BeginClose` 경로로 들어간다. 첫 닫기만 수행하고 생성/선택을 취소해 임시 정리를 기다린 뒤 캐시를 정리한 후 Result와 창 닫기 의미를 확정한다. | `MetadataEditorWindow.BeginClose`, `CompleteCloseAsync`, `OnClosing`, handlers | 구현 | 추출 중·선택 중 cancel/escape/close 및 save/cancel 테스트 | 통과 | 미판정 | |
| IMP-021 | 필수 | F1 수명주기 회귀 | Task 4 Step 3~5·9 | 계획서가 명명한 `MetadataEditorWindowThumbnailGenerationTests` 전체와 기존 ViewModel 테스트가 통과한다. | `tests/Airi.Tests/MetadataEditorWindowThumbnailGenerationTests.cs` | 구현 | Task 4 필터 23개와 신규 범위 필터 50개 통과 | 통과 | 미판정 | |
| IMP-022 | 필수 | 전체 자동 회귀·빌드·정적 검사 | Task 5 Step 1·2·4 | 신규 범위 테스트, 전체 테스트, Debug 빌드, placeholder 검색, `git diff --check`가 통과하고 새 경고·설명되지 않은 변경이 없다. | 전체 변경 및 테스트 | 구현 | 신규 범위 50개, 전체 333개, Debug 빌드, placeholder·diff 검사 | 통과 | 미판정 | |
| IMP-023 | 필수 | 지식 그래프 갱신 | Task 5 Step 3 | 원본 checkout의 기존 graph·manifest 기준점을 worktree의 무시된 `graphify-out`에 복제한 상태에서, 코드 변경 뒤 worktree에서 `graphify update .`를 실행해 새 타입 관계를 기존 graph에 증분 반영한다. 원본 checkout의 graph는 갱신하지 않는다. | worktree `graphify-out/*`(Git 무시 산출물) | 구현 | 기준 SHA-256 일치, 최종 `graphify update .` 성공, 2,502 nodes/4,799 links와 새 상태 관계 확인 | 통과 | 미판정 | |
| IMP-024 | 필수 | 수동 UI 스모크 | Task 5 Step 5 | 앱 실행에서 버튼 순서, 포인터·키보드 선택/다시 생성/취소, 원자적 후보 교체, 생성 중 F1 종료, 최종 캐시 보존·회수를 계획 체크리스트대로 확인한다. | 실행 중 WPF UI | 구현 | 실제 H.264로 `dotnet run --project Airi.csproj`, Windows UI Automation·포인터·키보드로 계획의 모든 비조건부 시나리오 실행 | 통과 | 미판정 | |
| IMP-025 | 필수 | 범위와 사용자 변경 보존 | 승인된 구현 결정, 완료 체크리스트 | 하드웨어 가속·제품 timeout·설정 UI·범용 프레임워크·전역 청소기를 추가하지 않는다. 원본 checkout의 `MainWindow.xaml.cs`, `tests/Airi.Tests/MainWindowThumbnailRealizationTests.cs` 사용자 변경을 수정·포맷·스테이징하지 않는다. | 전체 diff와 원본 checkout 상태 | 구현 | 기준점 대비 변경 파일·scope 검색·원본 checkout 상태 대조 | 통과 | 미판정 | |

## 구현 가정

- 계획서에 `VideoThumbnailExtractor`의 생성자 서명이 없으므로 기존 패턴을 재사용한다. public 생성자는 FFmpeg 바이너리 폴더만 받고, internal 생성자는 같은 폴더와 기존 `IMediaProcessRunner`를 받아 동일 runner를 `FfmpegCorePreviewBackend` probe와 후보 FFmpeg 실행에 공유한다. 새 프로덕션 인터페이스는 만들지 않는다.
- `MetadataEditorWindow`의 기본 selector는 `ThumbnailSelectionWindow`를 만들고 F1 창을 Owner로 지정한 뒤 `SelectAsync`를 호출한다. 선택 창 자체는 `ShowDialog()`를 사용하지 않는다.
- 계획서와 기준 설계는 Git에서 무시된 원본 checkout의 절대 경로를 검증 기준으로 사용한다. 구현과 테스트 변경은 worktree에서만 수행하며 이 매핑 문서는 `git add -f`로 추적한다.
- 계획서 첫머리의 체크박스 갱신은 원본 checkout 문서를 수정하지 않기 위해 이 매핑표의 `구현 상태`, `검증 결과`, `항목 판정`과 `검증 요약`을 각 RED/GREEN/완료 게이트 결과에 맞춰 갱신하는 방식으로 대체한다. 원본 계획서는 읽기 전용 기준 문서로 유지한다.
- `graphify update .`는 기존 증분 기준이 필요하므로 구현 전 원본의 무시된 `graphify-out` 전체를 worktree의 무시된 동일 경로에 복제했다. 이후 graph 명령은 worktree에서만 실행하며 원본 graph에는 결과를 역복사하지 않는다.
- 생성 중 비활성화는 기존 crawler용 `SetInteractionInProgress`와 별도 상태로 구현해, 기존 crawler 동작의 Cancel 비활성 의미를 임의 변경하지 않는다.
- 계획서가 명시한 수동 UI 스모크는 자동 테스트·빌드 완료 뒤 실행한다. 자동화로 재현하기 어려운 정리 실패 주입은 대응 자동 테스트 결과로 보완하되 스모크 결과에 구분해 기록한다.

## 변경 기준점

- 시작 커밋: `b8388cf3a00b5b01ab3a6e477755afc7402ec1c1`
- 원본 checkout 시작 상태:
  - unstaged: `MainWindow.xaml.cs`
  - unstaged: `tests/Airi.Tests/MainWindowThumbnailRealizationTests.cs`
  - staged: 없음
  - untracked: 없음(`git status --short` 기준)
- 구현 worktree 시작 상태:
  - 브랜치: `feature/video-thumbnail-generation`
  - staged: 없음
  - unstaged: 없음
  - untracked: 없음
- 환경 준비(무시됨): 원본의 `resources/ffmpeg`를 worktree에 junction으로 연결하고, 영상 fixture 4개를 hard link로 연결했다. Git 변경 범위에는 포함되지 않는다.
- graphify 증분 기준 준비(무시됨): 원본 `graphify-out`을 worktree에 복제했다. 준비 시 SHA-256은 `graph.json=9419D10F93106D4C08A8E08FC6A7D871B82489F7E672C37D14F25F2F7C9FCDDB`, `manifest.json=B9E4C3293EE4EDB4298EA7E0A7E5D5205AEFDFD3AA42A164D2FD508318773C0D`로 원본과 복제본이 일치했다. `.graphify_root`는 `.`이고 interpreter 경로도 존재한다.
- baseline: `dotnet test tests/Airi.Tests/Airi.Tests.csproj -c Debug` — 284 통과, 0 실패. 자산 연결 전 14개 실패는 FFmpeg/fixture 부재였고 연결 후 전부 통과했다.
- 이번 루프가 생성하거나 수정한 전체 변경 범위(기능·테스트 13개 + 매핑 1개):
  - `Infrastructure/OnLoadImageSourceConverter.cs`
  - `Infrastructure/PathCleanup.cs`
  - `Services/VideoThumbnailExtractor.cs`
  - `Views/MetadataEditorWindow.xaml`
  - `Views/MetadataEditorWindow.xaml.cs`
  - `Views/ThumbnailSelectionWindow.xaml`
  - `Views/ThumbnailSelectionWindow.xaml.cs`
  - `tests/Airi.Tests/MetadataEditorViewModelTests.cs`
  - `tests/Airi.Tests/MetadataEditorWindowThumbnailGenerationTests.cs`
  - `tests/Airi.Tests/PathCleanupTests.cs`
  - `tests/Airi.Tests/ThumbnailSelectionWindowTests.cs`
  - `tests/Airi.Tests/VideoThumbnailExtractorIntegrationTests.cs`
  - `tests/Airi.Tests/VideoThumbnailExtractorTests.cs`
  - `docs/implementation/2026-07-18-video-thumbnail-generation-implementation-map.md`
- 기능·테스트 커밋:
  - `1d4e26f feat: retry thumbnail path cleanup`
  - `c5e2b0d feat: extract thumbnail candidates with bounded concurrency`
  - `14700eb feat: choose or regenerate thumbnail candidates`
  - `d8b52b7 feat: manage generated thumbnail lifecycle`
  - `98707df fix: release generated thumbnail preview handles`
  - `dc9582f fix: block crawler thumbnail generation overlap`
- 문서·검증 기록 커밋:
  - `a00e7bf docs: record video thumbnail implementation verification`
  - `docs: record final verifier remediation`(본 최종 매핑 갱신 커밋)

## 보류 항목

- 없음.

## 구현 계획서 모순

- 차단 모순은 없다.
- extractor 생성자 서명 누락은 `구현 가정`의 기존 패턴 재사용으로 해석했다.
- 계획서 첫머리는 실행 결과에 따른 원본 체크박스 갱신을 요구하지만, 계획서가 원본 checkout의 Git 무시 문서이고 사용자는 worktree 실행을 요구했다. 원본을 변경하지 않는 제약을 우선해 체크박스 대신 이 매핑 문서의 상태·검증 열을 동일 실행 기록으로 갱신한다.

## 검증 요약

- 구현 전 전체 baseline: 284/284 통과.
- IMP-001 RED: `PathCleanup` 타입 부재로 예상 컴파일 실패 확인. 테스트 import 오류를 제거한 뒤 기능 부재만 남는 RED를 재확인했다.
- IMP-001 GREEN: `dotnet test ... --filter FullyQualifiedName~PathCleanupTests` — 5 통과, 0 실패.
- IMP-002 RED/GREEN: 시점 타입 부재 RED 뒤 고정 구간 테스트 3개 통과.
- IMP-003~IMP-006 RED/GREEN: 생성자·`ExtractAsync` 부재 RED 뒤 추출기 단위 테스트 9개 통과. gate/취소 경합으로 대기 형제 1개가 진입한 실패를 gate 직후 토큰 재확인으로 수정했다.
- IMP-007 통합: 실제 번들 FFmpeg/H.264 JPEG 테스트 1개 통과(5장, JPEG, 가로 상한, 16:9).
- IMP-008~IMP-011 RED/GREEN: 선택 창 타입 부재 RED 뒤 WPF 테스트 12개 통과. 잘못된 후보 수 validation을 base Window 생성 전 수행해 Dispatcher 창 누수를 제거했다.
- IMP-012: 기존 ViewModel JPEG cache 적용 회귀 2개 통과, production ViewModel 변경 없음.
- IMP-013~IMP-021 RED/GREEN: 생성 handler 부재 RED 뒤 F1 수명주기·캐시 행렬 18개와 ViewModel 2개, 총 20개 통과. X 닫기 재진입은 공통 닫기 시작의 `Task.Yield()` 한 번으로 해결했다.
- 실제 UI 스모크에서 생성 미리보기의 기본 WPF URI 디코더가 cache 파일 핸들을 유지해 F1 취소 정리가 실패하는 결함을 재현했다. 유효 JPEG 미리보기 실현 뒤 취소하는 회귀 테스트의 실패를 확인하고, `OnLoadImageSourceConverter`로 스트림을 완전히 읽고 닫도록 수정했다. 신규 회귀 1개와 Task 4 묶음 21개가 통과했다.
- 독립 검증관 1차 구현 결과 판정은 `불만족`이었다. 필수 보완은 IMP-016 crawler→생성 경합, IMP-024 수동 시나리오 3종, 전체 변경 범위 최신화였고 그 외 IMP-001~015·017~023·025는 `만족`이었다.
- IMP-016 보완 RED/GREEN: crawler 상태에서 생성 버튼·handler가 모두 차단되지 않고 생성 상태 해제가 crawler 소유 컨트롤을 재활성화하는 두 실패를 재현했다. 상태 두 개를 함께 계산하는 `UpdateActionAvailability`와 생성 handler guard로 수정한 뒤 신규 2개와 Task 4 묶음 23개가 통과했다.
- 신규 범위 최종 검증: 계획 필터 명령 — 50 통과, 0 실패.
- 전체 품질 게이트: restore 성공, Debug build 경고 0·오류 0, 전체 테스트 333 통과·0 실패.
- 정적 검사: 필수 변경 파일 placeholder 0개, `git diff --check b8388cf..HEAD` exit code 0, worktree `git status --short` 출력 없음.
- graphify 최종 갱신: `graphify update .` exit code 0, 2,502 nodes/4,799 links, 최종 `graph.json` SHA-256 `464688D2688D1819236B2344D337FBF30D9CD76222379C920F9B8C3BFD72EAF7`. `MetadataEditorWindow`의 새 상태·handler 관계를 `explain`으로 확인했다. `hooks.json` zero-node 경고 1건은 기존 비코드 입력에 대한 비차단 경고다.
- 수동 UI 스모크: 실제 H.264 fixture로 F1을 열어 버튼 X 좌표가 `파일 선택` 2582 → `썸네일 생성` 2690 → `초기화` 2808 순임을 확인했다. 후보 5개, 포인터 다시 생성 후 새 batch 5개, Space 선택 상태·선택 버튼 활성화, 마지막 후보 다음 Tab이 다시 생성, 이후 Tab이 취소→선택 순서를 확인했다.
- 수동 UI 재생성 실패: 첫 batch 5장의 SHA-256을 기록한 뒤 검증 source를 제거해 probe 실패를 유도했다. `썸네일을 다시 생성하지 못했습니다` 오류 후 선택 창이 기존 후보 5장으로 다시 열렸고, batch-0001 5장과 해시는 그대로, batch-0002 결과는 0장, FFmpeg는 0개였다.
- 수동 F1 종료 행렬: 추출 중에는 생성 버튼 비활성·세션 존재를 확인한 상태에서 Cancel/X/Escape 각각을 실행했고, 선택 중에는 후보 5장·세션 존재 상태에서 같은 3동작을 실행했다. 6경로 모두 F1·선택 창 닫힘, 세션 0, FFmpeg 0이었다.
- 수동 저장 cache 행렬: 같은 F1 세션에서 두 번 생성·선택해 cache 2개를 만든 뒤 저장했다. 첫 cache `FORESTGUMP_20260718_121905175.jpg`는 삭제되고 최종 `FORESTGUMP_20260718_121905619.jpg`만 남아 cache 2→1, 세션 0, FFmpeg 0을 확인했다. 앱 종료 뒤 검증 source와 최종 cache는 정확한 경로로 제거했다.
- 수동 UI 후속: 선택 완료 시 세션 루트 0·FFmpeg 0, F1 취소 시 정리 경고 없음·생성 cache 0·세션 루트 0·FFmpeg 0을 확인했다. 주입이 필요한 정리 실패 절대 경로 경고만 대응 WPF 자동 테스트로 보완했다.
- Computer Use 플러그인의 네이티브 파이프가 이 세션에서 열리지 않아, 실제 앱 UI는 Windows UI Automation과 포인터/키보드 입력으로 동일 체크리스트를 수행했다.
- 범위 보존: 기준점 대비 변경은 기능 직접 관련 13개 파일뿐이며 하드웨어 가속·제품 timeout·설정 UI·전역 청소기는 없다. 원본 checkout은 시작 때와 동일하게 사용자 수정 2개만 unstaged이고 staged 변경은 없다.
- 독립 검증관 매핑 검토: 전체 재검토 후 `IMP-001~IMP-025` 매핑 `만족`, 필수 보완 없음.

## 남은 리스크

- WPF 작업 모달과 F1 비동기 닫기는 Dispatcher 순서에 민감하지만 계획의 생성 중·선택 중 닫기 행렬과 실제 UI 취소 경로를 검증했다.
- `ThumbnailCache` 파일명은 시각 기반이지만 빠른 연속 두 생성 테스트에서 서로 다른 경로가 확인됐다.
- worktree의 graphify 산출물은 무시되므로 완료 시 명령 결과와 관계 확인을 매핑에 기록하되 커밋 대상에는 포함하지 않는다. 복제된 기준 graph가 커서 증분 명령 전후 노드 관계를 명시적으로 대조한다.
