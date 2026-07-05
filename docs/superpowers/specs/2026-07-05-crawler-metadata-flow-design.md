# Fetch Metadata 및 141jav 크롤러 통합 스펙

## 목표

Fetch Metadata와 메타데이터 편집 다이얼로그의 `Try Parse On 141jav`가 크롤러 실행 여부를 사용자가 직접 관리하지 않게 한다. 크롤러가 없으면 필요한 시점에 자동으로 시작하고, 141jav 크롤링 결과 적용은 `WebMetadataService.EnrichAsync`의 일반 메타데이터 source 경로로 통합한다.

## 배경

현재 구조는 다음 두 흐름이 나뉘어 있다.

- 일반 메타데이터 보강: `WebMetadataService.EnrichAsync`가 `IWebVideoMetaSource` 목록을 순회하고 `WebVideoMetaResult`를 `VideoEntry`에 병합한다.
- 141jav 크롤링: `MainViewModel.FetchMissingMetadataWithCrawlerAsync`가 직접 141jav 검색 URL로 이동하고, `OneFourOneJavCrawler.CrawlerSession`에서 메타데이터와 썸네일 URL을 읽은 뒤 `ApplyCrawlerMetadataAsync`로 별도 병합한다.

이 분리는 같은 메타데이터 병합 규칙, 썸네일 저장, 설명 번역 흐름을 두 곳에서 관리하게 만든다. 이번 변경은 141jav를 `IWebVideoMetaSource` adapter로 넣어 일반 source 파이프라인과 같은 경로를 타게 하는 것이 핵심이다.

## 성공 기준

1. 메인 화면의 Fetch Metadata 버튼을 누르면 크롤러 세션이 없을 때 ChromeDriver를 자동으로 시작한 뒤 누락 메타데이터 보강을 진행한다.
2. 메타데이터 편집 다이얼로그의 `Try Parse On 141jav` 버튼도 크롤러 세션이 없을 때 자동으로 ChromeDriver를 시작한 뒤 141jav 검색과 파싱을 수행한다.
3. 메인 화면 status bar의 `Start Crawler` 버튼은 제거되고, 그 위치에 `Fetch Metadata` 버튼이 배치된다.
4. `StartCrawlerCommand`는 public command로 유지하되, UI 시작 버튼 역할이 아니라 ChromeDriver 세션 생성/모니터링/정리 수명 관리만 담당한다.
5. 141jav 결과를 `VideoEntry`에 적용하는 bulk 흐름은 `WebMetadataService.EnrichAsync`를 사용한다.
6. 141jav adapter는 ChromeDriver 생성/종료를 하지 않는다. 현재 크롤러 세션을 받아 검색/파싱하고 `WebVideoMetaResult`만 반환한다.
7. 141jav source가 부분 결과를 반환하면 bulk fetch에서는 그 결과를 성공으로 확정하고 NanoJav fallback을 호출하지 않는다.
8. `Fetch Metadata` bulk 대상 선정은 기존 `IsMetadataIncomplete` 기준을 유지한다. MVP에서는 썸네일이 비어 있거나 fallback thumbnail인 항목만 대상으로 한다.

## 비범위

- 141jav HTML 파싱 셀렉터의 정확도 개선은 하지 않는다.
- Selenium/ChromeDriver 패키지 버전 변경은 하지 않는다.
- NanoJav source 동작 방식은 변경하지 않는다.
- 메타데이터 편집 다이얼로그의 전체 MVVM 전환은 하지 않는다.
- 사용자가 수동으로 크롤러만 열어두는 별도 UI는 유지하지 않는다.

## 설계 결정

### 선택안

141jav를 `IWebVideoMetaSource` 구현체인 `OneFourOneJavMetaSource`로 추가한다. `OneFourOneJavMetaSource`는 작은 provider interface를 통해 현재 crawler session을 얻고, 검색 URL 이동, 파싱, 썸네일 다운로드를 수행해 `WebVideoMetaResult`를 반환한다.

이 접근은 기존 `WebMetadataService`의 병합, 썸네일 캐시 저장, 설명 번역 정책을 그대로 활용한다. `MainViewModel`은 크롤러 실행 보장과 결과 저장/컬렉션 갱신만 담당한다.

### source 선택 및 fallback 규칙

