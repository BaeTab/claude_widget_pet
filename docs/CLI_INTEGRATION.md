# Claude CLI 알림 연동

Claude Widget은 단순히 Claude Code의 실행 여부만 감지하는 데서 그치지 않고, **Claude Code의 자체 훅(hook) 핸들러**로도 동작할 수 있습니다. 이 기능을 켜면 Claude Code가 알림을 보내거나 작업을 마쳤을 때, 위젯이 그 내용을 짧은 한국어 말풍선으로 띄워줍니다.

## 1. 메뉴 토글

가재를 우클릭 → **Claude CLI 알림 연동** 체크박스를 켜면 됩니다. 처음 켤 때 "Claude CLI 알림을 연동했어요! 새 세션부터 적용돼요"라는 말풍선이 뜨는데, 문구 그대로 **바로 지금 실행 중인 Claude Code 세션에는 적용되지 않고, 다음에 새로 시작하는 세션부터 적용**됩니다(Claude Code가 시작 시점에 설정 파일을 읽기 때문).

껐다 켰다 하는 것은 안전합니다. 끌 때는 위젯이 자신이 추가한 항목만 정확히 제거하고, 그 외 사용자가 직접 설정해둔 훅은 건드리지 않습니다.

## 2. `~/.claude/settings.json`에 실제로 기록되는 내용

토글을 켜면 위젯이 `%USERPROFILE%\.claude\settings.json`을 읽어(없으면 새로 만들어) 다음 세 이벤트에 자기 자신을 훅 명령으로 병합합니다.

- `Notification`
- `Stop`
- `SubagentStop`

각 이벤트에는 다음과 같은 형태의 항목이 추가됩니다.

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

`Services/CliHookService.cs`의 `ComposeMessage()`가 Claude Code로부터 받은 훅 JSON(`hook_event_name`, `message` 필드 등)을 다음과 같이 짧은 한국어 문구로 바꿔줍니다.

| 이벤트 | 말풍선 문구 |
|---|---|
| `Notification` | 메시지가 있으면 "알림: …", 없으면 "클로드가 기다리고 있어요" |
| `Stop` | "작업을 마쳤어요!" |
| `SubagentStop` | "서브 작업 완료" |
| `SessionStart` | "세션 시작! 함께 코딩해요" (수신 시) |
| `SessionEnd` | "세션 종료. 수고했어요!" (수신 시) |
| 그 외 | 메시지가 있으면 메시지 그대로, 없으면 이벤트명 |

메뉴에서 실제로 등록하는 이벤트는 `Notification`/`Stop`/`SubagentStop` 세 가지이며, 나머지(`SessionStart`/`SessionEnd`)는 `--notify`로 수동 전달되거나 향후 확장을 위해 처리 로직만 준비되어 있습니다. 문구는 140자를 넘으면 말줄임표(…)로 잘립니다.

## 4. 진입점: `--hook` / `--notify`

Claude Widget 실행 파일은 두 가지 헤드리스(창 없는) 모드를 지원합니다(`App.xaml.cs`).

- **`ClaudeWidget.exe --hook`**: Claude Code가 훅 실행 시 호출하는 형태입니다. stdin으로 훅 JSON 페이로드를 받아 파싱한 뒤, 위에서 설명한 규칙대로 메시지를 만들어 인박스에 기록하고 즉시 종료합니다.
- **`ClaudeWidget.exe --notify "메시지"`**: 임의의 문자열을 그대로 인박스에 기록하는 간단한 수동/테스트용 진입점입니다. 예:
  ```bash
  ClaudeWidget.exe --notify "테스트 메시지입니다"
  ```

두 모드 모두 UI를 띄우지 않고 즉시 종료(exit code 0)하며, 실행 중인 위젯 인스턴스와는 별개의 프로세스로 동작합니다.

## 5. 인박스(inbox) 메커니즘

