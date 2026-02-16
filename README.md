# Airi

Airi는 로컬 비디오 라이브러리를 관리하는 .NET 9 WPF 데스크톱 앱입니다.  
앱 시작 시 `videos.json`을 로드하고 대상 폴더를 스캔해 파일 상태를 동기화하며, 웹 메타데이터 보강과 수동 편집 기능을 제공합니다.

## 주요 기능

- 라이브러리 영속화: `videos.json` 로드/저장 및 기본 타겟(`./Videos`) 자동 생성
- 파일 시스템 스캔: 신규/누락/변경 파일 감지(`mp4`, `mkv`, `avi`, `wmv` 기본 포함)
- 탐색/필터: 검색어, 배우 필터, 정렬 옵션, 메타데이터 누락 항목만 보기
- 재생: 더블클릭 또는 `Random Play`로 기본 플레이어 실행
- 웹 메타데이터 보강:
- 웹 메타데이터 소스를 통한 메타데이터/썸네일 수집
- Selenium 기반 크롤러 연동(`Start Crawler` 후 `Fetch Metadata`)
- 메타데이터 편집 창:
- `F1` 키로 편집 창 열기
- 제목/출시일/설명/배우/태그/썸네일 수동 수정
- 편집 창의 파싱 버튼으로 크롤러 페이지 결과 반영
- 선택적 번역: DeepL API 키가 있으면 설명 자동 번역

## 기술 스택

- .NET 9 (`net9.0-windows10.0.26100.0`)
- WPF + MVVM
- xUnit
- Selenium WebDriver + ChromeDriver
- HtmlAgilityPack
- DeepL.net (선택 기능)

## 프로젝트 구조

- `App.xaml`, `MainWindow.xaml`: 앱 진입점/UI
- `ViewModels/`: 화면 상태 및 명령(MVVM)
- `Views/`: 추가 창(XAML, code-behind)
- `Services/`: 스캔/저장/번역/메타데이터 처리
- `Domain/`: 도메인 모델(`LibraryData`, `VideoEntry`, `VideoMeta`)
- `Infrastructure/`: 경로/캐시/로깅/커맨드 유틸리티
- `Web/`: 외부 메타데이터 소스 및 크롤러
- `Themes/`, `resources/`, `Videos/`: 테마 및 정적 리소스
- `tests/Airi.Tests/`: xUnit 테스트 프로젝트

## 요구 사항

- Windows 10/11 (WPF 대상 프레임워크 기반)
- .NET 9 SDK
- 인터넷 연결(웹 메타데이터/크롤러 사용 시)

## 실행 방법

```powershell
dotnet restore
dotnet build Airi.sln -c Debug
dotnet run --project Airi.csproj
```

## 테스트

```powershell
dotnet test tests/Airi.Tests/Airi.Tests.csproj -c Debug
```

커버리지:

```powershell
dotnet test tests/Airi.Tests/Airi.Tests.csproj --collect:"XPlat Code Coverage"
```

현재 테스트는 주로 아래 영역을 검증합니다.

- 파일 스캐너/라이브러리 스캐너
- 라이브러리 저장소(`LibraryStore`)
- 경로 정규화(`LibraryPathHelper`)
- 썸네일 캐시
- 메타데이터 서비스(`WebMetadataService`)
- 번역 Null 서비스
- ViewModel 일부 동작

## 환경 변수(선택)

- `DEEPL_AUTH_KEY`: 설정 시 DeepL 번역 활성화
- `DEEPL_TARGET_LANG`: 번역 대상 언어 코드(기본값 `KO`)

둘 중 `DEEPL_AUTH_KEY`가 없으면 번역은 자동 비활성화됩니다.

## 데이터/로그/캐시 경로

기본 경로는 실행 파일 기준(AppDomain BaseDirectory)입니다.

- 라이브러리 파일: `videos.json`
- 로그: `log/airi_*.log`
- 썸네일 캐시: `cache/`
- 기본 이미지: `resources/noimage.jpg`

## 기본 라이브러리 동작

- `videos.json`이 없으면 기본 라이브러리를 생성합니다.
- 기본 타겟 폴더는 `./Videos`입니다.
- 디버그 빌드에서는 샘플 항목이 초기 데이터로 추가될 수 있습니다.
- 릴리스 빌드에서는 실제 파일이 없는 항목을 정규화 과정에서 제거합니다.

## 크롤러 사용 흐름

1. 앱 하단 `Start Crawler` 버튼 클릭
2. 크롬 브라우저 세션이 열리면 크롤러 준비 완료
3. 상단 `Fetch Metadata`로 메타데이터 일괄 보강
4. 필요 시 비디오 선택 후 `F1` -> 편집 창의 파싱 버튼 실행
5. 브라우저 창을 닫으면 크롤러 세션이 종료됨

## 확장 포인트

- 새 메타데이터 소스 추가:
- `Web/IWebVideoMetaSource` 구현
- `MainWindow.xaml.cs`의 `metadataSources`에 등록
- 번역기 교체/추가:
- `Services/ITextTranslationService` 구현
- `WebMetadataService` 또는 크롤러 서비스에 주입
