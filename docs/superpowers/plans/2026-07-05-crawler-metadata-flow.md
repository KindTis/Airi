# Crawler Metadata Flow 구현 계획서

> 에이전트 작업자 필수 조건: 이 계획을 실제 코드에 반영할 때는 `superpowers:subagent-driven-development` 또는 `superpowers:executing-plans`를 사용한다. 각 작업은 체크박스 단위로 추적한다.

## 기준 문서

- 기준 스펙: `docs/superpowers/specs/2026-07-05-crawler-metadata-flow-design.md`
- 수정 대상: 이 구현 계획서

## 목표

`Fetch Metadata`와 `Try Parse On 141jav`가 크롤러 세션을 자동으로 확보하고, bulk 메타데이터 적용은 `WebMetadataService.EnrichAsync` source 파이프라인으로 통합한다.

성공 상태:

- 메인 화면 `Fetch Metadata`는 크롤러 세션이 없으면 ChromeDriver를 자동 시작한 뒤 썸네일 누락 항목만 처리한다.
- 편집 다이얼로그 `Try Parse On 141jav`도 크롤러 세션이 없으면 자동 시작하지만, 저장 전에는 library를 직접 변경하지 않는다.
- 141jav bulk 결과 적용은 `WebMetadataService.EnrichAsync`를 통해서만 이루어진다.
- 141jav source가 부분 결과를 반환하면 해당 query는 성공으로 끝나고 NanoJav fallback을 호출하지 않는다.
- `Start Crawler` UI 버튼은 사라지지만 `StartCrawlerCommand`는 public command로 유지된다.
- 활성 또는 시작 중인 ChromeDriver crawler session은 항상 하나만 존재한다.

## 비범위

- 141jav HTML selector 정확도 개선.
- Selenium/ChromeDriver 패키지 버전 변경.
- NanoJav source 동작 변경.
- 메타데이터 편집 다이얼로그의 전체 MVVM 전환.
- source별 field-level merge, source filter, 품질 점수 기반 merge.
- 날짜, 배우, 태그, 설명만 누락된 항목을 bulk 대상으로 확장.

## 핵심 설계

### 책임 분리

| 구성 요소 | 책임 |
| --- | --- |
| `MainWindow` | provider, 141jav source, factory를 한 번 생성하고 같은 instance를 `WebMetadataService`와 `MainViewModel`에 주입한다. |
| `MainViewModel` | crawler 자동 시작, handle/session/provider 수명, bulk 결과의 library/UI 반영, 편집 다이얼로그 helper 제공. |
| `CrawlerSessionProvider` | 현재 사용할 수 있는 `IOneFourOneJavCrawlerSession`을 read-only provider interface로 노출한다. ChromeDriver 수명은 소유하지 않는다. |
| `OneFourOneJavCrawlerSessionFactory` | Selenium `ChromeDriverService`, `ChromeOptions`, `ChromeDriver` 생성과 실패 cleanup만 담당한다. |
| `OneFourOneJavMetaSource` | 현재 session으로 141jav 검색 URL 이동, metadata 파싱, thumbnail bytes 다운로드 후 `WebVideoMetaResult`를 반환한다. |
| `WebMetadataService` | source 순회, 첫 성공 source 확정, 기존 metadata 병합, thumbnail cache 저장, 설명 번역. |
| `MetadataEditorWindow` | `TryFetchOneFourOneJavMetadataAsync` 결과를 dialog ViewModel에만 반영한다. |

### 반드시 지킬 불변식

