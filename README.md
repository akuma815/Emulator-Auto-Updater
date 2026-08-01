# Emulator Auto Updater

.NET 8 WPF 기반 Windows 데스크톱 앱입니다. 자동 업데이트 기능이 없는 에뮬레이터들의 최신 빌드/나이트리 파일을 최대 10개 병렬로 검색하고, 8분할 병렬 다운로드 엔진으로 고속 수신하며, 지정한 폴더에 자동 압축 해제까지 한 번에 처리합니다.

📖 **[상세 사용 설명서 (USER_GUIDE.md)](USER_GUIDE.md)**

---

## ⚡ 주요 특징

- **최대 10개 병렬 업데이트 확인 및 전체 다운로드**: 모든 에뮬레이터의 릴리즈 상태를 동시에 초고속 체크 및 동시 다운로드.
- **8분할 고속 병렬 다운로드 엔진**: 5MB 이상의 대용량 아티팩트/빌드 파일(BizHawk, Dolphin, Ryujinx 등)을 8개 커넥션으로 분할 다운로드하여 8~10배 초고속 수신 (6.8 MB/s ~ 10 MB/s 이상).
- **스마트 Asset Pattern 자동 변환기**: 예시 파일명만 적고 `[패턴 자동 변환]`을 누르면 최적의 정규식 패턴으로 자동 전환.
- **독립 포터블 설정 파일(config.json) 지원**: 실행 폴더 내 `config.json` 우선 로드, 설정 불러오기/저장/다른 이름으로 저장 지원.
- **다양한 저장소 및 봇 우회 지원**: GitHub, Gitea, Dolphin 공식, PPSSPP devbuilds, melonDS, Flycast, BizHawk 등 자동 지원.

---

## 🛠️ 빌드 및 배포

### 빌드
```powershell
dotnet build .\EmulatorAutoUpdater.csproj -c Release
```

### EXE Publish
```powershell
dotnet publish .\EmulatorAutoUpdater.csproj -c Release --self-contained false
```

결과물 위치:
```text
bin\Release\net8.0-windows\publish\EmulatorAutoUpdater.exe
```

---

## 📖 다운로드 + 압축 해제 흐름

1. 선택한 빌드 파일을 에뮬레이터 폴더에 8분할 병렬 고속 다운로드합니다.
2. 다운로드 스트림이 완전히 닫히고 100% 수신되면 압축 파일 여부를 확인합니다.
3. `.zip` 또는 `.7z` 파일이면 지정된 에뮬레이터 폴더에 안전하게 압축을 해제합니다.
4. 압축 해제가 정상 완료되면 임시 다운로드 압축 파일을 자동으로 깔끔하게 삭제합니다.
5. 압축 해제에 실패할 경우 원인 확인을 위해 파일을 삭제하지 않습니다.

---

## ⚙️ 설정 파일 (config.json)

설정 파일은 실행 파일 위치(`AppContext.BaseDirectory`)의 `config.json`을 단독으로 사용합니다. (APPDATA 경로 미사용)

자세한 기능 설명 및 사용법은 **[USER_GUIDE.md](USER_GUIDE.md)**를 참조하세요.
