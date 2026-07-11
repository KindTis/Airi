# 비디오 썸네일 지연 로딩 구현 항목 매핑

## 구현 항목 매핑표

| 구현 항목 ID | 구현 계획서 항목 | 수용 기준 | 구현 대상 | 구현 상태 | 검증 방법 | 검증 결과 | 보류 사유 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| T0-1 | Task 0 / 기준 상태 기록 | 사용자 변경을 보존하고 기존 전체 테스트, Debug/Release 빌드 결과와 측정 commit을 기록한다. | 작업 트리, `Airi.sln`, `tests/Airi.Tests/Airi.Tests.csproj`, 성능 문서 | 구현 | `git status --short`; `git rev-parse HEAD`; 전체 Debug 테스트; Debug/Release 빌드 | 통과 |  |
| T0-2 | Task 0 / 공통 성능 측정 shell | normal app에서는 no-op인 probe, process-wide STA host, production XAML 기반 cold/warm harness, 고립된 fixture runner와 공통 JSON schema를 만든다. 기존 URI binding과 layout 동작은 baseline 단계에서 유지한다. | `AssemblyInfo.cs`, test csproj, `App.xaml`, `App.xaml.cs`, `MainWindow.xaml.cs`, `MainViewModel.cs`, `Infrastructure/ThumbnailPerformanceProbe.cs`, `tests/Airi.Tests/WpfTestHost.cs`, `tests/Airi.Tests/ThumbnailPerformanceHarnessTests.cs`, `scripts/Measure-ThumbnailStartup.ps1` | 구현 | harness 필터 테스트; Debug 빌드; baseline `current` cold/warm 5회 raw/summary JSON | 통과 |  |
| T0-3 | Task 0 / 변경 전 baseline | 동일 marker와 warm lifecycle로 `current=662`의 cold/warm을 각각 5회 측정하고 실제 median/worst 및 출처를 기록한다. | `docs/performance/2026-07-11-video-thumbnail-lazy-loading.md`, `docs/performance/raw/2026-07-11-thumbnail-baseline/*` | 구현 | baseline runner 5회; schema/marker/build provenance/환경 호환성 확인 | 통과 |  |
| T1 | Task 1 / WPF host와 virtualizing package gate | `VirtualizingWrapPanel` 2.5.2가 target framework와 호환되고, 하나의 STA dispatcher host에서 WPF 테스트가 직렬화되며, production `VideoList`가 recycling·pixel scroll·page cache를 사용한다. | `Airi.csproj`, `MainWindow.xaml`, `tests/Airi.Tests/WpfTestHost.cs`, `tests/Airi.Tests/VideoListVirtualizationTests.cs`, 기존 ViewModel 테스트 helper | 구현 | package contract red/green; restore; virtualization tests; Debug build 경고 0 | 통과 |  |
| T2 | Task 2 / `VideoItem` runtime 상태와 loader interface | persistence schema를 바꾸지 않고 `NotRequested/Loading/Loaded/Failed`, frozen source 결과와 지정된 loader signature를 제공한다. | `Video.cs`, `Infrastructure/IThumbnailImageLoader.cs`, `tests/Airi.Tests/VideoItemThumbnailStateTests.cs` | 구현 | 상태 전이 red/green test; Debug build | 통과 |  |
| T3-1 | Task 3 / bounded thumbnail loader | app-base 상대 경로, frozen fallback, UI 밖 축소 decode, 최대 동시성 4, `(path,width,lastWriteUtc,length)` stamp, 96-entry LRU, retry/failure de-dup 계약을 구현한다. | `Infrastructure/ThumbnailImageLoader.cs`, `Infrastructure/ThumbnailPerformanceProbe.cs`, `tests/Airi.Tests/ThumbnailImageLoaderTests.cs` | 구현 | 계획서의 loader 계약 22개 red/green test; diagnostics invariant; Debug build | 통과 |  |
| T3-2 | Task 3 / async app startup | fallback factory를 비동기로 한 번 실행하고 종료·중복·fault 뒤 ghost window를 만들지 않는다. | `App.xaml`, `App.xaml.cs`, `Infrastructure/AppStartupCoordinator.cs`, `MainWindow.xaml.cs`, `tests/Airi.Tests/AppStartupTests.cs` | 구현 | delayed/faulted/concurrent coordinator tests; Debug build | 통과 |  |
| T4 | Task 4 / thumbnail generation·identity lifecycle | item별 monotonic generation과 request identity로 stale completion을 막고, terminal tracking과 in-flight slot을 분리하며 release/remove/replace/dispose가 source와 상태를 정리한다. | `ViewModels/MainViewModel.cs`, `MainWindow.xaml.cs`, `tests/Airi.Tests/MainViewModelThumbnailTests.cs`, 기존 fixture | 구현 | 계획서의 request lifecycle 17개 red/green test; skeleton/crawler 회귀; Debug build | 통과 |  |
| T5 | Task 5 / realized image registration | card image만 `Loaded/DataContextChanged/Unloaded`를 통해 요청하고 tooltip은 source만 공유한다. DPI 폭은 `ceil(ActualWidth*DpiScaleX)`를 64..520으로 clamp하며 다중 image count와 recycling 순서를 보존한다. | `MainWindow.xaml`, `MainWindow.xaml.cs`, `tests/Airi.Tests/MainWindowThumbnailRealizationTests.cs` | 구현 | 계획서의 realization 13개 red/green test; thumbnail VM 회귀; Debug build | 통과 |  |
| T6 | Task 6 / same-directory atomic library save | load/save의 filesystem·projection·serialize/deserialize·flush·commit을 STA 밖에서 수행하고, cancellation linearization point 전에는 old, 이후에는 new 완전본을 남긴다. caller graph는 변경하지 않고 owned stale temp만 정리한다. | `Services/ILibraryStore.cs`, `Services/LibraryStore.cs`, `ViewModels/MainViewModel.cs`, `tests/Airi.Tests/LibraryStoreTests.cs`, 기존 fixture | 구현 | 계획서의 atomic/load 18개 red/green test; destination deserialize/bytes 검증; Debug build | 통과 |  |
| T7 | Task 7 / startup lifecycle와 mutation lease | 허용된 lifecycle edge만 사용하고 `StartupScan/AutoMetadata/ManualFetch/EditorSave` 중 dispatcher-owned writer 하나만 허용한다. read-only UI는 유지하고 Loaded once, close cancellation, fault observation을 보장한다. | `Services/ILibraryScanner.cs`, `Services/LibraryScanner.cs`, `ViewModels/MainViewModel.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`, startup/window tests | 미착수 | lifecycle/lease/window red/green tests; crawler 회귀; Debug build | 미검증 |  |
| T8 | Task 8 / stored library batch publish | 전체 선행 mapping을 제거하고 background mapping·dispatcher 40개 batch로 게시한다. 첫 실제 batch에서 video skeleton, 최종 actor snapshot에서 actor skeleton을 끝내며 partial failure는 persisted state로 복구하거나 `Faulted`가 된다. | `ViewModels/MainViewModel.cs`, `tests/Airi.Tests/MainViewModelStartupLoadingTests.cs`, `tests/Airi.Tests/MainViewModelSkeletonTests.cs` | 미착수 | stored publishing red/green tests; callback batch size/ordering/thread/점유시간 확인; Debug build | 미검증 |  |
| T9 | Task 9 / immutable scan plan과 recoverable commit | dispatcher seed capture 뒤 background deep clone으로 immutable plan을 만들고, 첫 mutation 전 operation cancel만 허용한다. 이후 40개 batch, atomic save, lease handoff를 끝내며 failure는 persisted state로 복구하고 항목별 로그는 summary 하나로 축소한다. | `ViewModels/ScanApplyPlan.cs`, `ViewModels/MainViewModel.cs`, `tests/Airi.Tests/MainViewModelStartupLoadingTests.cs` | 미착수 | scan/plan/save/recovery red/green tests; immutability/thread guard; Debug build | 미검증 |  |
| T10 | Task 10 / 모든 library mutation 직렬화 | manual fetch, auto metadata, editor dialog/save가 실행 시점에 같은 lease identity 규칙을 사용하고 store save 최대 동시 실행 수가 1이다. scan status와 read-only interaction을 보존한다. | `ViewModels/MainViewModel.cs`, `MainWindow.xaml.cs`, startup/crawler tests | 미착수 | mutation race red/green tests; crawler/read-only 회귀; Debug build | 미검증 |  |
| T11-1 | Task 11 / virtualization·recycling 통합 hard gate | 200/1,000 항목의 top/middle/last container가 계산 상한 이하이고 request membership, file-open bound, concurrency, LRU, source owner bound가 모두 충족된다. | virtualization/realization/loader/VM tests, `Infrastructure/ThumbnailPerformanceProbe.cs` | 미착수 | 구조 통합 테스트 전체; 500ms steady barrier; hard-gate assertions | 미검증 |  |
| T11-2 | Task 11 / Release cold·warm 측정 | 네 dataset을 각각 새 Release process에서 5회 실행해 40 phase snapshot을 만들고 모든 적용 hard gate가 5/5 통과한다. baseline 대비 관찰 목표는 실제 median/worst로 기록하며 추정하지 않는다. | performance harness, runner, raw/summary JSON, 성능 문서 | 미착수 | Release build; measurement script; schema/fixture/quiescence/environment 검증 | 미검증 |  |
| T12 | Task 12 / 전체 회귀·smoke·graph | 관련 회귀, 전체 테스트, Debug build 경고 0, production GUI smoke, `graphify update .`를 완료하고 사용자 변경과 관련 없는 파일은 건드리지 않는다. | 전체 저장소, `graphify-out/*` | 미착수 | 계획서의 관련 필터; 전체 Debug test/build; 수동 GUI 체크리스트; graphify update/status | 미검증 |  |