- `MainViewModel`만 provider session을 set/clear한다.
- `OneFourOneJavMetaSource`는 ChromeDriver를 생성하거나 종료하지 않는다.
- `OneFourOneJavMetaSource.FetchAsync`는 `SemaphoreSlim`으로 같은 source instance의 navigation/parse를 직렬화한다.
- `OneFourOneJavMetaSource`는 날짜, 배우, 태그, 설명, thumbnail 중 하나라도 실제 값이 있을 때만 non-null 결과를 반환한다. 빈 `CrawlerMetadata` 객체만으로 성공 처리하지 않는다.
- `MainViewModel.StartCrawlerAsync`는 별도 start gate로 자동 시작을 직렬화한다. bulk fetch와 editor helper가 동시에 session을 요구해도 `factory.StartAsync`는 한 번만 호출되어야 한다.
- 시작 gate 안에서는 기존 handle/session/provider를 다시 확인한다. 이미 다른 호출이 session을 만든 경우 새 ChromeDriver를 만들지 않고 그 session을 재사용한다.
- `DisposeCrawler`와 monitor cleanup은 provider session을 먼저 `null`로 만든 뒤 handle을 dispose한다.
- 스캔 후 자동 metadata queue는 crawler를 자동 시작하지 않는다. session이 없으면 141jav source가 `null`을 반환하고 NanoJav fallback으로 넘어간다.
- `TryFetchOneFourOneJavMetadataAsync(query, cancellationToken)`의 token은 `EnsureCrawlerReadyAsync`, `StartCrawlerAsync`, `factory.StartAsync`, `OneFourOneJavMetaSource.FetchAsync`까지 전달한다.
- `OperationCanceledException`은 cancellation token을 받는 helper/source/factory 경로에서 삼키지 않는다. `StartCrawlerCommand`와 MVP의 `FetchMetadataCommand`는 별도 취소 UI가 없으므로 `CancellationToken.None`을 사용한다.

## 데이터 흐름

### 메인 화면 `Fetch Metadata`

1. `FetchMetadataCommand`가 `FetchMissingMetadataWithCrawlerAsync`를 실행한다.
2. 이미 `IsFetchingMetadata`이면 status만 갱신하고 종료한다.
3. `IsMetadataIncomplete`가 true인 항목만 대상으로 잡는다. MVP 기준은 thumbnail URI가 비어 있거나 fallback thumbnail인 경우다.
4. 대상이 없으면 status를 갱신하고 종료한다.
5. `EnsureCrawlerReadyAsync`가 session을 보장한다. session이 없으면 start gate를 통해 factory를 한 번만 호출한다.
6. 각 대상 entry에 대해 query를 만든다.
7. `_webMetadataService.EnrichAsync(entry, query, CancellationToken.None)`를 호출한다.
8. source 목록의 `OneFourOneJavMetaSource`가 현재 session으로 141jav를 시도한다.
9. 141jav가 `WebVideoMetaResult`를 반환하면 `WebMetadataService`가 기존 병합/thumbnail 저장/번역을 수행하고 NanoJav는 호출하지 않는다.
10. `MainViewModel`은 반환된 `VideoEntry`만 library와 UI collection에 반영하고 저장한다.

### 편집 다이얼로그 `Try Parse On 141jav`

1. 버튼 클릭 시 제목을 `LibraryPathHelper.NormalizeCode`로 정규화한다.
2. owner가 `MainWindow`가 아니거나 query가 비어 있으면 안내 후 종료한다.
3. interaction 버튼들을 비활성화한다.
4. `MainWindow.ViewModel.TryFetchOneFourOneJavMetadataAsync(query)`를 호출한다.
5. helper는 session을 보장한 뒤 141jav source만 직접 호출한다. NanoJav fallback은 없다.
6. 결과가 있으면 날짜, 배우, 태그, 설명, thumbnail bytes를 dialog ViewModel에 반영한다.
7. helper 실패, 결과 없음, 적용할 값 없음, 예외는 사용자 안내로 드러낸다.
8. `SetInteractionInProgress(false)`는 `finally`에서 반드시 실행한다.
9. library entry 변경은 사용자가 Save를 누를 때 기존 저장 흐름에서만 발생한다.

### 스캔 후 자동 metadata queue

1. 기존처럼 `_webMetadataService.EnrichAsync`를 호출한다.
2. `EnsureCrawlerReadyAsync`나 `StartCrawlerAsync`를 호출하지 않는다.
3. session이 이미 살아 있으면 141jav-first 시도는 허용한다.
4. session이 없으면 141jav source는 `null`을 반환하고 NanoJav fallback이 동작한다.