MVP에서는 `WebMetadataService`의 "첫 성공 source에서 종료" 규칙을 유지한다. 141jav를 NanoJav보다 앞에 두는 것은 의도된 동작이다.

| 호출 경로 | 실행 source | 성공 판정 | fallback 규칙 |
| --- | --- | --- | --- |
| 메인 화면 `Fetch Metadata` bulk | `OneFourOneJavMetaSource`, 그 다음 `NanoJavMetaSource` | 141jav가 날짜, 배우, 태그, 설명, 썸네일 중 하나라도 반환하면 성공 | 141jav가 `null`을 반환할 때만 NanoJav를 호출한다. 부분 결과여도 NanoJav는 호출하지 않는다. |
| 편집 다이얼로그 `Try Parse On 141jav` | `OneFourOneJavMetaSource`만 직접 호출 | 141jav가 날짜, 배우, 태그, 설명, 썸네일 중 하나라도 반환하면 성공 | NanoJav fallback을 하지 않는다. 버튼 이름이 141jav 전용 시도를 뜻하기 때문이다. |
| 스캔 후 자동 metadata queue | `OneFourOneJavMetaSource`, 그 다음 `NanoJavMetaSource` | 141jav가 날짜, 배우, 태그, 설명, 썸네일 중 하나라도 반환하면 성공 | 자동 queue는 크롤러를 새로 시작하지 않는다. session이 없으면 141jav source가 `null`을 반환하고 NanoJav로 넘어간다. session이 이미 살아 있으면 141jav-first 시도를 허용한다. |

141jav 부분 결과가 NanoJav보다 품질이 낮을 수 있는 문제는 MVP에서 감수한다. source별 필드 병합, source filter, 품질 점수 기반 merge는 이번 범위에 넣지 않는다.

### 대안과 트레이드오프

1. `MainViewModel`에 141jav 전용 적용 로직을 유지한다.
   - 장점: 변경 파일이 적다.
   - 단점: 일반 metadata source 경로와 계속 분리되어 병합/썸네일/번역 정책이 중복된다.
   - 판단: 이번 요구사항의 핵심인 `EnrichAsync` 통합과 맞지 않아 선택하지 않는다.

2. `WebMetadataService`가 직접 ChromeDriver를 시작하게 한다.
   - 장점: 호출자는 `EnrichAsync`만 호출하면 된다.
   - 단점: `WebMetadataService`가 브라우저 수명과 UI 상태까지 알게 되어 너무 넓은 module이 된다.
   - 판단: 크롤러 세션 수명은 밖에 둔다는 요구와 충돌하므로 선택하지 않는다.

3. 별도 `OneFourOneJavMetaSource` adapter를 source 목록에 추가한다.
   - 장점: 141jav가 기존 source pipeline에 합류하고, 크롤러 수명과 파싱 책임이 분리된다.
   - 단점: provider seam과 source 등록 순서를 추가로 관리해야 한다.
   - 판단: 요구사항과 기존 `IWebVideoMetaSource` 구조에 가장 잘 맞으므로 선택한다.

## module 경계

### `WebMetadataService`

역할은 유지한다.

- `IWebVideoMetaSource` 목록을 순서대로 호출한다.
- 첫 성공 결과를 기존 `VideoEntry.Meta`와 병합한다.
- `ThumbnailBytes`가 있으면 `ThumbnailCache`에 저장한다.
- 설명 번역을 한 곳에서 처리한다.

141jav 전용 병합 로직은 이 module 밖에 두지 않는다.

### `OneFourOneJavMetaSource`

새 파일: `Web/OneFourOneJavMetaSource.cs`

역할:

- `IWebVideoMetaSource`를 구현한다.
- query를 141jav 검색 URL로 변환한다.
- provider에서 현재 crawler session을 얻는다.
- 현재 session으로 URL 이동, 메타데이터 파싱, 썸네일 URL 추출을 수행한다.
- 썸네일 URL이 있으면 bytes와 extension을 내려준다.
- `VideoMeta`와 thumbnail 정보를 담은 `WebVideoMetaResult`를 반환한다.
- 같은 source instance 안에서 141jav fetch를 한 번에 하나만 실행한다.

결과 생성 규칙:

