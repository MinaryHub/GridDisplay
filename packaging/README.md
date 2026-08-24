# 패키징 · 배포

`build-msix.ps1` 하나로 두 가지 배포물을 만듭니다.

| | 사이드로딩 (기본) | Microsoft Store (`-Store`) |
| --- | --- | --- |
| 산출물 | `.msix` + `.appinstaller` | `.msixupload` (`.msix` + `.appxsym`) |
| .NET 런타임 | 별도 설치 필요 | 패키지에 포함 (self-contained) |
| 서명 | 직접 서명 필수 | 서명하지 않음 — Partner Center가 재서명 |
| 업데이트 | 앱이 `.appinstaller`를 보고 스스로 설치 | Store가 관리 (앱의 자체 업데이트 기능은 자동으로 비활성) |

Windows SDK가 없어도 됩니다. `makeappx.exe` / `signtool.exe`는 SDK가 있으면 거기서, 없으면
`SdkTools.proj`를 restore해 NuGet 캐시에서 가져옵니다.

버전은 `src\GridDisplay\GridDisplay.csproj`의 `<Version>` 하나뿐입니다. 여기만 올리면 패키지 버전,
어셈블리 버전, 앱이 표시하는 버전이 함께 따라갑니다.

---

## Microsoft Store 등록

### 1. Partner Center 준비

