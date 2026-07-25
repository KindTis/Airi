# 정렬 및 대상 폴더 설정 분리 구현 항목 매핑

- 구현 계획서: `C:/Users/tatis/Repos/Airi/docs/superpowers/plans/2026-07-25-sort-settings-persistence.md`
- 기준 설계: `C:/Users/tatis/Repos/Airi/docs/superpowers/specs/2026-07-25-sort-settings-persistence-design.md`
- 구현 저장소: `C:/Users/tatis/Repos/Airi`
- 작업 브랜치: `feature/sort-settings-persistence`

## 구현 항목 매핑표

| 구현 항목 ID | 필수 여부 | 구현 계획서 항목 | 계획서 근거 | 수용 기준 | 구현 대상 | 구현 상태 | 검증 방법 | 검증 결과 | 항목 판정 | 보류 사유 |
|---|---|---|---|---|---|---|---|---|---|---|
| IMP-001 | 필수 | 설정 target 입력 정책 확정 | 확정 정책, Task 1 시작 게이트 | 빈·해석 불가 `Root`, 누락·빈·비정상 패턴, 빈 target 목록의 처리 결과와 scanner 호출 여부를 단일 정책으로 확정한다. 구조·값 타입 역직렬화 실패 시 두 JSON을 변경하지 않고 시작·스캔을 중단한다는 수용 기준도 확정한다. | 기준 설계의 `확정 정책`, 구현 계획서의 시작 게이트·정규화·테스트 기대값 | 구현 | 확정 정책의 설계·계획 일치, UTF-8 BOM 없음, legacy 미결정 문구 부재 확인 | 통과 | 만족 | 사용자 권장안 전체 승인 후 설계·계획·매핑에 반영했다. |
| IMP-002 | 필수 | 설정 모델 분리와 `LastScanUtc` 제거 | Task 1 Step 3 | `TargetFolder`는 root/include/exclude만 소유하고 `AppSettings`·`SortSettings`는 기본 Version 1, `./Videos`, `ReleaseDate/Descending`을 표현한다. 런타임 모델과 scan apply 경로에서 `LastScanUtc`·`ScanTimestamp`가 사라진다. | `Domain/SettingsModels.cs`, `Domain/LibraryModels.cs`, `ViewModels/ScanApplyPlan.cs`, `ViewModels/MainViewModel.cs`, 관련 테스트 | 구현 | 컴파일, 관련 단위 테스트, legacy 심볼 `rg` | 통과 | 만족 | 관련 118개 테스트 통과, 런타임 source 경로의 legacy 심볼 검색 결과 없음 |
| IMP-003 | 필수 | 설정 기본 생성·로드·정규화·파일 독립성 | Task 1 Step 1~4, 검증 3·4·7·8·15 | `settings.json`이 없으면 `videos.json`과 독립적으로 같은 디렉터리에 기본 파일을 만든다. 알 수 없는 sort와 누락 멤버는 메모리에서 기본값으로 정규화하되 기존 파일을 다시 쓰지 않는다. 존재하지 않는 비어 있지 않은 root는 보존한다. 구조·타입 오류는 두 파일을 변경하지 않는다. 새 파일과 정규화 결과 Version은 1이며, `2` 이상은 파일을 변경하지 않고 거부한다. | `Services/SettingsStore.cs`, `tests/Airi.Tests/SettingsStoreTests.cs` | 구현 | 필터된 `SettingsStoreTests`; 구조·타입 오류의 두 파일 byte 비교; 확정된 target·Version 입력별 회귀 테스트 | 통과 | 만족 | `SettingsStoreTests` 26개 통과 |
| IMP-004 | 필수 | 설정 원자 저장과 호출 순서 | Task 1 Step 4~6, 검증 10~13 | 저장 호출 시 정규화 snapshot을 만들고 호출 순서대로 직렬화한다. commit 전 취소는 기존 파일을 보존하고 commit gate 뒤 취소는 새 파일을 남긴다. 최신 실패는 앞선 성공을 보존하며 owned 임시 파일을 정리한다. | `Services/SettingsStore.cs`, `tests/Airi.Tests/SettingsStoreTests.cs` | 구현 | 취소·겹침·실패·임시 파일 회귀 테스트 | 통과 | 만족 | 호출 순서·취소 경계·최신 실패·temp 정리 테스트 통과 |
| IMP-005 | 필수 | 설정 선행 로드와 시작 정렬 복원 | Task 2 Step 1~4, 검증 6·7·14 | 라이브러리보다 설정을 먼저 읽고 빈 `FilteredVideos`에 저장 sort를 적용한다. 게시된 아이템의 선택과 실제 열거 순서가 일치한다. 설정 읽기·생성 실패는 `Faulted` 상태와 원인 메시지를 남기며 library load와 scan을 시작하지 않는다. | `ViewModels/MainViewModel.cs`, `tests/Airi.Tests/SortSettingsPersistenceTests.cs`, 기존 시작 테스트 | 구현 | `SortSettingsPersistenceTests`, `MainViewModelStartupLoadingTests`, `MainWindowStartupTests` | 통과 | 만족 | 신규 7개, 관련 시작 회귀 포함 59개 통과 |
| IMP-006 | 필수 | 정렬 변경 즉시 저장과 실패 상태 | Task 2 Step 5~7, 검증 5·10·11 | 선택 변경은 화면 정렬을 먼저 적용한 뒤 안정적인 Field/Direction을 저장한다. 빠른 연속 변경은 마지막 성공값을 파일과 재실행에 남긴다. 최신 저장 실패 시 화면 최신 선택은 유지하고 상태·`AppLogger.Error` 로그에 실패를 알리며 `videos.json`을 바꾸지 않는다. 기존 `SortOptions` 항목·라벨·순서, XAML 바인딩, 키보드·접근성 동작은 바꾸지 않는다. | `ViewModels/MainViewModel.cs`, `tests/Airi.Tests/SortSettingsPersistenceTests.cs`, 기존 `MainWindow.xaml` | 구현 | 단일 변경·연속 성공·최신 실패·재실행 테스트; 실패 catch의 `AppLogger.Error` 확인; `MainWindow.xaml`과 sort 목록 기준점 diff 없음 확인 | 통과 | 만족 | 연속 성공·최신 실패·파일 불변 테스트 통과, XAML diff 없음과 기존 sort 목록·로그 호출 확인 |
| IMP-007 | 필수 | `videos.json` 모델·저장 투영 분리 | Task 3 Step 1~3, 검증 2~4 | `LibraryData`와 새로 저장되는 JSON은 `Version`, `Videos`만 가진다. `LibraryStore`의 기존 영상 원자 저장·손상 초기화·복구는 유지한다. legacy `Targets`는 기본 역직렬화로 무시하며 설정 파일 생성이나 전달 경로를 만들지 않는다. | `Domain/LibraryModels.cs`, `Services/LibraryStore.cs`, `tests/Airi.Tests/LibraryStoreTests.cs` 및 fixture | 구현 | 저장 속성 집합·legacy 무시·기존 원자성/복구 테스트 | 통과 | 만족 | Task 3 필터 85개 통과 |
| IMP-008 | 필수 | scanner target 입력 경계 분리 | Task 3 Step 4, 검증 3·15 | `ILibraryScanner.ScanAsync`가 library와 `IReadOnlyList<TargetFolder>`를 별도 입력으로 받고 `LibraryScanner`는 전달받은 targets만 `FileSystemScanner`에 보낸다. 시작 scan은 로드된 settings targets snapshot을 전달한다. | `Services/ILibraryScanner.cs`, `Services/LibraryScanner.cs`, `ViewModels/MainViewModel.cs`, 모든 구현/fake와 테스트 | 구현 | scanner 단위 테스트, settings target 캡처 시작 테스트, 전체 컴파일 | 통과 | 만족 | settings target 캡처와 scanner diff 테스트 통과, 모든 fake 컴파일 |
| IMP-009 | 필수 | scan apply에서 target 복제·timestamp 제거 | Task 1 Step 3, Task 3 Step 5 | scan seed/snapshot/final library는 영상과 item identity만 다루고 target 복제, `LastScanUtc`, scan timestamp를 저장하지 않는다. 기존 scan diff와 복구 흐름은 유지한다. | `ViewModels/ScanApplyPlan.cs`, `ViewModels/MainViewModel.cs`, `tests/Airi.Tests/MainViewModelStartupLoadingTests.cs` | 구현 | scan snapshot 불변성·저장·복구 테스트와 legacy 심볼 검색 | 통과 | 만족 | 시작 scan 회귀 통과, legacy 검색 결과 없음 |
| IMP-010 | 필수 | 성능 fixture 파일 계약 분리 | Task 3 Step 6~7 | current/synthetic `videos.json` 템플릿은 Version/Videos만, 같은 폴더 `settings.json`은 Version/Targets/Sort만 가진다. 재사용 조건·fixture assertion·cold/warm 복사가 두 파일 계약을 확인한다. `AIRI_PERF_COLD_STORE`와 `AIRI_PERF_WARM_STORE`는 계속 각 `videos.json`을 가리키며 기존 영상·manifest·payload 검증을 유지한다. | `scripts/Measure-ThumbnailStartup.ps1` | 구현 | PowerShell scriptblock 파싱; `Assert-Fixture`의 기존 검증과 새 파일 계약 확인; 두 환경 변수 할당 대상이 `videos.json`인지 정적 확인 | 통과 | 만족 | scriptblock 파싱 통과, 기존 fixture 검증과 env 경로 유지 |
| IMP-011 | 필수 | Debug/Release 실행 데이터 직접 분리 | Task 4 Step 1~3, 검증 1·2 | 두 ignored runtime `videos.json`의 전체 Videos와 Version을 byte-equivalent JSON 값으로 보존하면서 Targets를 같은 폴더 `settings.json`으로 옮긴다. 정렬은 기본값이며 `LastScanUtc`는 두 파일 모두에 없다. 임시 파일 검증 뒤 원자 교체하고 실패 시 원본을 보존한다. | `bin/Debug/net9.0-windows10.0.26100.0/{videos,settings}.json`, `bin/Release/net9.0-windows10.0.26100.0/{videos,settings}.json` | 구현 | 변환 전후 target·video count·JSON 값·속성 집합 검증 | 통과 | 만족 | Debug 6개·Release 662개 영상과 Version의 JSON 값 보존, target·sort 분리와 legacy timestamp 제거 확인 |
| IMP-012 | 필수 | 계획의 테스트 선행·작업별 회귀 | Task 1 Step 1~7, Task 2 Step 1~7, Task 3 Step 1~7 | 각 동작 변경은 해당 실패 테스트를 먼저 확인한 뒤 최소 구현으로 통과시킨다. 계획이 지정한 필터 테스트와 기존 관련 회귀가 모두 통과한다. | 신규 `SettingsStoreTests.cs`, `SortSettingsPersistenceTests.cs`, 기존 관련 테스트 | 구현 | 각 RED/GREEN 명령의 exit code와 테스트 수 기록 | 통과 | 만족 | Task 1~3 RED 원인과 GREEN 결과 기록 완료 |
| IMP-013 | 필수 | 전체 테스트와 Debug 빌드 | Task 4 Step 4~5, 검증 9 | 전체 xUnit suite가 실패 없이 통과하고 `Airi.sln` Debug 빌드가 error 0으로 성공한다. | 전체 변경 | 구현 | `dotnet test tests/Airi.Tests/Airi.Tests.csproj -c Debug`; `dotnet build Airi.sln -c Debug` | 통과 | 만족 | 전체 375개 통과, Debug 빌드 경고 0·오류 0 |
| IMP-014 | 필수 | 지식 그래프와 정적 수용 기준 갱신 | Task 3 Step 8, Task 4 Step 6~7 | 코드 변경 뒤 `graphify update .`가 성공하고 설정 모델·저장소·scanner 관계를 반영한다. 금지된 legacy target/timestamp 경로 검색 결과가 없다. | `graphify-out/`, `Domain`, `Services`, `ViewModels`, `scripts` | 구현 | `graphify update .`, 계획의 `rg`, status 대조; 추가 정적 검사로 `git diff --check` 실행 | 통과 | 만족 | graphify 2,673 nodes/6,059 edges 갱신, legacy 검색 결과 없음, diff check 통과 |
| IMP-015 | 필수 | 범위·사용자 변경·ignored 데이터·문서 인코딩 보존 | 전역 제약, 의도적으로 제외한 작업, Task 4 Step 7 | 새 패키지·설정 인터페이스·공용 JSON 추상화·설정/복구 UI·migration·폴백·대체 scan 이력을 추가하지 않고 XAML을 수정하지 않는다. 기존 사용자 수정 설계 문서를 보존하고 runtime JSON은 강제 add하지 않는다. 이번 루프가 새로 작성하거나 수정하는 문서는 모두 UTF-8(BOM 없음)이다. | 전체 diff, Git 상태, ignored runtime 파일, 변경 문서 | 구현 | 기준점 대비 전체 diff/status와 패키지/XAML/금지 경로 검사; 변경 Markdown byte preamble 검사 | 통과 | 만족 | XAML·패키지 변경 없음, runtime은 ignored 유지, 문서 BOM 없음 |
| IMP-016 | 필수 | 작업별 의도적 커밋 | Task 1 Step 7, Task 2 Step 8, Task 3 Step 9 | 각 task의 필터 검증을 통과한 변경만 계획서의 메시지로 커밋한다. 검증관 만족 전 병합·푸시·브랜치 삭제·worktree 정리는 하지 않는다. | Git 이력과 작업 트리 | 구현 | `git log`, `git status`, 각 커밋 직전 검증 결과 | 통과 | 만족 | Task 1 `25e7717`, Task 2 `e2b8416`, Task 3 `c17c9dd`; 병합·푸시·정리 미실시 |