- provider에 현재 session이 없으면 `null`을 반환한다.
- navigation이 실패하면 `null`을 반환한다.
- metadata와 thumbnail이 모두 비어 있으면 `null`을 반환한다.
- `CrawlerMetadata.ReleaseDate`는 `DateOnly.FromDateTime(releaseDate.Date)`로 변환한다.
- `CrawlerMetadata.Actors`, `Tags`, `Description`은 비어 있지 않을 때만 `VideoMeta`에 채운다.
- 141jav에서 title을 파싱하지 않으므로 `VideoMeta.Title`은 빈 문자열로 둔다. 기존 title 보존은 `WebMetadataService.MergeMeta`가 담당한다.
- thumbnail URL 다운로드에 실패하면 metadata만 담은 `WebVideoMetaResult`를 반환한다.
- thumbnail extension은 URL path의 확장자를 우선하고, 없으면 `.jpg`를 사용한다.

동시 실행 규칙:

- `OneFourOneJavMetaSource`는 private `SemaphoreSlim`으로 `FetchAsync`를 직렬화한다.
- lock 범위는 최소한 `NavigateToAsync`, `TryGetMetadataAsync`, `TryGetThumbnailUrlAsync`를 포함한다.
- MVP에서는 구현 단순성을 위해 thumbnail bytes 다운로드까지 같은 `FetchAsync` lock 안에서 처리해도 된다.
- lock 대기 중 cancellation이 요청되면 `OperationCanceledException`을 전파한다.
- 이 규칙은 메인 화면 bulk fetch와 편집 다이얼로그 `Try Parse On 141jav`가 같은 ChromeDriver session을 동시에 navigation하지 못하게 하기 위한 것이다.

하지 않는 일:

- ChromeDriver 생성/종료
- `VideoEntry` 저장
- UI status 변경
- `MainViewModel` 컬렉션 갱신
- 설명 번역

`Name`은 `"141Jav"`로 둔다. `CanHandle`은 공백이 아닌 query면 `true`를 반환한다.

설명 번역은 bulk 경로에서 `WebMetadataService`만 수행한다. adapter와 `OneFourOneJavCrawler.CrawlerSession`은 source 결과를 만들 때 설명을 번역하지 않는다. 기존 `OneFourOneJavCrawler`의 번역 기능은 이번 source pipeline에서는 사용하지 않거나 제거한다.

### crawler session provider

새 provider interface를 추가한다. 위치는 `Web/IOneFourOneJavCrawlerSessionProvider.cs`이다.

```csharp
namespace Airi.Web
{
    public interface IOneFourOneJavCrawlerSessionProvider
    {
        IOneFourOneJavCrawlerSession? CurrentSession { get; }
    }

    public interface IOneFourOneJavCrawlerSession
    {
        Task<bool> NavigateToAsync(string url, CancellationToken cancellationToken = default);
        Task<OneFourOneJavCrawler.CrawlerMetadata?> TryGetMetadataAsync(CancellationToken cancellationToken = default);
        Task<string?> TryGetThumbnailUrlAsync(CancellationToken cancellationToken = default);
    }
}
```

provider interface는 ChromeDriver 수명을 소유하지 않는다. "현재 사용할 수 있는 141jav crawler session이 있는가"만 알려준다. `CrawlerSessionProvider`가 이를 구현하고, `MainViewModel`은 provider에 session을 설정하며, `OneFourOneJavMetaSource`는 provider를 소비한다.

`IOneFourOneJavCrawlerSession`은 Selenium `IWebDriver`를 source와 테스트에 노출하지 않기 위한 작은 session interface다. 실제 `OneFourOneJavCrawler.CrawlerSession`이 이 interface를 구현한다.

provider는 UI thread affinity를 숨기지 않는다. `CurrentSession`의 set/clear는 `MainViewModel`의 crawler lifecycle에서만 수행하고, source는 read-only로 소비한다.

### `OneFourOneJavCrawler.CrawlerSession`

현재 `TryGetMetadataAsync`와 `TryGetThumbnailUrlAsync`를 제공한다. adapter가 검색까지 맡으려면 session에 URL 이동 method를 추가한다.

```csharp
public Task<bool> NavigateToAsync(string url, CancellationToken cancellationToken = default)
```

