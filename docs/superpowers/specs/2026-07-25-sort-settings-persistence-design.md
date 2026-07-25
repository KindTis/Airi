# 정렬 및 대상 폴더 설정 분리 설계

## 목표

- 사용자가 마지막으로 선택한 정렬을 변경 즉시 저장한다.
- 앱을 비정상 종료한 뒤 다시 실행해도 선택한 정렬과 실제 아이템 순서를 복원한다.
- 스캔 대상 폴더 설정을 `videos.json`에서 분리한다.
- `settings.json`과 `videos.json`을 실행 파일과 같은 폴더에 저장한다.

## 제외 범위

- 대상 폴더를 편집하는 새 UI는 추가하지 않는다.
- 설정 파일 위치를 선택하는 옵션은 추가하지 않는다.
- 사용되지 않는 `LastScanUtc`를 대체하는 새 스캔 이력 모델은 만들지 않는다.

## 현재 문제

`MainViewModel` 생성자는 실행할 때마다 `출시일 내림차순`을 선택하고 즉시 적용한다. 선택한 정렬을 저장하는 모델이나 저장소가 없으므로 다음 실행에서 복원할 수 없다.

현재 `TargetFolder`의 `Root`, `IncludePatterns`, `ExcludePatterns`는 사용자 설정이지만 `Videos`와 함께 `videos.json`에 저장된다. `LastScanUtc`는 시작 스캔 후 기록되지만 어떤 실행 경로에서도 읽히지 않는다.

## 파일 책임

### settings.json

실행 폴더의 `settings.json`은 앱 동작 설정만 저장한다.

```json
{
  "Version": 1,
  "Targets": [
    {
      "Root": "./Videos",
      "IncludePatterns": ["*.mp4", "*.mkv", "*.avi", "*.wmv"],
      "ExcludePatterns": []
    }
  ],
  "Sort": {
    "Field": "ReleaseDate",
    "Direction": "Descending"
  }
}
```

- `Targets`는 스캔 경로와 포함·제외 패턴만 가진다.
- `Sort`는 표시 문자열이나 콤보 박스 인덱스가 아닌 안정적인 필드와 방향을 저장한다.
- `LastScanUtc`는 모델과 저장 데이터에서 제거한다.

### videos.json

실행 폴더의 `videos.json`은 라이브러리 데이터만 저장한다.

- `Version`
- `Videos`

새로 저장되는 `videos.json`에는 `Targets`가 포함되지 않는다.

## 구성 요소

### SettingsStore

별도 인터페이스 없이 하나의 작은 설정 저장소를 추가한다.

- 기본 경로는 `ILibraryStore.FilePath`와 같은 디렉터리의 `settings.json`이다.
- `settings.json`이 없을 때만 같은 디렉터리의 기존 `videos.json`에서 `Targets`를 읽어 이전한다.
- 설정을 정규화하고 누락된 값에는 기존 기본값을 사용한다.
- 임시 파일에 쓴 뒤 같은 볼륨에서 교체하여 손상된 중간 파일이 남지 않게 한다.
- 정렬 변경 저장은 작은 설정 파일의 원자적 교체가 끝난 뒤 반환한다.
- 정렬 변경 저장 실패는 현재 화면 정렬을 되돌리지 않고 상태 메시지와 로그로 알린다.

### MainViewModel

- 생성 과정에서 설정을 읽고 `SortOptions` 중 일치하는 항목을 선택한다.
- 선택한 정렬을 빈 `FilteredVideos`에 먼저 적용한다.
- 이후 게시되는 모든 아이템은 `ICollectionView`의 기존 정렬 설명을 따라 표시된다.
- 사용자가 정렬을 바꾸면 실제 뷰 정렬을 적용한 뒤 작은 설정 파일을 즉시 저장한다.
- 스캔에는 `settings.json`에서 읽은 `Targets`를 사용한다.

### LibraryStore

- 기존 `videos.json`을 읽을 때 남아 있는 `Targets`는 알 수 없는 이전 필드로 무시한다.
- 저장 투영에서는 `Targets`를 제외해 새 형식의 `videos.json`만 쓴다.
- `Videos`의 기존 원자적 저장과 복구 동작은 변경하지 않는다.

## 시작 및 마이그레이션 흐름

1. `settings.json`이 있으면 설정을 읽고 정규화한다.
2. `settings.json`이 없으면 `SettingsStore`가 기존 `videos.json`의 `Targets`만 읽는다.
3. 이전 `Targets`가 유효하면 그대로 사용하고, 없으면 기존 기본 대상 폴더를 사용한다.
4. 기본 정렬은 기존 동작과 동일한 `ReleaseDate/Descending`을 사용한다.
5. 완성된 설정을 `settings.json`에 원자적으로 저장한다.
6. 선택한 정렬을 `FilteredVideos`에 적용한다.
7. `videos.json`을 읽고 아이템을 게시한다.
8. 다음 `videos.json` 저장부터 `Targets`가 제외된다.

`settings.json` 저장이 성공한 뒤에만 `videos.json`에서 `Targets`를 제외하므로 마이그레이션 중 대상 폴더 설정을 잃지 않는다.

## 오류 처리

- 설정 파일이 없으면 이전 `videos.json` 또는 기본값으로 생성한다.
- 알 수 없는 정렬 필드나 방향은 `ReleaseDate/Descending`으로 정규화한다.
- 유효하지 않은 대상 항목은 제외하고, 남은 대상이 없으면 기본 대상 폴더를 사용한다.
- 설정을 읽거나 이전하는 데 실패하면 기존 `videos.json`을 덮어쓰지 않고 시작을 실패시켜 잘못된 대상 폴더로 스캔하는 것을 막는다.
- 정렬 변경 후 설정 저장에 실패하면 현재 세션의 정렬은 유지하고 실패를 사용자와 로그에 알린다.

## 검증

1. 기존 형식의 `videos.json`만 있을 때 `Targets`가 같은 폴더의 `settings.json`으로 이전된다.
2. 이전 성공 후 저장된 `videos.json`에는 `Targets`와 `LastScanUtc`가 없다.
3. 정렬을 변경한 직후 `settings.json`에 안정적인 필드와 방향이 기록된다.
4. 재실행 시 콤보 박스 선택과 `FilteredVideos`의 실제 열거 순서가 모두 저장된 정렬과 일치한다.
5. 잘못된 정렬 값은 기존 기본 정렬로 복구된다.
6. 설정 이전 또는 저장 실패 시 기존 `videos.json`이 보존된다.
7. 전체 테스트와 Debug 빌드가 통과한다.

## 예상 변경 범위

- 설정 모델 및 `SettingsStore`
- `TargetFolder`와 `LibraryData`
- `LibraryStore`의 읽기 호환 및 저장 투영
- `LibraryScanner`가 설정의 `Targets`를 입력받도록 하는 호출 경계
- `MainViewModel`의 설정 로드·정렬 적용·즉시 저장
- 관련 단위 테스트