## 구현 작업

### Task 0: 기준 상태 확인

**파일:** 없음

- [ ] `git status --short`로 기존 사용자 변경을 확인한다.
- [ ] `dotnet test tests/Airi.Tests/Airi.Tests.csproj -c Debug`를 실행해 baseline을 기록한다.
- [ ] `dotnet build Airi.sln -c Debug`를 실행해 baseline을 기록한다.

검증:

- 테스트나 빌드가 이미 실패하면 실패 내용을 기록한다. 이 계획의 변경과 무관한 기존 실패를 조용히 고치지 않는다.

### Task 1: WebMetadataService fallback 규칙을 테스트로 고정

**파일:**

- 수정: `tests/Airi.Tests/WebMetadataServiceTests.cs`

작업:

- [ ] 첫 source가 부분 `WebVideoMetaResult`를 반환하면 fallback source를 호출하지 않는 테스트를 추가한다.
- [ ] 첫 source가 `null`을 반환하면 다음 source를 호출하는 테스트를 추가한다.
- [ ] test double은 호출 횟수를 노출하는 작은 `IWebVideoMetaSource` 구현으로 둔다.

검증:

```powershell
dotnet test tests/Airi.Tests/Airi.Tests.csproj -c Debug --filter WebMetadataServiceTests
```

기대:

- production 변경 없이 통과해야 한다.

### Task 2: 141jav session 계약, provider, source adapter 추가

**파일:**

- 생성: `Web/IOneFourOneJavCrawlerSessionProvider.cs`
- 생성: `Web/CrawlerSessionProvider.cs`
- 생성: `Web/OneFourOneJavMetaSource.cs`
- 수정: `Web/OneFourOneJavCrawler.cs`
- 생성: `tests/Airi.Tests/OneFourOneJavMetaSourceTests.cs`

작업:

- [ ] `IOneFourOneJavCrawlerSessionProvider`와 `IOneFourOneJavCrawlerSession`을 추가한다.
- [ ] `CrawlerSessionProvider`는 `CurrentSession`과 `SetSession(IOneFourOneJavCrawlerSession? session)`만 가진다.
- [ ] `OneFourOneJavCrawler.CrawlerSession`이 `IOneFourOneJavCrawlerSession`을 구현하게 한다.
- [ ] `CrawlerSession`에 `NavigateToAsync(string url, CancellationToken cancellationToken = default)`를 추가한다.
- [ ] source pipeline에서 description이 이중 번역되지 않도록 `CrawlerSession.TryGetMetadataAsync` 경로는 raw crawler metadata를 반환하게 한다.
- [ ] `OneFourOneJavMetaSource`를 `IWebVideoMetaSource`로 구현한다.
- [ ] `Name`은 `"141Jav"`, `CanHandle`은 공백이 아닌 query에 대해 true를 반환한다.
- [ ] provider session이 없거나 navigation이 실패하면 `FetchAsync`는 `null`을 반환한다.
- [ ] metadata의 date, actors, tags, description과 thumbnail이 모두 비어 있으면 `CrawlerMetadata` 객체가 non-null이어도 `null`을 반환한다.
- [ ] metadata만 있으면 thumbnail 없이 `WebVideoMetaResult`를 반환한다.
- [ ] thumbnail만 있으면 빈 `VideoMeta`와 thumbnail bytes를 담은 `WebVideoMetaResult`를 반환한다.
- [ ] thumbnail extension은 URL path extension을 우선하고 없으면 `.jpg`를 사용한다.
- [ ] thumbnail 다운로드 실패는 metadata 결과를 버리지 않는다.
- [ ] `FetchAsync`는 같은 source instance 안에서 navigation/parse가 겹치지 않게 직렬화한다.
- [ ] source lock 대기 중 cancellation이 요청되면 `OperationCanceledException`을 전파하고 대기 중이던 두 번째 fetch는 navigation을 시작하지 않는다.

테스트:

