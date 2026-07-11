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

schema v2 공통 probe를 적용한 Release runner가 legacy URI-binding `current=662`를 새 process 5개에서 cold/warm으로 다시 측정했다. 모든 phase가 100% DPI와 1920×1032 DIP 작업영역 조건을 충족했다. 기준 동작은 `56d7e1d` 소스이며, 측정 전용 probe만 최종 schema로 교체한 격리 tree라 raw에 `dirtyWorktree=true`와 별도 binary hash를 남겼다.

| phase | first meaningful card median / worst | first thumbnail median / worst | realized containers |
| --- | ---: | ---: | ---: |
| cold | 9,056.3630ms / 10,201.9542ms | 1,649.1146ms / 2,263.8095ms | 662 |
| warm | 4,426.8100ms / 6,362.2625ms | 212.3844ms / 227.5690ms | 662 |

- raw 결과: `docs/performance/raw/2026-07-11-thumbnail-baseline/current/iteration-01.json`부터 `iteration-05.json`
- 집계: `docs/performance/raw/2026-07-11-thumbnail-baseline/summary.json`
- 측정 source commit: `56d7e1d272b959c8d761c4faf7ce0b3e7cc9ad11`
- 측정 binary SHA-256: `00ECF747ED12B62BE408F0EF7506E4E927A2D976728B4257FAA7FC0F9120D6C3`
- 환경: Windows 10.0.26200.0, .NET 9.0.17, X64, Release, 균형 조정 전원 구성
- raw의 `dirtyWorktree=true`는 legacy 동작 소스에 최종 schema v2 probe를 측정 전용으로 적용했음을 뜻한다. fixture manifest SHA-256은 after와 같은 `1EEC1AE76B68FF2ADA29FC6B91535EADC5A98D5FD6F08D27010DC267C01796A1`이다.

baseline URI binding에서는 WPF가 모든 662개 container를 실체화했고 video skeleton이 마지막 publish 뒤에야 사라져 `VisualFirstMeaningfulCard`가 `AllItemsPublished`보다 늦었다. 첫 thumbnail marker는 canonical non-fallback URI의 decoded pixels를 기준으로 별도 기록됐다.

## 변경 후 결과

Release runner가 small=40, medium=200, current=662, stress=1,000을 dataset별 새 process 5개에서 cold/warm으로 측정했다. raw 20개와 phase 40개가 모두 100% DPI·1920×1032 DIP 조건에서 valid였고, 적용 가능한 hard gate는 모두 5/5 통과했다.

| dataset | phase | first meaningful card median / worst | first thumbnail median / worst |
| --- | --- | ---: | ---: |
| small | cold | 1,592.9617ms / 2,016.7946ms | 1,608.6711ms / 2,050.4416ms |
| small | warm | 167.8263ms / 211.3597ms | 178.4055ms / 222.0841ms |
| medium | cold | 1,363.2214ms / 1,966.9704ms | 1,592.9391ms / 1,991.4093ms |
| medium | warm | 208.6842ms / 223.0200ms | 218.9458ms / 234.7866ms |
| current | cold | 1,363.9692ms / 1,852.2494ms | 1,379.7675ms / 1,869.7317ms |
| current | warm | 214.9197ms / 302.4238ms | 227.3906ms / 313.8532ms |
| stress | cold | 1,231.7679ms / 1,313.9369ms | 1,248.8761ms / 1,331.2873ms |
| stress | warm | 225.7105ms / 260.0540ms | 238.1926ms / 278.1594ms |

### current baseline 대비

양수는 개선, 음수는 회귀다.

| phase / metric | median 변화 | worst 변화 | 50% 관찰 목표 |
| --- | ---: | ---: | --- |
| cold first meaningful card | +84.94% | +81.84% | 달성 |
| cold first thumbnail | +16.33% | +17.41% | 미달성 |
| warm first meaningful card | +95.15% | +95.25% | 달성 |
| warm first thumbnail | -7.07% | -37.92% | 미달성 |

virtualization으로 첫 card는 전체 662개 publish 완료를 기다리지 않아 크게 개선됐다. cold thumbnail도 최종 schema 재측정 기준 16.33% 개선됐지만 50%에는 미달했다. warm thumbnail은 median 7.07%, worst 37.92% 회귀했다. 이 목표는 계획서 정의상 hard gate가 아니므로 구조 gate 통과와 구분한다.

### memory·GC checkpoint

working set, managed heap, LRU/item-source/registration/decoded owner는 absolute gauge이고 GC만 phase 시작 대비 delta다. 각 phase는 `PhaseStart`, `VisualFirstMeaningfulCard`, `VisualFirstThumbnail`, `StartupTerminal`, `FirstSteady`, `PhaseEnd` 여섯 고정 checkpoint를 가진다. `checkpointMax`는 이 여섯 sample의 최대값이며 continuous peak가 아니다.

