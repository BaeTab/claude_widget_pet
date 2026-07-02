# Claude CLI 알림 연동

Claude Widget은 단순히 Claude Code의 실행 여부만 감지하는 데서 그치지 않고, **Claude Code의 자체 훅(hook) 핸들러**로도 동작할 수 있습니다. 이 기능을 켜면 Claude Code가 알림을 보내거나 작업을 마쳤을 때 위젯이 그 내용을 짧은 한국어 말풍선으로 띄워주는 것은 물론, **v1.2.0부터는 Claude가 지금 어떤 툴을 쓰고 있는지**(파일을 읽는지, 코드를 고치는지, 명령을 실행하는지 등)까지 아이콘 배지로 실시간 표시합니다.

## 1. 메뉴 토글

가재를 우클릭 → **Claude CLI 알림 연동** 체크박스를 켜면 됩니다. 처음 켤 때 "Claude CLI 알림을 연동했어요! 새 세션부터 적용돼요"라는 말풍선이 뜨는데, 문구 그대로 **바로 지금 실행 중인 Claude Code 세션에는 적용되지 않고, 다음에 새로 시작하는 세션부터 적용**됩니다(Claude Code가 시작 시점에 설정 파일을 읽기 때문).

이 토글 하나로 아래 §2의 이벤트 7종(알림·완료·프롬프트·세션·툴 사용)이 **한 번에** 등록/해제됩니다. 툴 사용 이벤트(`PreToolUse`)만 따로 끄고 싶다면, 훅 등록 자체는 유지한 채 **작업 상세 표시 (툴 인식)** 메뉴로 화면 표시만 끌 수 있습니다(§4 참고).

껐다 켰다 하는 것은 안전합니다. 끌 때는 위젯이 자신이 추가한 항목만 정확히 제거하고, 그 외 사용자가 직접 설정해둔 훅은 건드리지 않습니다.

## 2. `~/.claude/settings.json`에 실제로 기록되는 내용

토글을 켜면 위젯이 `%USERPROFILE%\.claude\settings.json`을 읽어(없으면 새로 만들어) 다음 **7개 이벤트**에 자기 자신을 훅 명령으로 병합합니다(`Services/CliHookInstaller.cs`의 `AllEvents`).

- `Notification`
- `Stop`
- `SubagentStop`
- `UserPromptSubmit`
- `SessionStart`
- `SessionEnd`
- `PreToolUse` — 툴을 하나 호출할 때마다 한 번씩 발생하는 고빈도 이벤트입니다. v1.2.0의 경량 헤드리스 진입점(§5) 덕분에 매 호출마다 실행해도 부담이 크지 않습니다.

각 이벤트에는 다음과 같은 형태의 항목이 추가됩니다(`PreToolUse`도 동일한 형태로 추가됩니다).

```json
{
  "hooks": {
    "Notification": [
      { "hooks": [ { "type": "command", "command": "\"<설치 경로>\\ClaudeWidget.exe\" --hook" } ] }
    ],
    "Stop": [
      { "hooks": [ { "type": "command", "command": "\"<설치 경로>\\ClaudeWidget.exe\" --hook" } ] }
    ],
    "SubagentStop": [
      { "hooks": [ { "type": "command", "command": "\"<설치 경로>\\ClaudeWidget.exe\" --hook" } ] }
    ],
    "UserPromptSubmit": [
      { "hooks": [ { "type": "command", "command": "\"<설치 경로>\\ClaudeWidget.exe\" --hook" } ] }
    ],
    "SessionStart": [
      { "hooks": [ { "type": "command", "command": "\"<설치 경로>\\ClaudeWidget.exe\" --hook" } ] }
    ],
    "SessionEnd": [
      { "hooks": [ { "type": "command", "command": "\"<설치 경로>\\ClaudeWidget.exe\" --hook" } ] }
    ],
    "PreToolUse": [
      { "hooks": [ { "type": "command", "command": "\"<설치 경로>\\ClaudeWidget.exe\" --hook" } ] }
    ]
  }
}
```

병합 로직의 특징(`Services/CliHookInstaller.cs`):