- [ ] session 없음 -> `null`.
- [ ] navigation 실패 -> `null`, 검색 URL은 `https://www.141jav.com/search/{escaped query}` 형식.
- [ ] metadata + thumbnail -> `VideoMeta`와 thumbnail bytes 반환.
- [ ] metadata only -> metadata 반환, thumbnail bytes 없음.
- [ ] thumbnail only -> 빈 meta와 thumbnail bytes 반환.
- [ ] empty `CrawlerMetadata` + thumbnail 없음 -> `null`. 이 케이스는 NanoJav fallback을 막지 않아야 한다.
- [ ] thumbnail download 실패 -> metadata만 반환.
- [ ] concurrent `FetchAsync` 두 개가 fake session의 navigation/parse를 겹치지 않게 실행한다.
- [ ] 첫 `FetchAsync`가 lock을 잡고 있을 때 두 번째 `FetchAsync`의 token을 cancel하면 `OperationCanceledException`이 발생하고 두 번째 navigation은 실행되지 않는다.

검증:

```powershell
dotnet test tests/Airi.Tests/Airi.Tests.csproj -c Debug --filter OneFourOneJavMetaSourceTests
dotnet build Airi.sln -c Debug
```

### Task 3: ChromeDriver factory와 MainViewModel lifecycle seam 추가

**파일:**

- 생성: `Web/IOneFourOneJavCrawlerSessionFactory.cs`
- 생성: `Web/OneFourOneJavCrawlerSessionFactory.cs`
- 수정: `MainWindow.xaml.cs`
- 수정: `ViewModels/MainViewModel.cs`
- 수정: `tests/Airi.Tests/MainViewModelSkeletonTests.cs`
- 생성: `tests/Airi.Tests/MainViewModelCrawlerTests.cs`

작업:

- [ ] `IOneFourOneJavCrawlerSessionHandle`, `OneFourOneJavCrawlerStartResult`, `IOneFourOneJavCrawlerSessionFactory`를 추가한다.
- [ ] Selenium factory는 기존 `MainViewModel.StartCrawlerAsync`의 ChromeDriver 생성 코드를 옮겨서 구현한다.
- [ ] factory 실패 시 생성 중인 driver/service를 factory 내부에서 cleanup하고 예외를 던진다.
- [ ] `MainWindow.xaml.cs`에서 `CrawlerSessionProvider`, `OneFourOneJavCrawler`, `OneFourOneJavMetaSource`, `OneFourOneJavCrawlerSessionFactory`를 한 번씩 생성한다.
- [ ] `WebMetadataService` source 순서는 `OneFourOneJavMetaSource`, `NanoJavMetaSource`로 둔다.
- [ ] 같은 `CrawlerSessionProvider`와 `OneFourOneJavMetaSource` instance를 `WebMetadataService`와 `MainViewModel`에 전달한다.
- [ ] `MainViewModel` 생성자 의존성을 `WebMetadataService`, `CrawlerSessionProvider`, `OneFourOneJavMetaSource`, `IOneFourOneJavCrawlerSessionFactory`로 바꾼다.
- [ ] `MainViewModel`에서 Selenium concrete field와 using을 제거한다.
- [ ] `_crawlerHandle`, `_crawlerSession`, `_crawlerMonitorTask`, `_crawlerStartGate`를 둔다.
- [ ] `EnsureCrawlerReadyAsync(CancellationToken cancellationToken = default)`는 session이 없을 때 `StartCrawlerAsync(cancellationToken)`를 호출한다.
- [ ] `StartCrawlerAsync(CancellationToken cancellationToken = default)`는 start gate를 소유하고, gate 안에서 활성/시작 완료 session을 재확인한다.
- [ ] start gate 대기, factory 시작, source fetch에 같은 cancellation token을 전달한다.
- [ ] `StartCrawlerCommand`는 `StartCrawlerAsync(CancellationToken.None)`을 호출한다.
- [ ] 같은 시점에 bulk fetch와 editor helper가 session을 요구해도 `factory.StartAsync`가 한 번만 호출되게 한다.
- [ ] factory 성공 후 handle/session/provider는 같은 dispatcher update에서 설정한다.
- [ ] monitor는 raw driver가 아니라 `_crawlerHandle.IsBrowserOpen()`만 본다.
- [ ] `DisposeCrawler`는 provider session을 `null`로 만든 뒤 handle을 dispose한다.
- [ ] `StartCrawlerCommand`는 유지하되 UI 버튼이 사라져도 실행 가능한 lifecycle command로 남긴다.
- [ ] `TryFetchOneFourOneJavMetadataAsync(string query, CancellationToken cancellationToken = default)` helper를 추가한다.
- [ ] helper는 query가 비어 있으면 `null`, session 보장 실패 시 `null`, 성공 시 141jav source만 직접 호출한다.
- [ ] helper는 `EnsureCrawlerReadyAsync(cancellationToken)`와 `_oneFourOneJavSource.FetchAsync(query, cancellationToken)`에 같은 token을 전달한다.
- [ ] bulk와 editor가 아직 전환되기 전까지 `NavigateCrawlerToAsync`, `TryGetCrawlerMetadataAsync`, `TryGetCrawlerThumbnailUrlAsync`는 새 `_crawlerSession`을 감싸는 호환 wrapper로 유지한다.
- [ ] 이 Task 끝의 `dotnet build`가 통과해야 한다. 생성자 변경만 하고 `MainWindow.xaml.cs`를 나중으로 미루지 않는다.

