# 3D 캐릭터 오버레이 설계 (Godot 4 하이브리드)

> 상태: **설계안(초안)** · 대상: v1.4+ 실험 트랙 · 작성 2026-07-05
>
> 목표: 현재 WPF 벡터(2D) 캐릭터를 **3D 렌더링 + 화면 자유 배회**가 가능한 캐릭터로 교체한다.
> 단, 검증된 세션 로직·HUD·트레이·자동 업데이터는 **WPF에 그대로 두고**, 캐릭터 렌더링만
> 게임엔진 투명 오버레이로 분리하는 **하이브리드** 구조로 리스크를 최소화한다.

---

## 1. 큰 그림 — 두뇌(WPF) + 얼굴(엔진)

```
┌─────────────────────────────── Windows 데스크톱 ───────────────────────────────┐
│                                                                                │
│   [Godot 오버레이 프로세스]  ← 투명·항상 위·무포커스 창                          │
│      · 3D 캐릭터 렌더 + 애니메이션(idle/walk/sleep/celebrate/worried)           │
│      · 화면 배회(wander) 상태머신                                                │
│      · 말풍선(3D 앵커 빌보드) — 캐릭터를 따라다님                                │
│              ▲   stdin/stdout JSON (양방향 IPC)   │                             │
│              │                                    ▼                             │
│   [WPF 호스트 프로세스] (기존 앱, 두뇌)                                          │
│      · SessionRegistry / 훅 / TranscriptUsage / ProcessScanner                  │
│      · 세션 대시보드(HUD) 팝업, 트레이 아이콘, 우클릭 메뉴                       │
│      · 자동 업데이터, 설정(SettingsService)                                      │
│      · 엔진 프로세스 생명주기 관리(실행/감시/재시작/종료)                        │
└────────────────────────────────────────────────────────────────────────────────┘
```

**책임 분리**

| 기능 | WPF(두뇌) | Godot(얼굴) |
|------|:--------:|:-----------:|
| 세션 추적·훅·비용·자원 | ✅ | |
| 상태 판정(권한대기>작업>유휴) | ✅ (판정) | (표현만) |
| 3D 캐릭터 렌더·애니메이션 | | ✅ |
| 화면 배회 | | ✅ |
| 말풍선 | (텍스트 전달) | ✅ (렌더) |
| 세션 대시보드(HUD) | ✅ | |
| 트레이·컨텍스트 메뉴·설정 | ✅ | |
| 자동 업데이트 | ✅ | |

→ HUD처럼 **데이터 패널**은 WPF가 압도적으로 유리하므로 남기고, **살아있는 캐릭터 연출**만 엔진으로 옮긴다.

---

## 2. 엔진 선택 — Godot 4 권장

| | **Godot 4 (권장)** | Unity |
|--|--------------------|-------|
| 라이선스/비용 | MIT, 무료 | 무료(조건부), 정책 변동 이력 |
| 런타임 크기 | 경량(익스포트 ~50–70MB) | 큼(수백 MB) |
| 투명 창 | **엔진 내장** (아래 §3) | 플러그인 필요(UniWindowController 등) |
| 3D 에셋 | glTF/.glb 임포트, VRM(애드온) | Mixamo/UniVRM 등 성숙 |
| C# 지원 | 있음(.NET) — 기존 스택과 언어 통일 가능 | C# 네이티브 |
| 데스크톱 마스코트 적합성 | **높음**(가볍고 투명·패스스루 내장) | 높지만 무겁다 |

**결론:** "가벼운 위젯"이라는 정체성 + 투명/패스스루 내장 + 작은 런타임 → **Godot 4**.
휴머노이드 VRM 아바타를 쓰거나 고급 렌더가 필요하면 Unity가 유리하지만, 가재 마스코트에는 과함.
Godot는 **C#(.NET)** 도 지원하므로 IPC/로직을 팀 스택(C#)과 통일할 수 있다(선택).