- **안전한 병합(추가만)**: 기존에 사용자가 등록해둔 다른 훅 항목은 그대로 유지됩니다. 오직 명령어에 `--hook`과 `ClaudeWidget`이 모두 포함된 항목만 "우리 항목"으로 인식하고 관리합니다.
- **멱등성**: 이미 등록되어 있으면 중복으로 추가하지 않습니다.
- **경로는 실행 중인 실제 exe 경로**: 현재 프로세스의 `MainModule.FileName`을 그대로 사용하므로, 사용자 단위 설치(`%LOCALAPPDATA%\Programs\Claude Widget`)든 시스템 설치(`%ProgramFiles%\Claude Widget`)든 실제 설치 위치가 자동으로 반영됩니다.
- **끌 때는 우리 항목만 제거**: `Disable()`은 명령어 조건에 맞는 항목만 배열에서 제거하고, 이벤트 배열이 비면 해당 이벤트 키 자체를 지웁니다. 다른 훅은 그대로 보존됩니다.

## 3. 이 기능이 다루는 이벤트

`Services/CliHookService.cs`의 `Compose()`가 Claude Code로부터 받은 훅 JSON(`hook_event_name`, `message`, `tool_name` 필드 등)을 다음과 같이 짧은 한국어 반응으로 바꿔줍니다.

| 이벤트 | 위젯 반응 |
|---|---|
| `PreToolUse` | (툴 인식 켜져 있을 때) **활동 아이콘 배지** 표시 + 툴별 말풍선(예: "파일 읽는 중…") — §4 참고 |
| `Notification` | 좌우로 살짝 흔들기 + 말풍선("알림: …" 또는 "클로드가 기다리고 있어요") + **트레이 풍선 알림(토스트)**, 숨겨져 있으면 다시 표시 — §5 참고 |
| `Stop` | 눈 깜빡임 애니메이션 + "작업을 마쳤어요!" |
| `SubagentStop` | "서브 작업 완료" |
| `UserPromptSubmit` | 통통 튀는 애니메이션 + "요청 받았어요!" |
| `SessionStart` | "세션 시작! 함께 코딩해요" |
| `SessionEnd` | "세션 종료. 수고했어요!" |
| 그 외 | 메시지가 있으면 메시지 그대로(텍스트 이벤트), 없으면 무시 |

메뉴에서 **Claude CLI 알림 연동**을 켜면 위 7개 이벤트가 전부 한 번에 등록됩니다(§2). 문구는 140자를 넘으면 말줄임표(…)로 잘립니다.

## 4. 툴 인식 (작업 상세 표시)

v1.2.0의 핵심 기능입니다. `PreToolUse` 훅이 등록되어 있으면, Claude Code가 툴을 호출할 때마다(파일 읽기, 코드 수정, 명령 실행 등) 위젯이 이를 감지해 화면에 반영합니다.

### 4-1. 툴 → 아이콘/문구 매핑

`CliHookService.ToolLabel()`(문구)과 `MainWindow.xaml.cs`의 `GlyphForTool()`(Segoe MDL2 아이콘)이 툴 이름을 다음과 같이 변환합니다.

| Claude Code 툴 | 말풍선 문구 | 배지 아이콘 |
|---|---|---|
| `Read`, `NotebookRead` | 파일 읽는 중… | 문서 아이콘 |
| `Edit`, `MultiEdit`, `Write`, `NotebookEdit` | 코드 쓰는 중… | 연필(편집) 아이콘 |
| `Bash`, `BashOutput`, `KillShell` | 명령 실행 중… | 커맨드 프롬프트 아이콘 |
| `Glob` | 파일 찾는 중… | 폴더 아이콘 |
| `Grep` | 코드 검색 중… | 돋보기 아이콘 |
| `WebFetch` | 웹 읽는 중… | 지구본 아이콘 |
| `WebSearch` | 웹 검색 중… | 지구본 아이콘 |
| `Task` | 에이전트 실행 중… | 사람(에이전트) 아이콘 |
| `TodoWrite` | 할 일 정리 중… | 체크리스트 아이콘 |
| `mcp__*` (모든 MCP 툴) | 도구 사용 중… | 컴포넌트 아이콘 |
| 그 외 전부 | 작업 중… | 기어(진행 중) 아이콘 |

### 4-2. 활동 배지 동작

- 툴 이벤트가 들어오면 가재 옆에 **활동 아이콘 배지**가 팝인(pop-in) 애니메이션과 함께 나타나고, 배지에 마우스를 올리면 툴팁으로 문구를 볼 수 있습니다.
- 배지는 툴 이벤트가 계속 들어오는 동안 유지되며, **약 6초간 새 이벤트가 없으면 자동으로 페이드아웃**됩니다.
- 텍스트 말풍선은 툴 호출마다 뜨면 너무 정신없기 때문에 **최대 4초에 한 번**만 갱신되도록 제한(throttle)되어 있습니다. 배지 자체는 이 제한 없이 매 이벤트마다 갱신됩니다.
- `Notification`(권한 대기)이나 `Stop`(작업 완료) 이벤트가 들어오면 활동 배지는 즉시 사라집니다.