이 method는 내부 `IWebDriver`로 이동한다. `WebDriverException`이 발생하면 `false`를 반환한다. 실패가 source 결과 없음으로 자연스럽게 이어지고, bulk fetch가 다음 항목으로 계속 진행할 수 있게 하기 위해서다.

### `MainViewModel`

역할:

- `CrawlerSessionProvider` instance를 소유한다.
- `OneFourOneJavMetaSource` 또는 동일한 141jav 전용 source interface instance를 소유한다.
- `IOneFourOneJavCrawlerSessionFactory` instance를 소유한다.
- Fetch Metadata 실행 전에 크롤러 세션을 보장한다.
- `FetchMissingMetadataWithCrawlerAsync`에서 직접 141jav URL 이동/파싱/적용을 하지 않고 `_webMetadataService.EnrichAsync(entry, query, cancellationToken)`를 호출한다.
- `StartCrawlerAsync`는 ChromeDriver를 직접 생성하지 않는다. factory 호출, crawler handle/session 설정, provider session 설정, monitor 시작, dispose만 담당한다.

새 helper 계약:

```csharp
public Task<WebVideoMetaResult?> TryFetchOneFourOneJavMetadataAsync(
    string query,
    CancellationToken cancellationToken = default)
```

규칙:

- query가 비어 있으면 `null`을 반환한다.
- `_crawlerSession`이 없으면 `StartCrawlerAsync`를 호출해 session을 만든다.
- 시작 후에도 provider의 `CurrentSession`이 없으면 `null`을 반환한다.
- 141jav 전용 source의 `FetchAsync`를 직접 호출한다.
- NanoJav fallback은 하지 않는다.
- `OperationCanceledException`은 전파한다.

제거 또는 축소 대상:

- `ApplyCrawlerMetadataAsync`는 bulk 경로에서 제거한다.
- `DownloadCrawlerThumbnailAsync`는 bulk 경로에서 제거한다. 썸네일 저장은 `WebMetadataService`와 `ThumbnailCache`가 담당한다.
- `NavigateCrawlerToAsync`, `TryGetCrawlerMetadataAsync`, `TryGetCrawlerThumbnailUrlAsync`는 public surface에서 제거한다. 메타데이터 편집 다이얼로그는 `TryFetchOneFourOneJavMetadataAsync` helper 하나만 호출한다.

### crawler start factory

새 파일: `Web/IOneFourOneJavCrawlerSessionFactory.cs`

ChromeDriver 생성은 UI/환경 의존성이 크므로 `MainViewModel`에서 직접 수행하지 않는다. MVP에서 작은 factory collaborator를 도입해 자동 시작과 실패 cleanup을 unit test에서 대체할 수 있게 한다.

```csharp
namespace Airi.Web
{
    public interface IOneFourOneJavCrawlerSessionHandle : IDisposable
    {
        IOneFourOneJavCrawlerSession Session { get; }
        bool IsBrowserOpen();
    }

    public sealed record OneFourOneJavCrawlerStartResult(
        IOneFourOneJavCrawlerSessionHandle Handle,
        string Summary);

    public interface IOneFourOneJavCrawlerSessionFactory
    {
        Task<OneFourOneJavCrawlerStartResult> StartAsync(CancellationToken cancellationToken = default);
    }
}
```

factory 책임:

- `ChromeDriverService`, `ChromeOptions`, `ChromeDriver`를 생성한다.
- page load timeout과 seed URL navigation을 수행한다.
- `OneFourOneJavCrawler.CreateSession(driver)`로 session을 만든다.
- 성공하면 Selenium resource를 감싼 `IOneFourOneJavCrawlerSessionHandle`과 summary를 `OneFourOneJavCrawlerStartResult`로 반환한다.
- 실패하면 생성 중인 Selenium resource를 직접 정리하고 예외를 던진다.

factory가 하지 않는 일:

- `IsCrawlerRunning` 변경
- `StatusMessage` 변경
- provider session 설정
- monitor task 시작
- 성공 후 handle dispose

