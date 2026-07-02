# 자동 업데이트 동작 방식

Claude Widget은 별도의 업데이트 서버 없이, **GitHub Releases**를 그대로 업데이트 소스로 사용합니다. 이 문서는 버전이 어디서 정해지고, 새 버전을 어떻게 감지하며, 설치가 어떻게 조용히 끝나는지 처음부터 끝까지 설명합니다.

## 1. 버전은 어디서 정해지나

버전 값은 두 곳에서 관리되며, 릴리스를 낼 때마다 함께 맞춰줘야 합니다.

| 위치 | 역할 |
|---|---|
| `Claude_Widget/Claude_Widget.csproj`의 `<Version>` / `<AssemblyVersion>` / `<FileVersion>` | 빌드된 실행 파일(`ClaudeWidget.exe`)의 어셈블리 버전. 앱이 자기 자신의 현재 버전을 판단하는 근거(`AppInfo.CurrentVersion`, `Services/AppInfo.cs`). |
| `setup.iss`의 `#define MyAppVersion` | Inno Setup 인스톨러의 버전 및 출력 파일명(`ClaudeWidget_Setup_{#MyAppVersion}.exe`)을 결정. `ISCC /DMyAppVersion=X.Y.Z setup.iss`로 커맨드라인에서 덮어쓸 수도 있음(CI가 이 방식을 사용). |

실행 중인 앱의 현재 버전은 `AppInfo.CurrentVersion`이 `Assembly.GetExecutingAssembly().GetName().Version`을 읽어 `Major.Minor.Build` 3부분으로 만들어 사용합니다.

## 2. 새 버전 확인: GitHub Releases API

`Services/UpdateService.cs`의 `CheckForUpdateAsync()`가 앱 시작 시, 그리고 **6시간마다** 다음 엔드포인트를 호출합니다.

```
GET https://api.github.com/repos/BaeTab/claude_widget_pet/releases/latest
```

처리 로직:
1. 요청에 실패하거나(네트워크 오류 등) 파싱에 실패하면 **예외를 던지지 않고 조용히 `null`을 반환**합니다 — 업데이트 확인 실패가 위젯 동작에 영향을 주지 않습니다.
2. 응답이 `draft`이거나 `prerelease`이면 무시합니다(정식 릴리스만 대상).
3. `tag_name`(예: `v1.2.0`)을 파싱해 `Version` 객체로 만들고, 실행 중인 `AppInfo.CurrentVersion`과 비교합니다. 태그 버전이 더 크지 않으면 업데이트 없음으로 처리합니다.
4. 릴리스의 `assets` 배열에서 이름이 `ClaudeWidget_Setup_`으로 시작하고 `.exe`로 끝나는 자산을 찾습니다(아래 "자산 이름 규칙" 참고). 찾지 못하면 업데이트가 있어도 무시됩니다(설치 파일이 없으므로).
5. 조건을 모두 만족하면 `UpdateInfo`(버전, 태그명, 다운로드 URL, 자산명, 릴리스 노트, 파일 크기)를 반환합니다.

새 버전이 발견되면 UI에서 상태 점이 파랗게 펄스 애니메이션으로 깜빡이고, "새 버전 vX.Y.Z 나왔어요! 클릭해서 업데이트" 말풍선이 뜹니다. 사용자는 우클릭 메뉴의 **업데이트 확인**으로 즉시 수동 확인도 할 수 있고, **자동 업데이트 확인** 토글로 자동 확인 자체를 끌 수도 있습니다.

## 3. 설치 자산 이름 규칙 (반드시 지켜야 함)

앱 내 업데이터는 릴리스 자산 이름을 **정확히 다음 패턴**으로 기대합니다.

```
ClaudeWidget_Setup_<version>.exe
```

- `<version>`은 태그에서 `v` 접두사를 뗀 semver(예: 태그 `v1.2.0` → `1.2.0`)입니다.
- 이 이름은 `setup.iss`의 `OutputBaseFilename=ClaudeWidget_Setup_{#MyAppVersion}`이 만들어내는 Inno Setup 인스톨러 출력 파일명과, `UpdateService`가 릴리스 자산을 찾을 때 쓰는 접두사(`ClaudeWidget_Setup_`)/확장자(`.exe`) 매칭 조건이 정확히 일치해야 합니다.
- 자산 이름이 이 규칙을 벗어나면(오타, 접두사 누락 등) `CheckForUpdateAsync()`가 해당 릴리스를 "업데이트 있음"으로 판단하지 않으므로, 기존 설치본이 영영 업데이트를 인식하지 못합니다.