### 4-3. "작업 상세 표시 (툴 인식)" 메뉴

우클릭 메뉴의 **작업 상세 표시 (툴 인식)** 체크박스(기본 **켜짐**)로 이 화면 표시 자체를 켜고 끌 수 있습니다. 설정은 `settings.json`에 `ToolAwareness` 값으로 저장됩니다.

> 주의: 이 토글은 어디까지나 **화면 표시**만 제어합니다. `PreToolUse` 훅 등록 여부(=Claude CLI 알림 연동이 켜져 있는지)와는 별개입니다. 즉 연동은 켜져 있는데 이 토글만 꺼두면, 훅 이벤트는 계속 위젯에 도착하지만 배지/말풍선으로 표시되지는 않습니다.

### 4-4. `PreToolUse`는 툴 호출마다 한 번씩 발생합니다

다른 이벤트(`Stop`, `Notification` 등)는 세션당 몇 번 안 되지만, `PreToolUse`는 **Claude가 툴을 쓸 때마다** 매번 발생하는 고빈도 이벤트입니다. v1.2.0에서 훅 진입점을 경량화(§6)하지 않았다면 부담스러웠을 빈도지만, 지금은 호출 1회에 ~120ms 수준이라 실사용에 문제가 없습니다. 다만 매우 짧은 시간에 툴이 연속으로 호출되는 상황(예: 대량 `Grep`/`Read` 루프)에서는 배지/말풍선이 빠르게 계속 바뀌는 것이 정상 동작입니다.

## 5. 권한 알림 강화 (트레이 토스트)

`Notification` 이벤트(대표적으로 "Claude가 파일 삭제 등 권한이 필요한 작업 전에 확인을 기다리는 상황")를 받으면, 위젯은 놓치기 쉬운 이 순간을 최대한 눈에 띄게 만듭니다.

1. 활동 배지를 즉시 숨깁니다.
2. 위젯이 숨겨져 있었다면 다시 나타납니다(`Show()`).
3. 가재가 좌우로 흔들리는 애니메이션(`WaveAnimation`)을 재생합니다.
4. 말풍선으로 알림 문구를 7초간 띄웁니다.
5. **시스템 트레이 풍선 알림(Windows 토스트)** 을 함께 띄웁니다(`NotifyIcon.ShowBalloonTip`). 위젯 창이 다른 모니터에 있거나 최소화되어 있어도, 트레이 토스트는 별도로 표시되므로 권한 대기 상황을 놓치기 어렵습니다.

## 6. 프롬프트/세션 반응

- **`UserPromptSubmit`**(사용자가 프롬프트를 제출) → 통통 튀는 애니메이션과 함께 "요청 받았어요!" 말풍선(2.5초).
- **`SessionStart`**(새 Claude Code 세션 시작) → "세션 시작! 함께 코딩해요" 말풍선.
- **`SessionEnd`**(세션 종료) → "세션 종료. 수고했어요!" 말풍선.

## 7. 휴식 넛지 (뽀모도로)

Claude Code 실행이 감지되어 위젯이 "작업 모드"로 전환된 뒤 **50분간 끊김 없이** 이어지면, 가재가 "50분째 열일 중! 잠깐 쉬어요"라고 한 번 살짝 알려줍니다. 이 기능은 훅 이벤트와 무관하게 **위젯 내부 타이머만으로 동작**하므로, Claude CLI 알림 연동을 켜지 않아도 작동합니다. 같은 작업 세션 동안 한 번만 알려주며, 작업이 끊겼다가(대기 상태로 돌아갔다가) 다시 시작되면 타이머도 새로 시작됩니다.

## 8. 진입점: `--hook` / `--notify` (경량 헤드리스)

Claude Widget 실행 파일은 두 가지 헤드리스(창 없는) 모드를 지원합니다. v1.2.0부터는 이 두 모드가 `App.xaml.cs`(WPF)가 아니라 **`Program.cs`의 전용 `Main`** 을 거쳐 WPF/PresentationFramework를 아예 로드하지 않고 종료합니다. 그 결과 훅 1회 호출 비용이 대략 **절반(~230ms → ~120ms)** 으로 줄었고, 이 덕분에 §2의 `PreToolUse`처럼 툴 호출마다 실행되는 고빈도 훅도 실용적인 비용으로 등록할 수 있게 되었습니다.