성공 후 crawler handle/session 수명은 `MainViewModel`이 소유한다. Selenium 구현체는 handle 내부에서만 `ChromeDriverService`와 `IWebDriver`를 보관한다. `MainViewModel.StartCrawlerAsync`는 factory 결과를 받은 뒤 `_crawlerHandle`, `_crawlerSession`과 provider session을 같은 dispatcher update 안에서 설정한다. monitor는 raw `IWebDriver`가 아니라 `_crawlerHandle.IsBrowserOpen()`으로 browser 생존 여부를 확인한다. `DisposeCrawler`는 provider session을 `null`로 만든 뒤 `_crawlerHandle.Dispose()`를 호출한다.

## UI 변경

### 메인 화면

파일: `MainWindow.xaml`

변경:

- top command bar의 `Fetch Metadata` 버튼을 제거한다.
- status bar 오른쪽의 `Start Crawler` 버튼을 제거한다.
- 제거된 `Start Crawler` 위치에 `Fetch Metadata` 버튼을 배치하고 `FetchMetadataCommand`에 바인딩한다.

결과:

- 사용자는 별도 `Start Crawler` 버튼을 누르지 않는다.
- status bar에는 현재 상태 메시지와 Fetch Metadata 실행 버튼이 남는다.
- top command bar는 검색, 정렬, Random Play, Missing Metadata Only 중심으로 단순해진다.

### 메타데이터 편집 다이얼로그

파일: `Views/MetadataEditorWindow.xaml.cs`

변경:

- `OnTryParseOn141JavClick`은 검색 query를 만든 뒤 ViewModel helper를 호출한다.
- helper는 크롤러 세션이 없으면 자동으로 시작한다.
- 크롤러 시작 실패, 검색 실패, 파싱 결과 없음은 현재처럼 사용자에게 안내한다.
- 파싱 성공 시 `WebVideoMetaResult.Meta`의 날짜, 배우, 태그, 설명을 다이얼로그 ViewModel에 반영한다.
- `ThumbnailBytes`가 있으면 기존 `UpdateThumbnailFromBytesAsync`를 사용한다.
- metadata와 thumbnail이 모두 없으면 결과 없음으로 안내한다.
- 이 다이얼로그 경로는 `WebMetadataService.EnrichAsync`를 호출하지 않는다. 사용자가 저장하기 전까지 library entry를 변경하면 안 되기 때문이다.

## 동작 흐름

### Fetch Metadata

1. 사용자가 status bar의 `Fetch Metadata`를 클릭한다.
2. `FetchMetadataCommand`가 `FetchMissingMetadataWithCrawlerAsync`를 실행한다.
3. 이미 `IsFetchingMetadata`이면 status만 갱신하고 종료한다.
4. 누락 메타데이터 대상이 없으면 status를 갱신하고 종료한다. MVP에서 누락 메타데이터 대상은 썸네일이 비어 있거나 fallback thumbnail인 항목이다.
5. `_crawlerSession`이 없으면 `StartCrawlerAsync`를 호출하고, `StartCrawlerAsync`는 factory를 통해 ChromeDriver와 session을 만든다.
6. 크롤러 시작에 실패하면 status에 실패 메시지를 남기고 종료한다.
7. 각 대상에 대해 query를 만든다.
8. `_webMetadataService.EnrichAsync(entry, query, cancellationToken)`를 호출한다.
9. source 목록에서 `OneFourOneJavMetaSource`가 현재 session으로 141jav 검색/파싱을 수행한다.
10. 성공 결과는 `WebMetadataService`에서 병합, 썸네일 저장, 설명 번역을 거쳐 `VideoEntry`로 반환된다.
11. `MainViewModel`은 반환된 entry를 library와 UI collection에 반영하고 저장한다.

### Try Parse On 141jav

1. 사용자가 편집 다이얼로그에서 `Try Parse On 141jav`를 클릭한다.
2. 버튼, Save, Cancel을 비활성화한다.
3. 제목을 `LibraryPathHelper.NormalizeCode`로 정규화한다.
4. owner `MainWindow.ViewModel`의 `TryFetchOneFourOneJavMetadataAsync` helper를 호출한다.
5. ViewModel helper는 크롤러 세션이 없으면 `StartCrawlerAsync`를 호출하고, `StartCrawlerAsync`는 factory를 통해 세션을 만든다.
6. `OneFourOneJavMetaSource`를 통해 현재 session에서 검색/파싱한다.
7. 결과가 있으면 다이얼로그 ViewModel에 날짜, 태그, 배우, 설명, 썸네일을 반영한다.
8. 완료 또는 실패 후 버튼 상태를 복구한다.