1. [Partner Center](https://partner.microsoft.com/dashboard)에서 개발자 계정을 만듭니다
   (개인 약 $19, 회사 약 $99 — 1회 결제).
2. **앱 및 게임 → 새 제품 → MSIX 또는 PWA 앱**으로 앱 이름을 예약합니다.
   예약한 이름이 그대로 `Properties/DisplayName`이 되어야 합니다.

### 2. 제품 ID 확인

예약이 끝나면 **제품 → 제품 관리 → 제품 ID**(Product identity) 페이지에 이 세 값이 나옵니다.
패키지 매니페스트가 이 값과 한 글자라도 다르면 업로드가 거부됩니다.

| Partner Center 항목 | 스크립트 인자 | 이 앱의 값 |
| --- | --- | --- |
| Package/Identity/Name | `-IdentityName` | `minary.GridDisplay` |
| Package/Identity/Publisher | `-Publisher` | `CN=C460CD23-2642-4BA2-A332-6F8ADBE54E1F` |
| 게시자 표시 이름 | `-PublisherDisplayName` | `minary` |
| 예약한 앱 이름 | `-DisplayName` | `Grid Display` |

스크립트 기본값은 자체 서명 인증서로 사이드로딩하는 쪽(`SMIC.GridDisplay` / `CN=SMIC`)에 맞춰져
있습니다. Store 빌드에서는 위 값을 반드시 인자로 넘겨야 합니다.

### 3. 빌드

```powershell
powershell -ExecutionPolicy Bypass -File packaging\build-msix.ps1 -Store `
    -IdentityName         'minary.GridDisplay' `
    -Publisher            'CN=C460CD23-2642-4BA2-A332-6F8ADBE54E1F' `
    -PublisherDisplayName 'minary' `
    -DisplayName          'Grid Display'
```

`-DisplayName`은 Partner Center에서 **예약한 앱 이름과 정확히 같아야** 합니다. 예약 이름이
`Grid Display`가 아니라면 그 이름으로 바꿔 다시 실행하세요.

`publish\msix\GridDisplay_<버전>_x64.msixupload`가 업로드할 파일입니다. 서명하지 않은 것이
정상입니다 — Partner Center가 Store 인증서로 다시 서명합니다.

`-Publisher`가 `CN=<GUID>` 모양이 아니거나 `-IdentityName`이 기본값이면 빌드 시작 시 경고가
나옵니다. 경고를 보고 멈춰 세우는 것이, 업로드까지 갔다가 거부당하는 것보다 싸기 때문입니다.

### 4. 업로드와 심사 준비

**패키지** 단계에서 `.msixupload`를 올린 뒤, 심사에서 걸리기 쉬운 순서로 다음을 채웁니다.

- **`runFullTrust` 사유** — 제출 시 제한된 기능(restricted capability)에 대한 설명을 요구합니다.
  이 앱이 필요한 이유를 그대로 쓰면 됩니다:

  > 이 앱은 사용자가 지정한 데스크톱 프로그램의 창을 격자 칸으로 편입하기 위해 `SetParent`를
  > 사용하고, 칸 클릭·확대·입력 모드 전환을 처리하기 위해 저수준 입력 훅(`SetWindowsHookEx`)을
  > 사용합니다. 두 API 모두 샌드박스된 UWP 프로세스에서는 사용할 수 없어 full trust가 필요합니다.
  > 앱은 사용자가 명시적으로 설정한 프로그램과 주소만 다룹니다.

  패키지된 데스크톱 앱에는 통상 승인되지만, 사유 자체를 비워 둘 수는 없습니다.

- **개인정보처리방침 URL** — 앱이 인터넷에 접속하므로(브라우저 칸, 업데이트 확인) 필수입니다.
  수집하는 정보가 없다면 "수집하지 않는다"고 명시한 페이지라도 있어야 합니다.
- **연령 등급 설문** — 몇 분이면 끝납니다.
- **스크린샷** — 최소 1장(1366×768 이상). 격자에 실제 내용이 들어찬 화면이 좋습니다.
- **설명·기능 목록** — 루트 `README.md`의 기능 목록을 그대로 써도 됩니다.
- **시스템 요구 사항** — Windows 10 1809(x64) 이상. 매니페스트의 `MinVersion`과 같은 값입니다.

### 5. 다음 릴리스

`<Version>`을 올리고 같은 명령을 다시 실행합니다. 이미 게시된 버전보다 높지 않은 패키지는
Store가 거부합니다. 리비전(네 번째 자리)은 Store가 예약하므로 항상 `0`이어야 하고,
스크립트가 이를 검사합니다.

---

## 사이드로딩 (사내 배포)

Store를 거치지 않고 사내 서버·공유 폴더에서 배포할 때 씁니다.

```powershell
# 1) 개발/사내용 자체 서명 인증서 (관리자 권한, 한 번만)
powershell -ExecutionPolicy Bypass -File packaging\New-DevCertificate.ps1

# 2) 패키지 + 업데이트 피드
powershell -ExecutionPolicy Bypass -File packaging\build-msix.ps1 `
    -BaseUri 'https://intranet.example/griddisplay' `
    -Publisher 'CN=SMIC' `
    -CertificatePath .\GridDisplay.pfx -CertificatePassword '...'
```

`-Publisher`는 서명 인증서의 주체(subject)와 정확히 같아야 합니다. 다르면 Windows가 패키지를
거부하고, signtool은 그 이유를 알려주지 않습니다.

`.msix`와 `.appinstaller`를 `-BaseUri`가 가리키는 같은 폴더에 올립니다. 예전 `.msix`도 남겨
두세요 — 한참 뒤처진 기기가 자기 버전에서 올라올 수 있어야 합니다.

설치할 기기에는 먼저 인증서를 **신뢰할 수 있는 루트 인증 기관**(또는 신뢰할 수 있는 사람)에
설치해야 합니다. 그 다음 `GridDisplay.appinstaller`를 실행하면 됩니다.

.NET 8 Desktop Runtime이 없는 기기에 배포한다면 `-SelfContained`를 붙여 런타임을 함께 넣으세요.

---

## 에셋

`New-Assets.ps1`이 타일과 exe 아이콘을 코드로 그려 냅니다. 색이나 모양을 바꿀 때는 스크립트
상단의 팔레트를 고치고 다시 실행하세요. 생성된 PNG는 커밋합니다.

빌드 스크립트는 매니페스트가 참조하는 로고가 스테이지에 모두 있는지 확인하고, 없으면 그 자리에서
멈춥니다. `makeappx`는 로고 누락을 그냥 "pack 실패"로만 알려 주기 때문입니다.

---

## 알려진 제약

- **WebView2 런타임은 패키지에 없습니다.** Windows 11에는 기본 포함이고 Windows 10에도 Edge를
  통해 사실상 항상 깔려 있지만, 없는 기기에서는 브라우저 칸이 칸 안에 안내 문구를 띄우고 나머지
  칸은 정상 동작합니다(앱이 죽지는 않습니다). 심사용 깨끗한 VM에서 브라우저 칸이 비어 보일 수
  있으므로, 심사 노트에 이 사실을 적어 두면 오해를 줄일 수 있습니다.
- **WACK(Windows App Certification Kit)는 여기서 실행할 수 없습니다.** `appcert.exe`는 NuGet
  패키징 도구에 없고 Windows SDK 전체 설치에만 들어 있습니다. 제출 전에 SDK가 설치된 기기에서
  한 번 돌려 보는 것을 권합니다.
- **자체 업데이트 기능은 Store 설치본에서 꺼집니다.** `AppVersion.IsStoreInstall`이 패키지 서명
  종류를 보고 판단하며, 이때 `UpdateService`는 확인도 설치도 하지 않고 업데이트 대화상자는 Store를
  여는 버튼만 남깁니다. Store 정책이 Store 앱의 업데이트를 Store에 맡기도록 정하고 있어서,
  숨기는 것이 아니라 실제로 동작하지 않게 만들어 둔 것입니다.