## 4. 다운로드 → 조용한 설치 → 재실행

말풍선 클릭 시 다음 순서로 진행됩니다.

1. **다운로드** (`DownloadInstallerAsync`): 설치 파일을 `%TEMP%\ClaudeWidgetUpdate\<자산명>`에 스트리밍으로 받으며, 진행률(0~1)을 말풍선에 "업데이트 다운로드 중… NN%"로 표시합니다.
2. **조용한 설치** (`LaunchInstaller`): 다운로드한 인스톨러를 다음 인자로 실행합니다.
   ```
   /SILENT /NOCANCEL /SP-
   ```
   `/SILENT`는 진행률 표시줄만 보여주고 나머지 대화상자는 생략합니다. 이후 앱은 스스로 종료해 실행 파일이 잠기지 않게 합니다.
3. **재설치**: Inno Setup은 `CloseApplications=yes`로 설정되어 있어(Restart Manager를 통해) 실행 중인 위젯이 있으면 자동으로 닫고 파일을 덮어씁니다. `RestartApplications=no`이므로 재실행은 인스톨러가 직접 담당합니다.
4. **재실행**: `setup.iss`의 `[Run]` 섹션에 `skipifsilent`가 **없기 때문에**, 조용한 설치 뒤에도 `ClaudeWidget.exe`가 자동으로 다시 시작됩니다. 즉 사용자는 아무 것도 누르지 않아도 새 버전이 켜진 채로 이어집니다.

## 5. CI 파이프라인 (`.github/workflows/release.yml`)

`vX.Y.Z` 형태의 태그를 푸시하면(또는 `workflow_dispatch`로 수동 실행하면) GitHub Actions가 다음을 자동으로 수행합니다.

1. **버전 산출**: 태그명(`refs/tags/vX.Y.Z`)에서 `v`를 뗀 semver를 뽑아냅니다. `workflow_dispatch`로 수동 실행한 경우 입력값 `version`을 사용합니다.
2. **빌드**: `dotnet publish Claude_Widget/Claude_Widget.csproj -c Release -r win-x64 --self-contained true -o publish`로 self-contained 빌드를 생성합니다.
3. **Inno Setup 설치**: Chocolatey로 `innosetup`을 설치합니다.
4. **인스톨러 컴파일**: `ISCC /DMyAppVersion=<version> setup.iss`로 `installer_output/ClaudeWidget_Setup_<version>.exe`를 생성합니다(태그 버전으로 `setup.iss`의 기본값을 덮어씀).
5. **검증**: 예상한 경로에 설치 파일이 실제로 생성됐는지 확인합니다.
6. **릴리스 생성/업데이트**: `softprops/action-gh-release`로 태그명을 릴리스명으로 하는 GitHub Release를 만들고(또는 갱신하고), 인스톨러를 자산으로 첨부합니다. 릴리스 노트는 자동 생성(`generate_release_notes: true`)됩니다.

이 파이프라인이 만들어내는 태그명(`tag_name`)과 자산 파일명이 곧 §2, §3에서 앱이 검사하는 값이므로, 워크플로를 건드릴 때는 이 계약을 깨지 않도록 주의해야 합니다.

## 6. 릴리스를 새로 낼 때 (수동 절차)

1. `Claude_Widget/Claude_Widget.csproj`의 `<Version>`(및 `<AssemblyVersion>`/`<FileVersion>`)을 새 버전으로 올립니다.
2. `setup.iss`의 `#define MyAppVersion` 기본값도 같은 버전으로 맞춥니다(CI에서는 `/DMyAppVersion`으로 덮어써지지만, 로컬 빌드/일관성을 위해 함께 갱신).
3. 변경 사항을 커밋합니다.
4. 태그를 만들고 푸시합니다.
   ```bash
   git tag vX.Y.Z
   git push --tags
   ```
5. GitHub Actions가 인스톨러를 빌드하고 릴리스를 게시합니다.
6. 이미 설치되어 있는 사용자들의 위젯은 다음 자동 확인 주기(또는 수동 **업데이트 확인** 클릭) 시 새 버전을 감지해 스스로 업데이트합니다.