## source 등록

파일: `MainWindow.xaml.cs`

현재 source 등록은 `NanoJavMetaSource`만 포함한다.

변경 후 source 목록에는 `OneFourOneJavMetaSource`를 포함한다. Fetch Metadata 버튼이 141jav 중심 흐름을 대체하므로 source 순서는 141jav를 NanoJav보다 앞에 둔다.

```csharp
var crawlerSessionProvider = new CrawlerSessionProvider();
var oneFourOneJavCrawler = new OneFourOneJavCrawler();
var oneFourOneJavSource = new OneFourOneJavMetaSource(crawlerSessionProvider, _httpClient);
var crawlerSessionFactory = new OneFourOneJavCrawlerSessionFactory(oneFourOneJavCrawler);
var metadataSources = new IWebVideoMetaSource[]
{
    oneFourOneJavSource,
    new NanoJavMetaSource(_httpClient)
};
```

`MainWindow`는 같은 `crawlerSessionProvider` instance를 `OneFourOneJavMetaSource`와 `MainViewModel`에 전달한다. 또한 편집 다이얼로그 helper가 141jav source만 호출할 수 있도록 `oneFourOneJavSource`도 `MainViewModel`에 전달한다. `crawlerSessionFactory`는 ChromeDriver 생성만 담당하고, 생성된 session의 provider 등록은 `MainViewModel`이 담당한다. 이 구조는 생성 순환을 만들지 않고 source가 `MainViewModel` concrete type을 알지 않게 한다.

스캔 후 자동 metadata queue도 같은 `WebMetadataService` instance를 사용한다. 따라서 자동 queue는 같은 source 순서를 타지만, `MainViewModel` helper를 거치지 않으므로 크롤러 자동 시작은 하지 않는다.

## provider 세부 설계

새 파일: `Web/CrawlerSessionProvider.cs`

역할:

- `IOneFourOneJavCrawlerSessionProvider`를 구현한다.
- `CurrentSession`을 get/set 또는 `UpdateSession` method로 관리한다.
- ChromeDriver 자체는 보관하지 않는다.

예상 interface:

```csharp
public sealed class CrawlerSessionProvider : IOneFourOneJavCrawlerSessionProvider
{
    public IOneFourOneJavCrawlerSession? CurrentSession { get; private set; }

    public void SetSession(IOneFourOneJavCrawlerSession? session)
    {
        CurrentSession = session;
    }
}
```

`MainViewModel`은 `_crawlerSession` field를 직접 source에 노출하지 않고 provider에 session을 설정한다. `DisposeCrawler`는 provider session도 `null`로 초기화한다.

## 상태 및 오류 처리

- 크롤러 자동 시작 중에는 `IsCrawlerRunning = true`, `StatusMessage = "Crawler starting..."`를 유지한다.
- factory가 ChromeDriver 시작에 실패하면 생성 중인 Selenium resource는 factory가 정리한다. `MainViewModel`은 `StatusMessage = $"Crawler failed: {ex.Message}"`를 설정하고 Fetch Metadata 또는 Try Parse를 중단한다.
- 이미 크롤러가 실행 중이면 `StartCrawlerAsync`는 새 crawler handle을 만들지 않고 기존 session을 사용한다.
- 사용자가 Chrome 창을 닫으면 monitor가 session과 provider를 정리하고 `IsCrawlerRunning = false`로 돌린다.
- 141jav navigation 실패는 해당 query의 결과 없음으로 처리한다. bulk fetch에서는 다음 항목으로 진행한다.
- `Try Parse On 141jav`에서는 navigation 실패나 결과 없음이 사용자 안내 메시지로 드러나야 한다.
- `OperationCanceledException`은 삼키지 않고 호출자에게 전파한다.
- 141jav source가 부분 결과를 반환한 경우 status는 성공으로 표시한다. 빠진 필드를 이유로 같은 query에서 NanoJav fallback을 실행하지 않는다.
- `WebMetadataService.EnrichAsync`가 141jav에서 예외를 catch한 뒤 다음 source로 넘어가는 기존 정책은 유지한다. 단, source 내부에서 처리 가능한 navigation 실패와 parse 실패는 예외가 아니라 `null`로 반환한다.
- bulk fetch와 편집 다이얼로그가 동시에 141jav fetch를 요청하면 두 번째 요청은 source lock에서 대기한다. 같은 session에서 navigation이 겹치는 동작은 허용하지 않는다.
- 스캔 후 자동 metadata queue는 `StartCrawlerAsync`를 호출하지 않는다. 같은 source 목록을 쓰지만, session이 없으면 141jav source가 결과 없음으로 빠지고 NanoJav fallback이 실행된다.

