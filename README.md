<div align="center">
  <img src="icon.png" width="96" alt="Airi 아이콘">
  <h1>Airi</h1>
  <p>로컬 비디오 라이브러리를 한곳에서 탐색하고 관리하는 Windows 데스크톱 앱</p>
</div>

## 소개

Airi는 로컬에 보관한 비디오를 자동으로 정리하고 빠르게 탐색할 수 있도록 만든 .NET 9 WPF 애플리케이션입니다. 실행 시 저장된 라이브러리를 불러오고 대상 폴더를 스캔해 신규·누락·변경 파일을 동기화합니다.

## 주요 기능

- **라이브러리 자동 동기화** — 저장된 라이브러리를 불러오고 지정 폴더의 파일 상태를 반영합니다.
- **빠른 탐색** — 가상화된 목록과 지연 썸네일 로딩으로 많은 항목도 효율적으로 표시합니다.
- **검색과 필터** — 제목·배우 검색, 배우 필터, 메타데이터 누락 항목 필터를 제공합니다.
- **다양한 정렬** — 제목, 출시일, 생성일 기준의 오름차순·내림차순 정렬을 지원합니다.
- **간편한 재생** — 항목을 더블클릭하거나 무작위 재생 기능으로 기본 미디어 플레이어를 실행합니다.
- **메타데이터 편집** — 제목, 출시일, 설명, 배우, 태그와 썸네일을 직접 관리할 수 있습니다.
- **선택적 정보 보강** — 필요한 경우 메타데이터와 설명 번역을 보강할 수 있습니다.

기본 스캔 대상에는 `mp4`, `mkv`, `avi`, `wmv` 파일이 포함됩니다.

## 요구 사항

- Windows 10 또는 Windows 11
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- 선택 기능 사용 시 인터넷 연결

## 빠른 시작

```powershell
git clone https://github.com/KindTis/Airi.git
cd Airi
dotnet restore
dotnet build Airi.sln -c Debug
dotnet run --project Airi.csproj
```

처음 실행하면 실행 파일 위치를 기준으로 기본 라이브러리와 `Videos` 스캔 대상이 준비됩니다.

## 기본 사용 흐름

1. 앱을 실행하면 저장된 라이브러리를 먼저 표시한 뒤 대상 폴더를 스캔합니다.
2. 검색창, 배우 필터와 정렬 옵션으로 원하는 항목을 찾습니다.
3. 비디오를 더블클릭하거나 **Random Play**를 사용해 재생합니다.
4. 항목을 선택하고 `F1`을 눌러 메타데이터와 썸네일을 편집합니다.
5. 필요한 항목에는 선택적 정보 보강 기능을 사용합니다.

## 선택적 번역 설정

설명 번역이 필요하면 실행 전에 환경 변수를 설정합니다.

```powershell
$env:DEEPL_AUTH_KEY = "your-api-key"
$env:DEEPL_TARGET_LANG = "KO"
dotnet run --project Airi.csproj
```

`DEEPL_AUTH_KEY`가 없으면 번역 기능은 자동으로 비활성화됩니다. `DEEPL_TARGET_LANG`의 기본값은 `KO`입니다.

## 데이터 위치

아래 경로는 모두 실행 파일 디렉터리를 기준으로 합니다.

| 용도 | 기본 경로 |
| --- | --- |
| 라이브러리 데이터 | `videos.json` |
| 기본 스캔 대상 | `Videos/` |
| 로그 | `log/airi_*.log` |
| 썸네일 캐시 | `cache/` |
| 기본 썸네일 | `resources/noimage.jpg` |

## 기술 스택

- .NET 9
- WPF 및 MVVM
- Newtonsoft.Json
- VirtualizingWrapPanel
- xUnit
- DeepL.net(선택적 번역)

## 프로젝트 구조

```text
Airi/
├─ ViewModels/       화면 상태와 명령
├─ Views/            추가 WPF 창
├─ Services/         라이브러리 스캔, 저장과 메타데이터 처리
├─ Domain/           라이브러리 도메인 모델
├─ Infrastructure/   경로, 캐시, 로깅과 공통 유틸리티
├─ Themes/           WPF 스타일과 테마
├─ resources/        기본 이미지 등 정적 리소스
└─ tests/Airi.Tests/ xUnit 테스트 프로젝트
```

## 테스트

전체 테스트를 실행합니다.

```powershell
dotnet test tests/Airi.Tests/Airi.Tests.csproj -c Debug
```

코드 커버리지를 수집하려면 다음 명령을 사용합니다.

```powershell
dotnet test tests/Airi.Tests/Airi.Tests.csproj --collect:"XPlat Code Coverage"
```
