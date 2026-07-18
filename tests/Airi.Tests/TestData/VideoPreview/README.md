# 영상 프리뷰 고정 테스트 자료

## H.264

- 파일: `h264.mp4`
- 컨테이너·코덱: MP4/H.264
- 길이·해상도·프레임률: 12초, 640×360, 15fps
- 생성 도구: BtbN FFmpeg `n8.1.2-22-g94138f6973-20260711` Windows x64 LGPL
- SHA-256: `e8e3294fe7c87526c4518bd59ee558979028ea50d70edaed55a1a16c66162671`

```powershell
resources/ffmpeg/win-x64/ffmpeg.exe -y -f lavfi -i "testsrc2=size=640x360:rate=15:duration=12" -an -c:v libopenh264 -pix_fmt yuv420p -movflags +faststart tests/Airi.Tests/TestData/VideoPreview/h264.mp4
```

배포용 LGPL build에 포함된 OpenH264 encoder로 fixture를 생성했다. 이 파일은 디코더와 raw BGRA pipe 검증에만 사용한다.

## HEVC

- 파일: `hevc.mkv`
- 컨테이너·코덱: MKV/HEVC
- 길이·해상도·프레임률: 12초, 640×360, 15fps
- 생성 도구: BtbN FFmpeg `n8.1.2-22-g94138f6973-20260711` Windows x64 LGPL
- SHA-256: `e821a1ccc86a0019dec0d45fc9dff68a5e2677d24e537c74affa7a80b460403e`

```powershell
resources/ffmpeg/win-x64/ffmpeg.exe -y -hide_banner -loglevel error -f lavfi -i "testsrc2=size=640x360:rate=15:duration=12" -an -c:v libkvazaar -pix_fmt yuv420p tests/Airi.Tests/TestData/VideoPreview/hevc.mkv
```

배포용 LGPL build에 포함된 Kvazaar encoder로 생성했다. 저장소에는 생성된 bitstream만 포함한다.

## 민감 metadata 실패 주입 자료

- 파일: `sensitive-failure.mp4`
- 컨테이너·코덱: MP4/H.264
- 길이·해상도·프레임률: 12초, 640×360, 15fps
- metadata sentinel: title `AIRI_SENTINEL_TITLE`, comment `AIRI_SENTINEL_TAG`
- 생성 도구: BtbN FFmpeg `n8.1.2-22-g94138f6973-20260711` Windows x64 LGPL
- SHA-256: `81418829159b69fc5abecc946a8d7c1b413967cf3aba8318a96d157298b4a779`

```powershell
resources/ffmpeg/win-x64/ffmpeg.exe -y -hide_banner -loglevel error -i tests/Airi.Tests/TestData/VideoPreview/h264.mp4 -map 0:v:0 -c copy -metadata title=AIRI_SENTINEL_TITLE -metadata comment=AIRI_SENTINEL_TAG tests/Airi.Tests/TestData/VideoPreview/sensitive-failure.mp4
```

## 손상 자료

- 파일: `corrupt.mp4`
- 내용: UTF-8 문자열 `not a media file` 뒤 LF 1개
- 생성 도구: PowerShell/.NET UTF-8(BOM 없음) 쓰기
- SHA-256: `33de11461209028bc4f440a03b107b7fcb096209b44005450082e749cee8504b`

```powershell
[IO.File]::WriteAllText('tests/Airi.Tests/TestData/VideoPreview/corrupt.mp4', "not a media file`n", [Text.UTF8Encoding]::new($false))
```