## 테스트 계획

### unit test

1. `WebMetadataServiceTests`
   - 141jav source가 `WebVideoMetaResult`를 반환하면 `EnrichAsync`가 기존 병합/썸네일 저장/번역 경로를 그대로 사용하는지 검증한다.
   - 첫 source가 `null`을 반환하면 다음 source로 fallback하는 기존 동작을 유지하는지 검증한다.
   - 기존 stub source 테스트를 유지한다.

2. `OneFourOneJavMetaSourceTests`
   - provider session이 없으면 `FetchAsync`가 `null`을 반환한다.
   - session navigation이 실패하면 `FetchAsync`가 `null`을 반환한다.
   - session metadata와 thumbnail URL이 있으면 `WebVideoMetaResult.Meta`와 `ThumbnailBytes`를 반환한다.
   - metadata만 있으면 thumbnail 없이 `WebVideoMetaResult`를 반환한다.
   - thumbnail만 있으면 빈 `VideoMeta`와 thumbnail bytes를 담은 `WebVideoMetaResult`를 반환한다.
   - thumbnail 다운로드 실패 시 meta만 반환하고 thumbnail bytes는 비운다.
   - concurrent `FetchAsync` 호출이 같은 session에서 navigation/parse를 겹치게 실행하지 않는지 검증한다.

3. `MainViewModel` 관련 테스트
   - Fetch Metadata 실행 시 session이 없으면 `IOneFourOneJavCrawlerSessionFactory.StartAsync`가 호출되는지 검증한다.
   - factory 성공 결과를 받은 뒤 provider session이 설정되고 monitor 시작 경로로 넘어가는지 검증한다.
   - factory 성공 테스트는 실제 Selenium 타입 없이 fake `IOneFourOneJavCrawlerSessionHandle`로 수행한다.
   - factory 실패 시 provider session이 설정되지 않고 status가 실패로 끝나는지 검증한다.
   - `DisposeCrawler`가 provider session을 `null`로 정리하고 handle을 dispose하는지 검증한다.
   - monitor가 `IsBrowserOpen() == false`를 감지하면 provider session을 정리하고 `IsCrawlerRunning = false`로 되돌리는지 검증한다.
   - `StartCrawlerCommand`의 `CanExecute`는 기존처럼 running 상태에 따라 갱신되는지 유지 확인한다.
   - bulk 대상 선정은 썸네일 누락 기준을 유지하고, 날짜/배우/태그/설명만 비어 있는 항목은 대상에 포함하지 않는지 검증한다.
   - `TryFetchOneFourOneJavMetadataAsync`가 141jav source만 호출하고 NanoJav fallback을 하지 않는지 검증한다.
   - `FetchMissingMetadataWithCrawlerAsync`가 `_webMetadataService.EnrichAsync` 결과만 library/UI collection에 반영하는지 검증한다.
   - 스캔 후 자동 metadata queue는 factory를 호출하지 않고, provider session이 없을 때 NanoJav fallback 경로를 사용할 수 있는지 검증한다.

### build/test command

변경 후 다음을 실행한다.

```powershell
dotnet test tests/Airi.Tests/Airi.Tests.csproj -c Debug
dotnet build Airi.sln -c Debug
```

## 구현 순서