## 구현 가정

- 사용자가 `master 브랜치에 바로 작업`을 명시했으므로 MyLoop의 기본 `main`/worktree 규칙보다 이 지시를 우선한다. 현재 브랜치는 `master`다.
- 기준 스펙과 구현 계획서는 사용자 소유의 추적되지 않은 파일이므로 내용을 보존하고, 계획에 명시되지 않은 시점에 임의 stage하지 않는다.
- 이미 승인된 기준 스펙과 구현 계획서가 architecture, data flow, error handling, tests 및 두 추가 결정을 확정했으므로 별도 설계 재인터뷰 없이 해당 문서를 brainstorming 승인 결과로 사용한다.
- MyLoop는 검증관 서브에이전트 1개만 유지하라고 명시하므로 일반 다중 implementer 방식 대신 현재 메인 에이전트가 `$executing-plans` 절차로 구현하고 동일 검증관이 반복 검토한다.
- performance raw JSON의 pseudonymous machine label은 개인 식별 정보를 담지 않는 고정값 `local-windows`를 사용한다.
- `bin/Release/net9.0-windows10.0.26100.0`에 662-entry `videos.json`과 cache가 존재하므로 `current` fixture의 기준 source로 사용하되, 측정은 전부 temporary copy에서 수행한다.
- small/medium/stress fixture는 current 파일을 수정하지 않고 runner가 고정 seed와 독립 media/thumbnail payload로 준비·검증하게 한다.