- **`ClaudeWidget.exe --hook`**: Claude Code가 훅 실행 시 호출하는 형태입니다. stdin으로 훅 JSON 페이로드를 받아 파싱한 뒤, 위에서 설명한 규칙대로 이벤트를 만들어 인박스에 기록하고 즉시 종료합니다.
- **`ClaudeWidget.exe --notify "메시지"`**: 임의의 문자열을 텍스트 이벤트로 인박스에 기록하는 간단한 수동/테스트용 진입점입니다. 예:
  ```bash
  ClaudeWidget.exe --notify "테스트 메시지입니다"
  ```

두 모드 모두 UI를 띄우지 않고 즉시 종료(exit code 0)하며, 실행 중인 위젯 인스턴스와는 별개의 프로세스로 동작합니다.

## 9. 인박스(inbox)와 구조화된 이벤트 (`CliEvent`)

v1.2.0부터 인박스 파일은 순수 텍스트가 아니라 **JSON `CliEvent` 객체**입니다(`Services/CliEvent.cs`).

```json
{ "Kind": "tool", "Tool": "Bash", "Text": "명령 실행 중…" }
```

- **`Kind`**: `text` | `tool` | `notify` | `prompt` | `stop` | `substop` | `session` 중 하나로, 위젯이 어떤 반응을 보일지 결정합니다(§3 표 참고).
- **`Tool`**: `Kind == "tool"`일 때만 채워지는 원본 Claude Code 툴 이름(예: `"Bash"`, `"Read"`, `"mcp__foo__bar"`).
- **`Text`**: 말풍선에 그대로 띄울 한국어 문구(이미 완성된 텍스트).

흐름은 다음과 같습니다.

1. `--hook` 또는 `--notify`로 실행된 프로세스가 `CliEvent`를 JSON으로 직렬화해 `%APPDATA%\ClaudeWidget\inbox\<타임스탬프>_<GUID>.json` 파일로 저장합니다(UTF-8, BOM 없음). `--notify "메시지"`는 `Kind = "text"`인 이벤트를 씁니다.
2. **실행 중인** 위젯은 `CliMessageService`(`FileSystemWatcher`)로 이 폴더를 감시하고 있다가, 파일이 생성되는 즉시 내용을 읽고 삭제한 뒤 `Kind`에 맞는 반응을 보입니다.
3. 위젯이 **꺼져 있던 동안** 쌓인 이벤트는, 다음에 위젯이 켜질 때 `DrainExisting()`이 처리합니다. 이때는 여러 개가 쌓여 있어도 한꺼번에 반응이 몰리지 않도록 **가장 최신 이벤트 하나만** 보여주고 나머지는 조용히 지웁니다.
4. **하위 호환**: 파일 내용이 `{`로 시작하지 않으면(예: 과거 버전이 쓴 순수 텍스트 파일) JSON으로 파싱하지 않고 `Kind = "text"`인 이벤트로 방어적으로 취급합니다. 즉 구버전 인박스 파일이나 수동으로 만든 텍스트 파일도 그대로 동작합니다.
5. 파일 I/O 실패, 잘못된 JSON 등은 전부 무시되도록 만들어져 있어 — 이벤트 하나가 잘못돼도 위젯 자체가 오류를 내거나 멈추지 않습니다.

## 10. 수동으로 직접 설정하고 싶다면

위젯의 토글을 쓰지 않고 `~/.claude/settings.json`을 직접 편집해도 됩니다. 아래 스니펫을 참고하세요(실제 경로는 설치 위치에 맞게 바꿔주세요 — 사용자 단위 설치는 보통 `%LOCALAPPDATA%\Programs\Claude Widget`, 시스템 설치는 `%ProgramFiles%\Claude Widget` 아래에 있습니다).

```json
{
  "hooks": {
    "Notification":      [ { "hooks": [ { "type": "command", "command": "\"C:\\Program Files\\Claude Widget\\ClaudeWidget.exe\" --hook" } ] } ],
    "Stop":              [ { "hooks": [ { "type": "command", "command": "\"C:\\Program Files\\Claude Widget\\ClaudeWidget.exe\" --hook" } ] } ],
    "SubagentStop":      [ { "hooks": [ { "type": "command", "command": "\"C:\\Program Files\\Claude Widget\\ClaudeWidget.exe\" --hook" } ] } ],
    "UserPromptSubmit":  [ { "hooks": [ { "type": "command", "command": "\"C:\\Program Files\\Claude Widget\\ClaudeWidget.exe\" --hook" } ] } ],
    "SessionStart":      [ { "hooks": [ { "type": "command", "command": "\"C:\\Program Files\\Claude Widget\\ClaudeWidget.exe\" --hook" } ] } ],
    "SessionEnd":        [ { "hooks": [ { "type": "command", "command": "\"C:\\Program Files\\Claude Widget\\ClaudeWidget.exe\" --hook" } ] } ],
    "PreToolUse":        [ { "hooks": [ { "type": "command", "command": "\"C:\\Program Files\\Claude Widget\\ClaudeWidget.exe\" --hook" } ] } ]
  }
}
```