1. `IOneFourOneJavCrawlerSessionProvider`와 provider 구현을 추가한다.
2. `OneFourOneJavCrawler.CrawlerSession`에 navigation method를 추가하고 기존 parse methods는 유지한다.
3. `IOneFourOneJavCrawlerSessionFactory`와 Selenium 기반 구현을 추가한다.
4. `OneFourOneJavMetaSource : IWebVideoMetaSource`를 추가한다.
5. `MainWindow.xaml.cs`에서 141jav source를 한 번 생성해 source 목록과 `MainViewModel`에 같은 instance로 전달하고, provider와 factory를 공유한다.
6. `MainViewModel` 생성자에 provider, 141jav source, factory dependency를 추가한다.
7. `MainViewModel`에서 `FetchMissingMetadataWithCrawlerAsync`를 `EnsureCrawlerReadyAsync` + `_webMetadataService.EnrichAsync` 흐름으로 단순화한다.
8. 141jav 전용 bulk 적용 method를 제거한다.
9. `StartCrawlerAsync`와 `DisposeCrawler`가 provider session을 설정/정리하게 한다.
10. `MainWindow.xaml`에서 Fetch Metadata 버튼을 status bar로 이동하고 Start Crawler 버튼을 제거한다.
11. `MetadataEditorWindow.xaml.cs`의 `Try Parse On 141jav` 흐름을 자동 start + 141jav source/helper 경로로 변경한다.
12. 관련 unit test를 추가하고 `dotnet test`, `dotnet build`를 실행한다.

## 수용 기준

- 앱 첫 실행 후 크롤러를 별도로 시작하지 않아도 Fetch Metadata 버튼 한 번으로 Chrome 창이 열리고 누락 메타데이터 fetch가 시작된다.
- 메타데이터 편집 다이얼로그에서도 크롤러를 별도로 시작하지 않아도 `Try Parse On 141jav`가 Chrome 창을 열고 검색/파싱을 시도한다.
- status bar의 오른쪽 버튼 텍스트는 `Fetch Metadata`이고, `Start Crawler` 버튼은 보이지 않는다.
- 141jav bulk fetch 결과는 `WebMetadataService.EnrichAsync`를 통해 적용된다.
- 141jav partial result가 있으면 해당 query는 성공으로 처리되고 NanoJav fallback은 실행되지 않는다.
- `Try Parse On 141jav`는 NanoJav fallback 없이 141jav 결과만 다이얼로그에 반영한다.
- bulk fetch 대상 선정은 기존처럼 썸네일 누락 기준이다. 날짜, 배우, 태그, 설명만 비어 있는 항목은 이번 bulk 대상에 포함하지 않는다.
- bulk fetch와 `Try Parse On 141jav`가 동시에 실행되어도 같은 ChromeDriver session에서 navigation/parse가 겹치지 않는다.
- `MainViewModel`에는 141jav metadata field별 병합 로직이 남지 않는다.
- `MainViewModel` lifecycle unit test는 실제 Selenium 타입 생성 없이 fake crawler handle로 검증할 수 있다.
- Chrome 창을 닫으면 기존처럼 crawler running 상태가 해제된다.
- 테스트와 Debug build가 통과한다.

## 후속 검토 항목

- 141jav partial result 때문에 NanoJav의 더 완성도 높은 결과를 놓치는 문제가 실제로 자주 발생하면 source filter나 field-level merge를 별도 스펙으로 다룬다.
- 편집 다이얼로그에서 141jav 설명을 즉시 번역해야 하는 요구가 확인되면, dialog 전용 번역 책임을 별도 helper로 둘지 `WebMetadataService`의 일부를 재사용할지 다시 설계한다.
- 날짜, 배우, 태그, 설명 누락까지 `Fetch Metadata` bulk 대상으로 볼지 여부는 별도 UX/성능 검토 후 정한다.

## 남는 리스크

- `OneFourOneJavMetaSource`를 source 목록 앞에 두므로 크롤러 session이 살아 있는 동안 자동 metadata queue도 141jav를 먼저 시도한다. 이번 스펙은 이를 MVP 동작으로 허용한다. 원치 않는 결과가 확인되면 `WebMetadataService.EnrichAsync`에 source filter를 넣는 대신, 호출 경로별 source 목록 분리를 별도 작업으로 다룬다.
- 실제 141jav 페이지 구조가 바뀌면 adapter는 정상적으로 `null`을 반환하지만, 결과 품질은 떨어진다. 파싱 셀렉터 개선은 별도 작업으로 다룬다.
- ChromeDriver 시작은 UI/환경 의존성이 커서 unit test에서는 직접 실행하지 않는다. start lifecycle은 작은 collaborator seam으로 검증하고, 실제 ChromeDriver 동작은 수동 smoke test로 확인한다.