테스트:

- [ ] helper 호출 시 session이 없으면 factory가 호출되고 provider session이 설정된다.
- [ ] factory 실패 시 provider는 비어 있고 `IsCrawlerRunning`은 false가 되며 status에 실패가 드러난다.
- [ ] concurrent helper 호출 두 개가 같은 시작 작업을 공유하고 factory 호출은 한 번이다.
- [ ] `DisposeCrawler`는 provider를 비우고 handle을 dispose한다.
- [ ] handle이 닫힌 상태를 monitor가 감지하면 provider를 비우고 `IsCrawlerRunning`을 false로 돌린다.
- [ ] `StartCrawlerCommand.CanExecute`는 running 상태에 따라 갱신된다.
- [ ] helper cancellation이 요청되면 factory/source 경로에서 `OperationCanceledException`이 전파되고 provider에 session이 남지 않는다.
- [ ] skeleton fixture는 실제 Selenium 타입 없이 fake factory/handle/session으로 생성된다.

검증:

```powershell
dotnet test tests/Airi.Tests/Airi.Tests.csproj -c Debug --filter "MainViewModelCrawlerTests|MainViewModelSkeletonTests"
dotnet build Airi.sln -c Debug
```

### Task 4: bulk Fetch Metadata를 WebMetadataService pipeline으로 이동

**파일:**

- 수정: `ViewModels/MainViewModel.cs`
- 수정: `tests/Airi.Tests/MainViewModelCrawlerTests.cs`

작업:

- [ ] `FetchMissingMetadataWithCrawlerAsync`는 직접 141jav URL 이동, parse, thumbnail download, field merge를 하지 않는다.
- [ ] 각 대상 entry에 대해 `_webMetadataService.EnrichAsync(entry, query, CancellationToken.None)` 결과만 반영한다.
- [ ] bulk command에 새 cancellation source를 추가하지 않는다. 취소 UI는 이번 범위가 아니다.
- [ ] 기존 `IsMetadataIncomplete` 기준은 thumbnail missing/fallback only로 유지한다.
- [ ] bulk-only `ApplyCrawlerMetadataAsync`, `DownloadCrawlerThumbnailAsync`, `DetermineThumbnailKey`는 더 이상 참조가 없을 때 제거한다.
- [ ] editor에서 아직 쓰는 public crawler helper는 Task 5 전까지 제거하지 않는다.

테스트:

- [ ] bulk fetch 실행 시 session이 없으면 factory가 한 번 호출된다.
- [ ] bulk fetch는 `_webMetadataService.EnrichAsync` 결과를 library/UI collection에 반영한다.
- [ ] 날짜, 배우, 태그, 설명만 비어 있고 thumbnail이 정상인 항목은 bulk 대상이 아니다.
- [ ] 스캔 후 자동 metadata queue는 factory를 호출하지 않는다.
- [ ] session이 없는 자동 queue에서 141jav source가 `null`이면 NanoJav fallback이 호출될 수 있다.