`PreToolUse`를 빼면 알림/완료/세션 반응만 받고 툴 인식(활동 배지)은 받지 않는 "가벼운" 구성이 됩니다. 편집 후에는 위젯 메뉴 토글과 마찬가지로 **새로 시작하는 Claude Code 세션부터** 적용됩니다.

## 11. 문제 해결

**"말풍선이 안 떠요" 체크리스트**

1. **위젯이 실행 중인지 확인하세요.** 트레이 아이콘이 보이는지, 또는 작업 관리자에 `ClaudeWidget.exe`가 떠 있는지 확인합니다. 위젯이 꺼져 있으면 `--hook`이 인박스에 파일만 쌓아두고, 다음에 위젯을 켰을 때(그것도 최신 이벤트 하나만) 비로소 표시됩니다.
2. **연동이 켜져 있는지 확인하세요.** 우클릭 메뉴에서 **Claude CLI 알림 연동** 체크박스가 켜져 있는지 확인합니다. 또한 `~/.claude/settings.json`을 직접 열어 `hooks.Notification`/`hooks.Stop`/`hooks.SubagentStop` 등에 `ClaudeWidget.exe --hook` 명령이 실제로 들어가 있는지 확인합니다.
3. **새 세션인지 확인하세요.** 설정 변경은 다음 세션부터 적용됩니다. 연동을 켠 뒤에도 계속 열려 있던 기존 Claude Code 세션에서는 반영되지 않으니, 세션을 새로 시작해보세요.
4. **말풍선 표시 자체가 꺼져 있지 않은지 확인하세요.** 우클릭 메뉴의 **말풍선 표시**가 꺼져 있으면 어떤 말풍선도(CLI 알림 포함) 뜨지 않습니다.
5. **직접 테스트해보세요.** `ClaudeWidget.exe --notify "테스트"`를 커맨드라인에서 실행해 인박스 경로(`%APPDATA%\ClaudeWidget\inbox`)와 위젯의 파일 감시가 정상 동작하는지 먼저 확인할 수 있습니다. 이 명령으로도 말풍선이 안 뜬다면 훅 설정이 아니라 위젯 자체(실행 여부, 말풍선 표시 설정)의 문제일 가능성이 큽니다.
6. **경로에 특수문자·이동 이력이 있는지 확인하세요.** 위젯을 다른 폴더로 옮기거나 재설치한 뒤에는, `~/.claude/settings.json`에 기록된 경로가 예전 경로를 가리키고 있을 수 있습니다. 연동 토글을 껐다 다시 켜면 현재 실행 경로 기준으로 새로 기록됩니다.

**"툴 활동 배지가 안 보여요" 체크리스트**

1. **작업 상세 표시가 켜져 있는지 확인하세요.** 우클릭 메뉴의 **작업 상세 표시 (툴 인식)** 체크박스(기본 켜짐)가 꺼져 있으면 `PreToolUse` 이벤트가 도착해도 화면에 표시되지 않습니다.
2. **Claude CLI 알림 연동이 켜져 있는지 확인하세요.** 이 토글이 켜져 있어야 `PreToolUse` 훅이 `~/.claude/settings.json`에 등록됩니다. `hooks.PreToolUse`에 `ClaudeWidget.exe --hook` 항목이 있는지 직접 확인해도 좋습니다.
3. **새 세션인지 확인하세요.** 위 §1-3과 마찬가지로, 연동(또는 재연동)을 켠 뒤에는 Claude Code를 새로 시작해야 `PreToolUse` 훅이 적용됩니다.
4. **6초 자동 숨김을 정상 동작으로 오인하지 마세요.** 배지는 툴 호출이 뜸해지면(약 6초) 자동으로 사라집니다. Claude가 툴을 쓰지 않고 텍스트만 생성 중인 구간이라면 배지가 안 보이는 것이 정상입니다.