---

## 3. 투명 오버레이 창 — Godot 4 핵심 API

검증된 Godot 4 API (context7/godot-docs 확인):

- **퍼픽셀 투명**
  - 프로젝트 설정: `display/window/per_pixel_transparency/allowed = true`
  - `get_viewport().transparent_bg = true`
  - `Window.transparent = true` + `WINDOW_FLAG_TRANSPARENT`
- **무테/항상 위/무포커스**
  - `display/window/size/borderless = true`
  - `DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_ALWAYS_ON_TOP, true)`
  - `DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_NO_FOCUS, true)`
    → *"창을 포커스할 수 없고 마우스 클릭 외 입력은 무시"* — 데스크톱 펫에 정확히 맞음(포커스 훔치지 않음).
- **클릭 통과(passthrough)**
  - `DisplayServer.WindowSetMousePassthrough(PackedVector2Array)` 또는 `Window.mouse_passthrough_polygon`
  - 지정한 **폴리곤 영역만 마우스를 받고, 바깥은 통과**. 빈 배열이면 전체 인터셉트(기본).

### ⚠️ Windows 특이사항 (설계에 결정적)
> Godot 문서 명시: **Windows에서는 passthrough 폴리곤 "바깥" 영역이 그려지지 않는다**
> (Linux/macOS는 그려짐).

즉 전체화면 투명 오버레이 + 패스스루 폴리곤을 쓰면 **폴리곤 밖의 파티클·이펙트·말풍선이 Windows에서 안 보인다.** 이를 피하는 두 가지 창 모델:

**모델 A — "따라다니는 작은 창"(권장 시작점)**
- 캐릭터 크기에 딱 맞는 작은 투명 창 하나. 배회는 **창 자체를 이동**(`window_set_position`)해서 구현(현재 WPF와 동일 발상, 단 3D).
- 말풍선/이펙트가 필요하면 창을 캐릭터+여백만큼만 키움.
- 장점: 패스스루 렌더 클리핑 이슈 회피, GPU 부담 작음, 멀티모니터 이동 단순.
- 단점: 창보다 큰 광역 이펙트 제한.

**모델 B — "전체화면 오버레이"**
- 가상 화면 전체를 덮는 투명 창. 캐릭터는 창 내부 좌표에서 이동.
- Windows 렌더 클리핑 때문에 **패스스루 폴리곤을 캐릭터 실루엣에 매 프레임 갱신**해야 하고, 그러면 폴리곤 밖 연출은 안 그려짐 → 광역 이펙트를 포기하거나, 네이티브 Win32(`WS_EX_LAYERED|WS_EX_TRANSPARENT` 토글 + 히트테스트)로 우회.
- 장점: 창 이동 없이 자유 이동·경계 연출 유연.
- 단점: Windows에서 구현 난이도↑, 상시 전체화면 컴포지팅 비용↑.

→ **Phase 0/1은 모델 A로 시작**, 광역 연출이 꼭 필요해지면 모델 B(+네이티브 우회)로 승격.

---

## 4. IPC 프로토콜 — stdin/stdout JSON(라인 구분)

WPF가 Godot exe를 **자식 프로세스로 실행**하고, 표준입출력으로 줄 단위 JSON을 주고받는다.
(포트 불필요·방화벽/AV 마찰 적음·구현 단순. 나중에 named pipe로 승격 가능.)

**WPF → 엔진 (명령)**
```jsonc
{"type":"state","value":"waiting|working|idle|ended","urgent":true}   // 대표 상태
{"type":"emote","value":"celebrate|worried|sleep|greet|wake"}          // 1회성 연출
{"type":"say","text":"권한 확인을 기다려요","ms":6000}                  // 말풍선
{"type":"roam","mode":"wander|follow_cursor|stay"}                     // 배회 모드
{"type":"config","scale":1.0,"theme":"Claude","speech":true}          // 설정 동기화
{"type":"anchor","x":1280,"y":700}                                    // (선택)기준 위치
{"type":"shutdown"}                                                    // 종료
```