## 구현 가정

- 사용자의 “작업 브랜치를 만들고 구현” 요청은 현재 checkout의 시작 HEAD에서 새 브랜치를 만들고 전환할 권한으로 해석했다. 사용자 수정 중인 기준 설계 문서를 그대로 동반하는 것이 결정 근거 보존에 가장 안전하므로 별도 worktree는 만들지 않았다.
- MyLoop `better-run-impl`이 `superpowers:subagent-driven-development` 사용을 금지하므로 계획서가 허용한 대안인 `superpowers:executing-plans`를 사용한다. 독립 검증관은 MyLoop가 요구하는 읽기 전용 검증 역할만 수행한다.
- 계획서 체크박스의 실행 상태는 이 매핑표의 `구현 상태`, `검증 결과`, `항목 판정`과 task별 커밋으로 기록한다. 정책 결정으로 계획서 본문을 갱신해야 하는 경우에만 계획서를 수정한다.
- 승인된 확정 정책은 설계·계획 문서에 반영했으며, `SettingsStore`는 그 입력별 결과만 구현한다.
- 계획서가 구체 코드 블록을 제공해도 현재 코드와 컴파일 계약을 대조한 뒤 같은 동작을 만족하는 최소 diff만 적용한다.

## 변경 기준점

- 시작 커밋: `738681047ae9311350da539de740c45df8ea7712`
- 시작 브랜치: `master` (`origin/master`보다 2커밋 앞섬)
- 구현 브랜치: `feature/sort-settings-persistence`
- 초기 staged: 없음
- 초기 unstaged:
  - `docs/superpowers/specs/2026-07-25-sort-settings-persistence-design.md` — 사용자 기존 변경, 54 additions / 8 deletions, SHA-256 `DCE6A2A6A4CD82A39153171C557845ED8492D0FA0FDAC0180D2A7C0C077484CD`