## 보류 항목

- 없음. 다만 실제 monitor가 1500×1000 DIP, 100% DPI 조건을 충족하지 않으면 계획서 규칙에 따라 해당 performance phase를 `invalid`로 기록하고 timing을 추정하지 않는다. 이는 구현 보류가 아니라 환경 검증 결과다.

## 구현 계획서 모순

- 계획서 상단은 `subagent-driven-development`를 권장하지만 MyLoop `better-run-impl`은 검증관 외 추가 에이전트를 만들지 않고 같은 검증관 하나를 유지하도록 더 구체적으로 제한한다. 따라서 구현은 `$executing-plans`로 수행한다.
- `executing-plans`의 일반 규칙은 master/main 직접 작업을 금지하지만 사용자가 이번 요청에서 `master 브랜치에 바로 작업`을 명시적으로 승인했다.
- Task 0의 baseline은 최종 harness와 공통 marker/schema를 요구하면서 아직 after loader가 없는 legacy URI binding을 측정한다. 공통 visual marker ID와 fixture lifecycle은 동일하게 유지하고, legacy/after eligibility 판정만 계획서가 정의한 두 branch로 분리한다.

## 검증 요약

- 기준 commit: `c4f32287a4377d0b97cf3370b8892560d68e8f1c`.
- 기준 작업 트리: `docs/superpowers/plans/2026-07-11-video-thumbnail-lazy-loading.md`, `docs/superpowers/specs/2026-07-10-video-thumbnail-lazy-loading-design.md` 두 파일만 추적되지 않은 사용자 변경으로 존재했다.
- 최초 sandbox 기준 검증은 NuGet endpoint 소켓 접근 제한으로 `NU1301` 실패했다. 권한 승인 후 동일 command를 다시 실행했다.
- 기준 전체 테스트: `dotnet test tests/Airi.Tests/Airi.Tests.csproj -c Debug` 실패 0, 통과 61, 건너뜀 0.
- 기준 Debug build: 경고 0, 오류 0.
- 기준 Release build: 경고 0, 오류 0.
- Task 0 공통 harness: `dotnet test ... --filter FullyQualifiedName~ThumbnailPerformanceHarnessTests`에서 8개 통과. production window test는 실제 `BitmapFrameDecode` URI와 decoded pixel을 canonical source 증거로 검증했다.
- Task 0 baseline: Release build 경고 0/오류 0, `current` iteration 5개가 각각 정확히 1개 performance test를 통과했다. cold/warm 10 phase가 모두 100% DPI·1920×1032 DIP 환경에서 valid였다.
- Task 0 baseline median/worst: cold card 8,363.5148/12,484.1864ms, cold thumbnail 852.4484/1,395.0628ms, warm card 4,894.7996/5,031.6384ms, warm thumbnail 215.1604/227.8298ms. baseline realized container는 cold/warm 모두 662개였다.
- Task 1 package RED: `WpfToolkit` namespace/type 미존재 compile failure를 확인했다. exact `VirtualizingWrapPanel` 2.5.2 restore 뒤 contract test가 통과했고 Debug build 경고 0/오류 0이었다.
- Task 1 구조 검증: `VideoListVirtualizationTests` 3개와 WPF host로 이관한 skeleton/crawler/harness 관련 필터 총 31개가 실패 0으로 통과했다. production XAML의 recycling, pixel scroll, `(1,1)` page cache와 horizontal wrap/vertical extent를 확인했다.
- Task 2 RED에서 thumbnail runtime member 미존재 compile failure를 확인했다. 구현 후 `VideoItemThumbnailStateTests` 2개가 통과했고 Debug build 경고 0/오류 0이었다. reflection test로 `VideoEntry` persistence model에 runtime 속성이 추가되지 않았음을 확인했다.
- Task 3 loader RED에서 concrete loader/test seam 미존재 compile failure를 확인했다. 구현 후 계획서의 loader 계약 22개가 모두 통과했다. 실제 최대 decode concurrency 4, cache/recency 96/96, cache hit file-open 억제, app-base 상대 경로, case-insensitive key, length 포함 stamp, 두 attempt file-change와 retry counter를 검증했다.
- Task 3 파일 교체 test는 이 Windows runtime에서 열린 destination에 대한 `File.Move(..., overwrite:true)`가 `UnauthorizedAccessException`을 반환함을 별도 재현했다. 동일 `FileShare.Delete` handle에서 delete+move path replacement는 성공하므로 test fixture는 그 방식으로 서로 다른 stamp 교체를 만들고 old pixels 폐기/retry 계약을 검증했다.
- Task 3 startup coordinator RED에서 type 미존재 compile failure를 확인했다. delayed completion after cancel, concurrent start one-window, factory fault one-log/one-shutdown 3개가 통과했다. loader/startup/harness/virtualization 관련 필터 총 36개와 Debug build 경고 0/오류 0이 통과했다.
- Task 4 RED에서 request/release/diagnostics/loader-injected constructor 미존재 compile failure를 확인했다. 구현 후 계획서의 generation/identity lifecycle 17개가 모두 통과했다. 같은 key 중복, path/width 교체, release와 dispatcher race, fallback/cancel terminal, replace/selection, remove/dispose cleanup을 검증했다.
- Task 4 fixture를 모두 명시적 fake/static thumbnail loader 주입으로 바꿨고 production `MainWindow`는 App에서 생성된 동일 loader를 ViewModel에 전달한다. thumbnail/skeleton/crawler/harness 관련 필터 45개와 Debug build 경고 0/오류 0이 통과했다.
- Task 5 RED에서 image registration/event/DPI/probe seam 미존재 compile failure를 확인했다. 구현 후 realization event ordering 13개가 모두 통과했다. registration dictionary와 item active count, 두 recycling 순서, 같은 item 복수 image의 2→1/1→0, close cleanup, request membership true/false를 확인했다.
- Task 5는 main card만 `ThumbnailSource` + realization event를 사용하고 tooltip은 같은 `ThumbnailSource`만 공유한다. 실제 production-window marker, VM lifecycle, virtualization을 포함한 관련 필터 41개와 Debug build 경고 0/오류 0이 통과했다.
- Task 6 RED에서 atomic save stage와 `ILibraryStore` seam 미존재 compile failure를 확인했다. 구현 후 계획서의 18개 atomic/load 계약과 missing-destination 회귀를 포함한 `LibraryStoreTests` 19개가 모두 통과했다.
- Task 6은 load/save 전체 filesystem 경계를 worker로 옮기고, same-directory temp flush 뒤 단일 cancellation gate를 거쳐 `File.Replace`/`File.Move`로 commit한다. caller graph 불변성, old/new destination 보존, primary exception 보존, valid owned stale temp만 정리하는 계약을 검증했다. skeleton/crawler 회귀 20개와 Debug build 경고 0/오류 0도 통과했다.

## 남은 리스크

- 외부 `VirtualizingWrapPanel` 2.5.2의 restore/build 또는 recycling 계약이 실패하면 계획서 hard stop에 따라 local panel을 임의 구현하지 않고 설계 재검토가 필요하다.
- 계획의 성능 harness 범위가 크고 실제 WPF render, monitor DPI, working area, process isolation에 의존한다. unit/integration green만으로 T11 완료를 주장하지 않는다.
- startup lifecycle, library mutation lease, scan recovery가 현재 하나의 큰 `MainViewModel`에 집중된다. 요청 범위를 벗어난 리팩터링은 하지 않되, 계획이 지정한 작은 interface와 immutable plan 경계로 테스트 가능성을 확보한다.
- GUI smoke는 실제 window와 입력 장치 동작을 확인해야 하며 자동 테스트를 대체하지 않는다.
