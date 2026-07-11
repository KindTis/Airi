# 비디오 썸네일 지연 로딩 성능 검증

## 기준 상태

- 기준 commit: `c4f32287a4377d0b97cf3370b8892560d68e8f1c`
- 기준 테스트: Debug 61개 통과, 실패 0개
- 기준 빌드: Debug/Release 모두 경고 0개, 오류 0개
- 최초 sandbox 실행은 NuGet 네트워크 접근 제한으로 `NU1301`이 발생했으며, 네트워크 승인을 받은 동일 명령은 정상 통과했다.

## marker와 비교 규칙

- 공통 원점은 loader/fallback composition 직전의 `StartupMeasurementBegin`이다.
- baseline과 after 비교에는 `VisualFirstMeaningfulCard`, `VisualFirstThumbnail`, `AllItemsPublished`의 동일 marker ID만 사용한다.
- legacy URI binding에서 WPF `ImageSourceConverter`가 `BitmapImage` 대신 `BitmapFrameDecode`를 반환할 수 있으므로, 둘 다 canonical local URI와 decoded pixel 크기를 증거로 사용한다. fallback URI와 경로 불일치는 제외한다.
- after의 `FirstThumbnailApplied`는 원인 분석용이며 baseline 비율 계산에 사용하지 않는다.
- 서로 다른 marker ID나 다른 계측 도구의 수치는 직접 비교하지 않는다.

## 변경 전 baseline

Release runner가 `current=662`를 새 process 5개에서 cold/warm으로 측정했다. 모든 phase가 100% DPI와 1920×1032 DIP 작업영역 조건을 충족했다.

| phase | first meaningful card median / worst | first thumbnail median / worst | realized containers |
| --- | ---: | ---: | ---: |
| cold | 8,363.5148ms / 12,484.1864ms | 852.4484ms / 1,395.0628ms | 662 |
| warm | 4,894.7996ms / 5,031.6384ms | 215.1604ms / 227.8298ms | 662 |

- raw 결과: `docs/performance/raw/2026-07-11-thumbnail-baseline/current/iteration-01.json`부터 `iteration-05.json`
- 집계: `docs/performance/raw/2026-07-11-thumbnail-baseline/summary.json`
- 측정 commit: `c4f32287a4377d0b97cf3370b8892560d68e8f1c`
- 측정 binary SHA-256: `C83282194813E178EBBEDB1AF9035DE496A95D8E41726D7EAA444FA56C79B099`
- 환경: Windows 10.0.26200.0, .NET 9.0.17, X64, Release, 균형 조정 전원 구성
- raw의 `dirtyWorktree=true`는 공통 measurement shell과 아직 추적되지 않은 사용자 스펙/계획서가 적용된 동일 작업 트리에서 측정했음을 뜻한다.

baseline URI binding에서는 WPF가 모든 662개 container를 실체화했고 video skeleton이 마지막 publish 뒤에야 사라져 `VisualFirstMeaningfulCard`가 `AllItemsPublished`보다 늦었다. 첫 thumbnail marker는 canonical non-fallback URI의 decoded pixels를 기준으로 별도 기록됐다.

## 변경 후 결과

Task 11의 네 dataset × 5회 cold/warm 측정 후 hard gate와 관찰 목표를 기록한다.