**엔진 → WPF (이벤트)**
```jsonc
{"type":"hello","pid":12345,"ver":"0.1.0"}                            // 핸드셰이크
{"type":"click","target":"character"}     // 캐릭터 클릭 → WPF가 HUD 토글
{"type":"click","target":"statusdot"}     // 상태점 상당 클릭 → 업데이트/HUD
{"type":"pos","x":1310,"y":642}           // (모델 B) 말풍선 위치 참고용
{"type":"error","msg":"..."}
```

**생명주기(WPF 담당):** 시작 시 엔진 spawn → `hello` 대기 → 죽으면 N회 재시작(백오프) →
설정에서 3D 모드 끄면 엔진 종료하고 WPF 2D 캐릭터로 폴백. **2D 캐릭터는 폴백으로 유지**(무엇이든 실패 시 앱이 캐릭터 없이 남지 않도록).

---

## 5. 3D 에셋 파이프라인 (가장 큰 비용)

> 상태: **구축 완료** — Blender 프로시저럴 파이프라인이 실제로 구현되어 v2("게임 퀄리티") 산출물까지 나온 상태다.
> 상세 스펙·재빌드 방법·GLB 이관 주의사항·한계는 [`assets/character3d/README.md`](../assets/character3d/README.md)에 정리되어 있으므로 여기서는 요약만 남긴다.

**캐릭터 방향 변경**: 초안 시점의 "가재 재현" 대신, 사용자 피드백을 반영해 **둥근 블롭 + 가재 하이브리드**로 확정했다(집게·꼬리·머리 위 별은 유지, 실루엣만 더 마스코트/피규어에 가깝게 조정).

- 코드 없이 `assets/character3d/build_pet.py` 하나로 모델링·머티리얼·리깅 없는 `Idle` 애니메이션·GLB 익스포트·베이스 렌더까지 절차적으로 생성한다(외부 에셋/텍스처 다운로드 없음).
- 산출물 `claude_pet.glb`(메시+머티리얼+`Idle` 노드 애니메이션)를 Godot `AnimationPlayer`에 연결해 재생한다.
- **아직 없는 것(Phase 2 과제)**: 아마추어/스키닝, 파츠별 애니메이션(`walk`/`sleep`/`celebrate`/`worried`/`wave` 등 상태별 클립), UV 텍스처, 셰이더 기반 표정. 지금은 몸 전체가 통째로 움직이는 `Idle` 바운스 하나뿐이라, §9 Phase 2에서 리깅과 함께 클립을 늘려야 한다.

---

## 6. 배회/행동 상태머신 (엔진 내부)

```
IDLE(제자리 숨쉬기)
  ├─(랜덤 타이머)→ WANDER: 목적지 선정 → walk 애니 → 도착 → IDLE
  ├─(follow_cursor 모드)→ 커서 근처로 이동
  ├─(WPF state=working)→ 배회 중단, 집중 포즈  ← 작업 방해 금지
  ├─(WPF state=waiting/urgent)→ 주의 환기 연출(worried + 흔들기)
  └─(장시간 무활동)→ SLEEP(눈감음, Zzz), 이벤트 오면 wake
```

- **경계 인지:** 멀티모니터 작업영역 안에서만 이동(작업표시줄·화면 밖 회피).
- **매너 옵션(설정):** `배회 on/off`, `커서 따라오기 on/off`, `제자리 고정`. 기본은 **작업 중 정지**로 성가심 방지.

---

## 7. 패키징 & 자동 업데이트

