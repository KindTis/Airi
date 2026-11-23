# Product

<!-- impeccable:product-schema 1 -->

## Platform

windows

Windows 10/11용 .NET 9 WPF 데스크톱 앱이다.

<!-- Impeccable 4.3.1의 표준 플랫폼 값(web/ios/android/adaptive)에 Windows WPF가 없어 실제 플랫폼을 기록했다. 자동 로더의 이 값 지원 여부는 확인하지 못했다. -->

## Users

Windows PC에 영상을 소장하고 직접 관리하는 개인 사용자다. 많은 영상 중 원하는 항목을 찾고, 커버와 미리보기로 내용을 확인한 뒤 재생하며, 필요한 메타데이터를 정리한다.

## Product Purpose

Airi는 로컬 비디오 라이브러리를 한곳에서 탐색하고 관리하는 앱이다. 사용자가 소장한 영상과 메타데이터를 보호하면서, 대량의 영상에서도 원하는 항목을 빠르게 찾아 재생할 수 있도록 한다.

## Operating Context

- 실행 시 저장된 라이브러리를 먼저 불러오고, 지정 폴더를 스캔해 신규·누락·변경 파일을 반영한다.
- 제목·품번·배우·태그 검색과 배우·제작사·장르 복합 필터, 정보 미완성·파일 없음 보기와 정렬로 영상을 찾는다.
- 커버 갤러리와 읽기 전용 툴팁 영상 미리보기로 내용을 확인한다.
- 더블클릭 또는 무작위 재생으로 Windows에 연결된 외부 미디어 플레이어를 실행한다.
- 항목을 선택하고 `F1`을 눌러 별도 메타데이터 편집 창을 연다. 다중 선택 모드에서는 여러 항목의 지정 필드를 일괄 편집한다.
- 메타데이터 갱신은 품번으로 같은 작품을 확인한 웹 정보를 가져와 미리보기·적용 흐름으로 반영한다. 시작 시 새로 발견한 식별 가능한 파일은 자동 갱신한다.
- 삭제 시 원본 파일이 있으면 Windows 휴지통으로 이동하고 카탈로그 항목을 제거한다. 파일이 없으면 카탈로그 항목만 제거한다. 기존 앱 휴지통 기록의 복원·재검사는 별도 흐름이다.

## Capabilities and Constraints

- 기존 구현은 .NET 9, WPF, MVVM을 사용한다. 향후 UI 작업도 이 네이티브 앱을 기준으로 판단한다.
- 기본 스캔 대상 확장자는 `mp4`, `mkv`, `avi`, `wmv`다. 파일 확장자의 인식과 영상 미리보기의 디코딩 성공은 구분한다.
- 제목, 출시일, 설명, 배우, 제작사, 장르, 태그와 썸네일을 편집할 수 있다. 썸네일 파일 선택과 영상에서의 썸네일 생성 기능이 있다.
- 웹에서 수집한 원문과 한국어 표시 값을 구분해 보존한다. 배우 이름은 원래 이름·읽기·한국어 이름을 저장하며, 검색과 필터는 표시 값과 원래 식별 값을 연결한다.
- 수동으로 잠근 필드는 웹 갱신에서 보존한다. 사용자가 잠그지 않은 필드는 기존 값이 있어도 웹 정보로 갱신될 수 있다.
- 라이브러리와 설정은 로컬 파일로 관리하며, 기본 경로는 실행 파일 디렉터리를 기준으로 한다.
- 대량 목록의 탐색을 위해 목록 가상화와 썸네일 지연 로딩을 사용한다.
- 웹 메타데이터는 로컬 주소 설정 파일과 설치된 Chromium 계열 브라우저, 네트워크와 외부 소스가 필요하다. 설정 파일은 Git과 게시 산출물에서 제외한다. 조회는 임시 프로필의 헤드리스·음소거 세션을 사용한다.
- DeepL 번역에는 별도 키가 필요하다. 번역을 사용할 수 없거나 실패하면 원문을 보존한다.
- ‘대량 영상의 빠른 탐색’은 사용자에게 확인한 우선순위다. 대상 항목 수와 허용 응답시간의 정량 기준은 미정이다.
- 파일·메타데이터 보호는 사용자에게 확인한 우선순위다. 향후 변경은 원본 파일과 사용자가 관리한 정보에 미치는 영향을 검토해야 한다.

## Brand Commitments

제품명은 Airi다. 기존 제품 아이콘은 루트의 `icon.png`와 `icon.ico`다.

## Evidence on Hand

- `README.md`: 제품 소개, 기본 사용 흐름, 요구 사항과 데이터 위치.
- `Airi.csproj`: Windows WPF 플랫폼과 현재 기술 스택.
- `MainWindow.xaml`, `MainWindow.xaml.cs`, `ViewModels/MainViewModel.cs`: 탐색·재생·편집 진입 흐름의 현재 구현.
- `Views/GalleryMetadataEditorWindow.xaml`, `ViewModels/GalleryEditorDraft.cs`: 개별·일괄 메타데이터와 썸네일 편집, 변경 미리보기.
- `Domain/LibraryModels.cs`, `Domain/SettingsModels.cs`: 라이브러리와 설정의 데이터 구조.
- `Infrastructure/TooltipPreviewController.cs`, `Services/VideoPreview/`: 툴팁 영상 미리보기와 미디어 처리 구현.
- `Web/MetadataProductCode.cs`, `Web/MetadataSourceConfiguration.cs`, `Services/WebMetadataService.cs`, `Services/MetadataLocalization.cs`: 웹 정보 식별·조회·한국어 표시.
- `ViewModels/MainViewModel.Deletion.cs`: 삭제·휴지통 이동·복원과 작업 저널 처리.
- `tests/Airi.Tests/`: 기존 동작과 회귀 확인에 사용할 테스트. 빌드·테스트 결과는 실제 실행한 범위에 한정해 별도로 보고한다.

## Product Principles

1. 대량 영상에서도 원하는 항목을 찾고 고르는 흐름의 속도와 반응성을 우선한다.
2. 원본 영상과 사용자가 관리한 메타데이터의 보존을 우선한다.
3. 탐색·내용 확인·재생·정보 정리라는 개인 라이브러리 관리의 핵심 흐름을 중심으로 기능을 판단한다.

## Accessibility & Inclusion

현재 자동 영상 미리보기는 Windows의 애니메이션 효과 설정을 따른다. 효과가 꺼져 있으면 커버를 유지한다. 향후 미리보기와 모션 관련 UI 작업에서 이 동작을 보존한다.