검증:

```powershell
dotnet test tests/Airi.Tests/Airi.Tests.csproj -c Debug --filter MainViewModelCrawlerTests
dotnet build Airi.sln -c Debug
```

### Task 5: MetadataEditorWindow의 141jav 흐름을 helper 기반으로 변경

**파일:**

- 수정: `Views/MetadataEditorWindow.xaml.cs`
- 수정: `ViewModels/MainViewModel.cs`

작업:

- [ ] `OnTryParseOn141JavClick`은 URL을 직접 만들지 않고 정규화된 query로 `TryFetchOneFourOneJavMetadataAsync`를 호출한다.
- [ ] `SetInteractionInProgress(true)` 이후 helper 호출과 결과 적용은 `try/catch/finally` 안에서 수행한다.
- [ ] `finally`에서 `SetInteractionInProgress(false)`를 호출해 Save, Cancel, Try Parse 버튼을 항상 복구한다.
- [ ] 결과가 `null`이면 "찾지 못함" 안내를 표시한다.
- [ ] helper 호출 또는 thumbnail 적용 중 예상하지 못한 예외가 나면 오류 안내를 표시하고 버튼 상태를 복구한다.
- [ ] 결과가 있으면 `WebVideoMetaResult.Meta`의 date, tags, actors, description을 dialog ViewModel에 반영한다.
- [ ] `ThumbnailBytes`가 있으면 기존 `MetadataEditorViewModel.UpdateThumbnailFromBytesAsync`를 사용한다.
- [ ] 적용된 metadata와 thumbnail이 모두 없으면 안내를 표시한다.
- [ ] 이 경로에서는 `WebMetadataService.EnrichAsync`를 호출하지 않는다.
- [ ] 기존 public crawler helper `NavigateCrawlerToAsync`, `TryGetCrawlerMetadataAsync`, `TryGetCrawlerThumbnailUrlAsync`는 editor 전환 후 제거한다.
- [ ] editor의 thumbnail download 전용 `HttpClient`가 더 이상 쓰이지 않으면 제거한다.

검증:

```powershell
dotnet build Airi.sln -c Debug
```

### Task 6: MainWindow 버튼 배치 변경

**파일:**

- 수정: `MainWindow.xaml`

작업:

- [ ] top command bar의 `Fetch Metadata` 버튼을 제거한다.
- [ ] top command bar의 column 수와 `Missing Metadata Only` `Grid.Column`을 정리한다.
- [ ] status bar 오른쪽의 `Start Crawler` 버튼을 제거한다.
- [ ] 같은 위치에 `Fetch Metadata` 버튼을 두고 `FetchMetadataCommand`에 바인딩한다.
- [ ] `StartCrawlerCommand`는 ViewModel public surface에 남긴다.

검증:

```powershell
dotnet build Airi.sln -c Debug
```

수동 확인:

- top command bar에 `Fetch Metadata` 버튼이 없다.
- status bar 오른쪽 버튼 텍스트가 `Fetch Metadata`다.
- status bar에 `Start Crawler` 버튼이 보이지 않는다.

### Task 7: crawler translation dead code 정리

**파일:**

- 수정: `Web/OneFourOneJavCrawler.cs`
- 수정: `MainWindow.xaml.cs`
- 수정: `tests/Airi.Tests/*`

작업:

- [ ] source pipeline에서 description 번역은 `WebMetadataService`만 담당한다.
- [ ] `OneFourOneJavCrawler`의 translation service field, target language field, translation constructor, private `TranslateDescriptionAsync`가 더 이상 쓰이지 않으면 제거한다.
- [ ] `OneFourOneJavCrawler` 생성은 `new OneFourOneJavCrawler()`로 통일한다.
- [ ] 관련 using과 테스트 생성자를 정리한다.

검증:

```powershell
dotnet build Airi.sln -c Debug
```

### Task 8: 전체 검증과 graphify 갱신

**파일:**

- 수정 가능: `graphify-out/*`

작업:

- [ ] 전체 unit test를 실행한다.
- [ ] Debug build를 실행한다.
- [ ] `graphify update .`를 실행한다.
- [ ] 앱 수동 smoke test를 수행한다.

검증:

```powershell
dotnet test tests/Airi.Tests/Airi.Tests.csproj -c Debug
dotnet build Airi.sln -c Debug
graphify update .
```

수동 smoke test:

- 앱 첫 실행 후 `Fetch Metadata` 한 번으로 Chrome 창이 열린다.
- 메타데이터 편집 다이얼로그의 `Try Parse On 141jav`도 Chrome 창을 자동 시작한다.
- `Try Parse On 141jav`가 실패하거나 결과가 없어도 Save, Cancel, Try Parse 버튼이 다시 활성화된다.
- Chrome 창을 닫으면 crawler running 상태가 false로 돌아간다.
- bulk 결과는 thumbnail cache 저장과 description translation을 `WebMetadataService` 경로로 거친다.
- `Try Parse On 141jav`는 저장 전 library entry를 바꾸지 않는다.

## 테스트 매트릭스

| 요구사항 | 테스트 |
| --- | --- |
| 141jav 부분 결과가 fallback을 막음 | `WebMetadataServiceTests` first non-null source 테스트 |
| 141jav source가 session 없을 때 빠짐 | `OneFourOneJavMetaSourceTests` session missing 테스트 |
| 141jav source 빈 metadata가 fallback을 막지 않음 | `OneFourOneJavMetaSourceTests` empty metadata 테스트 |
| 141jav source navigation/parse 직렬화 | `OneFourOneJavMetaSourceTests` concurrent fetch 테스트 |
| 141jav source lock 대기 중 cancellation 전파 | `OneFourOneJavMetaSourceTests` lock wait cancellation 테스트 |
| bulk/editor 동시 자동 시작이 ChromeDriver를 하나만 생성 | `MainViewModelCrawlerTests` concurrent auto-start 테스트 |
| helper cancellation이 start/source로 전파됨 | `MainViewModelCrawlerTests` cancellation propagation 테스트 |
| bulk fetch가 EnrichAsync 결과만 반영 | `MainViewModelCrawlerTests` bulk service result 테스트 |
| 자동 metadata queue가 crawler를 시작하지 않음 | `MainViewModelCrawlerTests` auto queue factory count 테스트 |
| thumbnail-only missing 기준 유지 | `MainViewModelCrawlerTests` `IsMetadataIncomplete` 테스트 |
| editor helper가 NanoJav fallback을 하지 않음 | `MainViewModelCrawlerTests` 141jav-only helper 테스트 |
| Selenium 없는 lifecycle test | fake factory/handle/session 기반 `MainViewModelCrawlerTests` |

## 스펙 확인 필요 항목

없음.

## 후속 검토 항목

- 141jav partial result 때문에 NanoJav의 더 완성도 높은 결과를 자주 놓치면 source filter 또는 field-level merge를 별도 스펙으로 다룬다.
- 편집 다이얼로그에서 141jav 설명을 즉시 번역해야 한다는 요구가 확인되면 dialog 전용 번역 책임을 별도 설계한다.
- 날짜, 배우, 태그, 설명 누락까지 bulk 대상으로 볼지는 별도 UX/성능 검토 후 정한다.

## 남은 리스크

- crawler session이 살아 있는 동안 자동 metadata queue도 141jav를 먼저 시도한다. 기준 스펙은 MVP에서 이를 허용한다.
- 실제 141jav 페이지 구조가 바뀌면 adapter는 `null`을 반환할 수 있지만 결과 품질은 떨어진다. selector 개선은 별도 작업이다.
- 실제 ChromeDriver 동작은 unit test에서 직접 검증하지 않는다. lifecycle은 fake collaborator로 검증하고 실제 browser 동작은 수동 smoke test로 확인한다.