- Godot 익스포트(`ClaudeWidgetPet.exe` + `.pck`)를 **InnoSetup `[Files]` 에 동봉** → 한 인스톨러로 배포.
- WPF가 설치 경로에서 엔진 exe를 실행. **버전·업데이트 파이프라인은 기존 그대로**(installer 하나에 둘 다 포함) → 자동 업데이터 로직 변경 최소.
- 설치 크기 **+50–70MB** 증가는 감수. 설정에 "3D 캐릭터" 토글을 두어 저사양 사용자는 2D 유지.

---

## 8. 리스크 & 완화

| 리스크 | 완화 |
|--------|------|
| **Windows passthrough 렌더 클리핑**(§3) | 모델 A로 시작, 광역 연출 필요 시 네이티브 우회 |
| **상시 GPU/배터리 소모**(엔진은 WPF보다 무거움) | FPS 캡(예: 30), 숨김/무활동 시 저전력·렌더 일시정지, 작업 중 최소 연출 |
| **z-order 충돌**(엔진 오버레이 vs WPF HUD 둘 다 topmost) | 캐릭터 클릭 시 HUD를 엔진 위로 올림(HUD 열릴 때 오버레이 topmost 잠시 양보/Owner 지정) |
| **두 번째 exe → AV/시작 마찰**(전역규칙 #8 백신 훅 이슈 기록됨) | 서명, 시작 시 TMP 우회, 재시작 백오프, 실패 시 2D 폴백 |
| **포커스 훔침** | `WINDOW_FLAG_NO_FOCUS` |
| **DPI/멀티모니터 좌표** | Godot·Win32 좌표 정합 테스트(Phase 0) |
| **스택 이원화(WPF+GDScript/C#)** | IPC 경계 얇게 유지, 엔진은 "표현"만·판정은 WPF |

---

## 9. 단계별 계획 (리스크 큰 것부터)

- **Phase 0 — 창 스파이크(가장 위험한 기술 먼저)** ✅ 완료 (2026-07-05)
  Godot 4: 투명·무테·항상 위·무포커스 창에 임시 3D 큐브를 띄우고, **클릭 통과 + 캐릭터 클릭 감지 + 멀티모니터 이동**을 검증. 산출물: 데스크톱을 돌아다니는 큐브 + 캐릭터만 클릭되는 데모.
  결과: Godot 4.7 Model A 스파이크 — 투명·무테·무포커스·항상위 창에 glb 렌더+화면 배회 검증(godot_overlay/). 잠금세션 탓에 라이브 클릭/패스스루/z-order 3건 미검증.
- **Phase 1 — IPC 연결**
  WPF가 엔진 spawn, stdio JSON. WPF 세션 상태 → 엔진 색/애니 변경, 엔진 클릭 → WPF HUD 토글. 캐릭터는 아직 임시 모델.
- **Phase 2 — 실제 3D 캐릭터** ✅ 완료 (2026-07-05)
  가재 모델·리깅·애니메이션 제작, 세션 이벤트 → 표정/연출 매핑, 말풍선을 엔진으로 이관.
  결과: 9본 리그 + 5 애니(idle/walk/sleep/celebrate/worried) glb에 포함.
- **Phase 3 — 배회 폴리시 & 배포**
  wander AI·커서 추적·작업 중 정지·설정 토글, InnoSetup 동봉, FPS/전력 튜닝. 2D 폴백 유지.

각 Phase는 독립적으로 가치가 있고, 언제든 중단해도 기존 2D 위젯은 그대로 동작한다.

---

## 10. 열린 질문(진행 전 확정 필요)

1. 엔진 언어: Godot **GDScript**(빠른 개발) vs **C#**(팀 스택 통일) — 어느 쪽?
2. 캐릭터 비주얼: 지금 가재를 **3D로 재현**? 아니면 이참에 디자인도 새로?
3. 3D 모델: 직접 Blender 제작 vs 외주/에셋 구매?
4. 배포 크기 +50–70MB 및 GPU 상시 사용 — 허용 범위인가(저사양 사용자용 2D 토글 전제)?
5. 창 모델: A(따라다니는 작은 창)로 시작 확정?