- 초기 untracked: 없음
- 무시된 runtime 기준:
  - Debug `videos.json`: `Version,Targets,Videos`, target `./Videos` / `*.mp4,*.mkv,*.avi`, 6 videos, SHA-256 `1CC24B98D6DD3C5A9D4B92D3E9F9A0FE9BE94A59E6D401AC8FA405CD6DD3F59F`, `settings.json` 없음
  - Release `videos.json`: `Version,Targets,Videos`, target `e:/Fascinating/Jap` / `*.mp4,*.mkv,*.avi,*.wmv`, 662 videos, SHA-256 `6762FA2FD27090545CBEB89146BD57AE9A9EAEC8219101E0F07561A1F74A3CBA`, `settings.json` 없음
- 이번 루프가 생성하거나 수정한 경로:
  - `docs/implementation/2026-07-25-sort-settings-persistence-implementation-map.md`
  - `docs/superpowers/plans/2026-07-25-sort-settings-persistence.md`
  - `docs/superpowers/specs/2026-07-25-sort-settings-persistence-design.md` — 기존 사용자 변경을 보존하며 확정 정책 영역만 추가 수정
  - `Domain/LibraryModels.cs`
  - `Domain/SettingsModels.cs`
  - `Services/LibraryStore.cs`
  - `Services/SettingsStore.cs`
  - `Services/ILibraryScanner.cs`
  - `Services/LibraryScanner.cs`
  - `ViewModels/MainViewModel.cs`
  - `ViewModels/ScanApplyPlan.cs`
  - `scripts/Measure-ThumbnailStartup.ps1`
  - `tests/Airi.Tests/FileSystemScannerTests.cs`
  - `tests/Airi.Tests/LibraryScannerTests.cs`
  - `tests/Airi.Tests/LibraryStoreTests.cs`
  - `tests/Airi.Tests/MainViewModelStartupLoadingTests.cs`
  - `tests/Airi.Tests/MainWindowStartupTests.cs`
  - `tests/Airi.Tests/SettingsStoreTests.cs`
  - `tests/Airi.Tests/SortSettingsPersistenceTests.cs`
  - `tests/Airi.Tests/ThumbnailPerformanceHarnessTests.cs`