| 결과 | phase | firstSteady working set median / worst | firstSteady managed heap median / worst | checkpointMax working set median / worst | checkpointMax managed heap median / worst | GC0/1/2 delta median |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| baseline current | cold | 2,012,151,808 / 2,014,232,576 B | 45,630,136 / 45,725,856 B | 2,012,168,192 / 2,014,248,960 B | 45,800,680 / 45,894,408 B | 79 / 62 / 56 |
| baseline current | warm | 2,032,205,824 / 2,042,466,304 B | 49,867,992 / 50,325,832 B | 2,038,767,616 / 2,042,470,400 B | 49,867,992 / 50,325,832 B | 51 / 35 / 29 |
| after current | cold | 201,547,776 / 204,455,936 B | 9,388,136 / 9,457,944 B | 201,719,808 / 205,500,416 B | 9,396,360 / 9,466,168 B | 12 / 10 / 8 |
| after current | warm | 229,990,400 / 234,938,368 B | 8,142,960 / 11,174,136 B | 231,448,576 / 236,429,312 B | 10,230,264 / 12,344,464 B | 11 / 7 / 7 |

baseline 대비 after current의 firstSteady working-set median은 cold 89.98%, warm 88.68% 감소했고 managed-heap median은 cold 79.43%, warm 83.67% 감소했다. checkpointMax median도 working set cold 89.98%/warm 88.65%, managed heap cold 79.48%/warm 79.49% 감소했다.

medium→stress 관찰값은 다음과 같다. 양수는 stress 증가, 음수는 감소다.

| phase | card median / worst delta | thumbnail median / worst delta | firstSteady WS median / worst delta | firstSteady heap median / worst delta | checkpointMax WS median / worst delta | checkpointMax heap median / worst delta | GC0/1/2 median delta 차이 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| cold | -131.4535 / -653.0335 ms | -344.0630 / -660.1220 ms | -241,664 / +1,142,784 B | -92,560 / -97,136 B | -245,760 / +1,138,688 B | -92,560 / -97,136 B | +3 / 0 / 0 |
| warm | +17.0263 / +37.0340 ms | +19.2468 / +43.3728 ms | +10,690,560 / +9,625,600 B | -1,649,888 / -1,925,928 B | +12,390,400 / +8,404,992 B | -1,278,824 / +1,191,976 B | +4 / +3 / +2 |

cold fallback file open/decode/freeze 초기화 elapsed는 20개 process median 31.3404ms, worst 44.0656ms였고 각 raw에 worker thread ID와 함께 기록됐다.

### hard gate

- current/stress cold·warm의 firstCardBeforeAllItems: 5/5 Pass.
- small/medium의 동일 gate: 5/5 NotApplicable.
- 모든 dataset/phase의 top·middle·last steady count와 이동 구간 traversal maximum, request realization membership, file-open bound, decode concurrency <=4, LRU entry/recency <=96, dispatcher callback <=100ms, non-fallback source bound, 실제 decoded strong-reference owner gauge <= LRU+realized item-source slot bound: 5/5 Pass.
- 140개 구조 position의 traversal maximum은 최대 36, 계산 hard limit은 최대 40이었고 위반 0개였다. 240개 resource checkpoint의 owner-bound 위반도 0개였다.
- dispatcher batch 1,200개의 최대 점유시간은 28.1933ms로 100ms hard gate뿐 아니라 50ms 관찰 목표도 모두 충족했다.
- medium/stress의 동일 phase·offset container/source guard row: cold/warm 각각 5/5 Pass.
- medium/stress fixture는 동일 seed prefix를 사용하며 runner preflight를 통과했다.
- `CanBeginPhase`는 active decode/ViewModel in-flight/registration/이전 runtime 0과 dispatcher drain을 확인했다. `CanSealPhase`는 startup terminal, active decode/ViewModel in-flight 0, terminal registration과 500ms 안정 상태를 확인했다.
- cold snapshot seal 뒤 구조 traversal로 shared loader를 예열하고, cold window registration/runtime/decoded item owner cleanup 뒤 같은 loader/LRU로 warm phase를 시작했다.

### 출처

- raw: docs/performance/raw/2026-07-11-thumbnail/{dataset}/iteration-01.json부터 iteration-05.json
- 집계: docs/performance/raw/2026-07-11-thumbnail/summary.json
- 측정 시 HEAD: c2450a998ee7674086980570eb0cd7a7b7db97f5
- 측정 binary SHA-256: FADDB62B8EA977CD452AA83F29EE478B6478902D68A49CB4974857904AADA0F0
- 환경: Windows 10.0.26200.0, .NET 9.0.17, X64, Release, 균형 조정 전원 구성
- dirtyWorktree=true는 사용자가 stage한 로컬 문서 정리 상태가 binary 생성 시 존재했음을 뜻한다. 측정 대상 production/test/script 변경은 `c2450a9`에 커밋된 뒤 실행했다.
