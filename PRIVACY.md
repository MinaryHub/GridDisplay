# Grid Display 개인정보처리방침

최종 개정: 2026-08-24 · 적용 버전: 1.0.0 이상

## 요약

**Grid Display는 개인정보를 수집하지 않습니다.** 개발자에게 전송되는 데이터는 없습니다. 사용량
분석, 광고, 추적, 크래시 보고를 위한 자체 통신 기능이 없습니다.

앱이 다루는 모든 데이터는 사용자의 PC에만 저장되며, 앱이 통신하는 상대는 **사용자가 직접 지정한
주소**뿐입니다.

## 앱이 저장하는 것

| 저장 위치 | 내용 |
| --- | --- |
| `%APPDATA%\GridDisplay\state.json` | 격자 크기, 세트 이름, 각 칸에 지정한 웹 주소와 프로그램 경로, 앱 설정 |
| `%LOCALAPPDATA%\GridDisplay\WebView2` | 브라우저 칸의 프로필 — 쿠키, 로컬 저장소, 로그인 세션, 캐시 |

브라우저 칸에서 이동한 주소는 다음 실행에 같은 화면을 복원하기 위해 `state.json`에 자동으로
기록됩니다. 이 파일은 사용자 PC를 떠나지 않습니다.

브라우저 칸에 로그인하면 그 세션은 위 프로필 폴더에 남습니다. 사이트에 제공한 자격 증명은 앱이
읽거나 저장하지 않고, WebView2(Microsoft Edge)가 브라우저와 같은 방식으로 처리합니다.

Microsoft Store 또는 MSIX로 설치한 경우 위 두 경로는 Windows에 의해
`%LOCALAPPDATA%\Packages\<패키지 패밀리 이름>\LocalCache\` 아래로 리디렉션됩니다.

## 앱이 통신하는 상대

1. **사용자가 칸에 지정한 웹사이트** — 브라우저 칸이 그 주소에 직접 접속합니다. 해당 사이트가
   수집하는 정보에는 그 사이트의 개인정보처리방침이 적용됩니다.
2. **로그아웃 방지(keep-alive)를 켠 칸** — 켜 둔 칸에 한해, 열려 있는 그 페이지 자체로 주기적인
   `HEAD` 요청을 보내 세션이 만료되지 않게 합니다. 다른 주소로는 보내지 않습니다.
3. **업데이트 확인 주소** — 사용자가 설정에 직접 입력한 주소(예: 사내 서버의 `.appinstaller`)만
   조회합니다. 비워 두면 아무 곳에도 연결하지 않습니다.
   **Microsoft Store에서 설치한 경우 앱은 업데이트 확인을 아예 하지 않습니다** — 업데이트는
   Microsoft Store가 처리합니다.

그 밖의 어떤 서버에도 접속하지 않습니다.

## 제3자 구성 요소

- **Microsoft Edge WebView2** — 브라우저 칸의 렌더링 엔진입니다. 웹 탐색과 관련한 데이터 처리는
  Microsoft의 방침을 따릅니다: <https://privacy.microsoft.com/privacystatement>
- **Microsoft Store** — Store를 통해 설치·업데이트하는 과정에서 Microsoft가 수집하는 정보 역시
  위 Microsoft 개인정보처리방침의 적용을 받습니다. 이는 앱이 아니라 Store의 동작입니다.

## 사용자가 지정해 실행하는 프로그램

프로그램 칸은 사용자가 경로를 지정한 Windows 프로그램을 실행해 그 창을 칸에 편입합니다. 그
프로그램이 무엇을 저장하거나 전송하는지는 해당 프로그램의 방침에 따르며, Grid Display는 그
내용에 관여하지 않습니다.

## 데이터 삭제

앱을 제거한 뒤 위 표의 두 폴더를 지우면 앱이 남긴 모든 데이터가 사라집니다. 로그인 세션과 쿠키도
함께 삭제됩니다.

Microsoft Store / MSIX 설치본은 제거 시 Windows가 해당 폴더를 함께 정리합니다.

## 아동의 개인정보

이 앱은 개인정보를 수집하지 않으며, 아동을 대상으로 하지 않습니다.

## 문의

개인정보 처리에 관한 문의: `<연락처 이메일을 여기에 적으세요>`

---

# Grid Display Privacy Policy

Last updated: 2026-08-24 · Applies to version 1.0.0 and later

## Summary

**Grid Display collects no personal information.** No data is sent to the developer. The app has no
analytics, advertising, tracking, or crash-reporting facility of its own.

Everything the app stores stays on the user's PC, and the only addresses it contacts are the ones
the user entered.

## What the app stores

| Location | Contents |
| --- | --- |
| `%APPDATA%\GridDisplay\state.json` | Grid size, set names, the web address or program path assigned to each cell, app settings |
| `%LOCALAPPDATA%\GridDisplay\WebView2` | Browser-cell profiles — cookies, local storage, sign-in sessions, cache |

Addresses visited in a browser cell are recorded in `state.json` so the same screen can be restored
on the next launch. The file never leaves the user's PC. Credentials entered on a site are handled
by WebView2 (Microsoft Edge) exactly as a browser would; the app neither reads nor stores them.

Under a Microsoft Store or MSIX install, Windows redirects both paths under
`%LOCALAPPDATA%\Packages\<package family name>\LocalCache\`.

## What the app contacts

1. **Websites the user assigned to a cell** — the browser cell connects to them directly. Whatever
   those sites collect is governed by their own privacy policies.
2. **Cells with keep-alive enabled** — for those cells only, a periodic `HEAD` request to the page
   already open in them, so the session does not expire. Nowhere else.
3. **The update address the user typed in settings** (e.g. an `.appinstaller` on an intranet
   server). Left empty, nothing is contacted. **When installed from Microsoft Store the app does
   not check for updates at all** — the Store handles that.

No other server is contacted.

## Third-party components

- **Microsoft Edge WebView2**, the rendering engine behind browser cells. Web browsing data is
  handled per Microsoft's statement: <https://privacy.microsoft.com/privacystatement>
- **Microsoft Store**, for installation and updates. Anything Microsoft collects in that process is
  covered by the same Microsoft statement; it is the Store's behaviour, not the app's.

## Programs the user launches

A program cell runs a Windows application whose path the user supplied and adopts its window. What
that program stores or transmits is governed by its own policy; Grid Display is not involved.

## Deleting data

Uninstall the app and delete the two folders above; nothing the app wrote remains, sign-in sessions
and cookies included. A Store/MSIX uninstall cleans those folders up itself.

## Children's privacy

The app collects no personal information and is not directed at children.

## Contact

Privacy enquiries: `<your contact email here>`