- 이번 루프 중 생성한 커밋:
  - `25e7717 feat: add atomic settings store`
  - `e2b8416 feat: persist and restore selected sort`
  - `c17c9dd refactor: separate scan settings from library data`

## 보류 항목

- 없음.

## 구현 계획서 모순

- target·오류 정책 미결정과 Version 계약 충돌은 사용자 권장안 전체 승인으로 해소했고 설계·계획 문서에 같은 정책을 반영했다.
- 계획서 첫머리의 권장 `superpowers:subagent-driven-development`는 MyLoop 하위 스킬이 금지한다. 계획서가 함께 허용한 `superpowers:executing-plans`를 사용하므로 실행 방식 충돌은 해소된다.

## 검증 요약

- 구현 전 저장소 구조를 `graphify query`와 관련 source/test 호출자 검색으로 대조했다.
- 구현 전 전체 테스트 baseline: `dotnet test tests/Airi.Tests/Airi.Tests.csproj -c Debug` — 339 통과, 0 실패, 0 건너뜀.
- 구현 전 Debug build baseline: `dotnet build Airi.sln -c Debug` — 경고 0, 오류 0.
- baseline 테스트 빌드 뒤 Debug/Release `videos.json` SHA-256이 변경되지 않았고 두 폴더 모두 `settings.json`이 없는 상태를 유지했다.
- 확정 정책 문서 검증: 설계·계획에서 legacy 미결정 문구와 임시 helper가 제거됐고, 세 Markdown 모두 UTF-8 BOM 없음이며 `git diff --check`가 통과했다.
- Task 1 RED: `SettingsStoreTests` 추가 후 `AppSettings` 타입 부재 `CS0246`으로 예상 실패했다.
- Task 1 GREEN: `SettingsStoreTests` 26개 통과. 변경 영향 범위 필터 118개 통과. `LastScanUtc|ScanTimestamp` source/test 검색 결과와 settings temp 잔존 파일이 없고 `git diff --check`가 통과했다.
- Task 2 RED: `SortSettingsPersistenceTests` 추가 후 `SettingsSaveTask` 부재 `CS1061`과 `settingsStore` 생성자 인자 부재 `CS1739`로 예상 실패했다.
- Task 2 GREEN: 신규 7개 통과, `SortSettingsPersistenceTests|MainViewModelStartupLoadingTests|MainWindowStartupTests` 59개 통과. `MainWindow.xaml`은 Task 1 HEAD 대비 변경 없고 기존 sort 목록·실패 `AppLogger.Error` 호출을 확인했다.
- Task 3 RED: 관련 32개 중 `videos.json`에 `Targets`가 남는 2개와 settings target이 scanner에 전달되지 않는 1개가 의도대로 실패했다.
- Task 3 GREEN: 계획 필터 85개 통과, PowerShell scriptblock 파싱 통과. `LastScanUtc|_library.Targets|library.Targets|data.Targets|TargetReferences|ScanTimestamp` source/script 검색 결과 없음.
- Task 4 runtime 변환: Debug/Release `Videos`와 `Version` JSON 값을 보존하며 `videos.json` 속성을 `Version,Videos`로, `settings.json` 속성을 `Version,Targets,Sort`로 분리했다. 영상 수 6/662, 기본 sort, legacy timestamp 부재를 확인했다.
- 구현 후 전체 테스트 375개 통과, Debug 빌드 경고 0·오류 0, `graphify update .` 성공(2,673 nodes/6,059 edges), 최종 legacy 검색과 `git diff --check` 통과.
- MyLoop 동일 독립 검증관의 전체 구현 재검토 결과: IMP-001~IMP-016 모두 `만족`, 필수 보완 없음.

## 남은 리스크

- runtime JSON은 Git 무시 파일이므로 변환 직전·직후 같은 프로세스의 JSON 값 비교 기록으로 보존을 증명한다.
- 테스트에는 legacy JSON 무시 동작을 검증하기 위한 `LastScanUtc` 문자열이 의도적으로 남아 있으며 런타임 경로에는 없다.
- 최종 검증관이 사용자 설계 문서 diff와 runtime JSON 증거를 대조했으며 구현 결함이나 필수 보완을 찾지 않았다.