1. `--hook` 또는 `--notify`로 실행된 프로세스가 메시지 텍스트를 `%APPDATA%\ClaudeWidget\inbox\<타임스탬프>_<GUID>.txt` 파일로 저장합니다(UTF-8, BOM 없음).
2. **실행 중인** 위젯은 `CliMessageService`(`FileSystemWatcher`)로 이 폴더를 감시하고 있다가, 파일이 생성되는 즉시 내용을 읽고 삭제한 뒤 말풍선으로 띄웁니다.
3. 위젯이 **꺼져 있던 동안** 쌓인 메시지는, 다음에 위젯이 켜질 때 `DrainExisting()`이 처리합니다. 이때는 여러 개가 쌓여 있어도 말풍선이 연속으로 뜨지 않도록 **가장 최신 메시지 하나만** 보여주고 나머지는 조용히 지웁니다.
4. 파일 I/O 실패, 잘못된 JSON 등은 전부 무시되도록 만들어져 있어 — 알림 하나가 잘못돼도 위젯 자체가 오류를 내거나 멈추지 않습니다.

## 6. 수동으로 직접 설정하고 싶다면

위젯의 토글을 쓰지 않고 `~/.claude/settings.json`을 직접 편집해도 됩니다. 아래 스니펫을 참고하세요(실제 경로는 설치 위치에 맞게 바꿔주세요 — 사용자 단위 설치는 보통 `%LOCALAPPDATA%\Programs\Claude Widget`, 시스템 설치는 `%ProgramFiles%\Claude Widget` 아래에 있습니다).

```json
{
  "hooks": {
    "Notification": [ { "hooks": [ { "type": "command", "command": "\"C:\\Program Files\\Claude Widget\\ClaudeWidget.exe\" --hook" } ] } ],
    "Stop":         [ { "hooks": [ { "type": "command", "command": "\"C:\\Program Files\\Claude Widget\\ClaudeWidget.exe\" --hook" } ] } ]
  }
}
```

`SubagentStop`도 동일한 형태로 추가하면 서브 에이전트 완료 시에도 말풍선을 받을 수 있습니다. 편집 후에는 위젯 메뉴 토글과 마찬가지로 **새로 시작하는 Claude Code 세션부터** 적용됩니다.

## 7. 문제 해결

**"말풍선이 안 떠요" 체크리스트**

1. **위젯이 실행 중인지 확인하세요.** 트레이 아이콘이 보이는지, 또는 작업 관리자에 `ClaudeWidget.exe`가 떠 있는지 확인합니다. 위젯이 꺼져 있으면 `--hook`이 인박스에 파일만 쌓아두고, 다음에 위젯을 켰을 때(그것도 최신 메시지 하나만) 비로소 표시됩니다.
2. **연동이 켜져 있는지 확인하세요.** 우클릭 메뉴에서 **Claude CLI 알림 연동** 체크박스가 켜져 있는지 확인합니다. 또한 `~/.claude/settings.json`을 직접 열어 `hooks.Notification`/`hooks.Stop`/`hooks.SubagentStop`에 `ClaudeWidget.exe --hook` 명령이 실제로 들어가 있는지 확인합니다.
3. **새 세션인지 확인하세요.** 설정 변경은 다음 세션부터 적용됩니다. 연동을 켠 뒤에도 계속 열려 있던 기존 Claude Code 세션에서는 반영되지 않으니, 세션을 새로 시작해보세요.
4. **말풍선 표시 자체가 꺼져 있지 않은지 확인하세요.** 우클릭 메뉴의 **말풍선 표시**가 꺼져 있으면 어떤 말풍선도(CLI 알림 포함) 뜨지 않습니다.
5. **직접 테스트해보세요.** `ClaudeWidget.exe --notify "테스트"`를 커맨드라인에서 실행해 인박스 경로(`%APPDATA%\ClaudeWidget\inbox`)와 위젯의 파일 감시가 정상 동작하는지 먼저 확인할 수 있습니다. 이 명령으로도 말풍선이 안 뜬다면 훅 설정이 아니라 위젯 자체(실행 여부, 말풍선 표시 설정)의 문제일 가능성이 큽니다.
6. **경로에 특수문자·이동 이력이 있는지 확인하세요.** 위젯을 다른 폴더로 옮기거나 재설치한 뒤에는, `~/.claude/settings.json`에 기록된 경로가 예전 경로를 가리키고 있을 수 있습니다. 연동 토글을 껐다 다시 켜면 현재 실행 경로 기준으로 새로 기록됩니다.
