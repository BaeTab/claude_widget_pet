# claude_pet v2 — 게임 퀄리티 상세 스펙 (Fable 작성)

> 목표: 현재 프리미티브 조립 수준(v1)을 **수집형 비닐 피규어 / 닌텐도풍 마스코트** 렌더 퀄리티로 끌어올린다.
> 구현 대상: `assets/character3d/build_pet.py` **in-place 업그레이드** (파일명·산출물 계약 유지).
> 실행: `"C:\Program Files\Blender Foundation\Blender 4.5\blender.exe" --background --python build_pet.py`
> **필수**: 실행 전 `TMP`/`TEMP`=`C:\temp\gradle-tmp` (이 PC 백신 우회, 전역규칙 #8).

## 0. 산출물 계약 (변경 금지)
| 파일 | 내용 |
|---|---|
| `claude_pet.glb` | Godot용 GLB. 머티리얼 + `Idle` 애니메이션 포함. **< 8MB** |
| `claude_pet.blend` | 소스 |
| `claude_pet_hero.png` | 정면 3/4 뷰티샷 |
| `claude_pet_front.png` | 정면 |
| `claude_pet_side.png` | 측면 (집게+꼬리 보임) |
| `claude_pet_back.png` | 후면 3/4 (꼬리 부채 보임) |
| `claude_pet_face.png` | 얼굴 클로즈업 (눈 퀄리티 검수용) |

## 1. 캐릭터 정의 (변경 금지 — 사용자 확정 디자인)
둥근 블롭 + 가재 하이브리드. Claude 주황 + 머리 위 골드 별.
- 몸: 큰 라운드 바디(약간 세로로 김), 밝은 배 패치
- 얼굴: 큰 눈 2개(흰자+큰 검은자+하이라이트), 볼터치, 작은 미소
- **눈 규칙(중요, 사용자 피드백 2회 반영됨)**: 동공은 흰자 표면에 **얇은 렌즈**처럼 얹힘. 앞으로 ~0.05만 돌출. 더 나오면 "곤충 눈알"(기괴), 더 묻으면 안 보임.
- 팔: 짧은 팔 + **벌어진 집게**(palm 아래턱 + finger 위턱)
- 다리: 콩 모양 발 2개
- 꼬리: 뒤쪽(+Y) 마디 3개 + 위로 들린 **부챗살 5장**(uropod)
- 별: 머리 위 4-point 별 — **v2에서 입체화** (§3.4)

## 2. 지오메트리 퀄리티
1. **폴리 밀도**: 주요 구 segments≥64/rings≥40, 보조 부위 ≥48/28. 또는 낮은 밀도 + Subdivision Surface(level 2, render 2) 모디파이어 — 익스포트 시 `export_apply=True`로 베이크되므로 GLB 폴리 예산(전체 ≤ 150k tri) 내에서.
2. **셰이딩**: 전 메시 shade smooth. Blender 4.1+ 이므로 `use_auto_smooth` 없음 — 폴리곤 `use_smooth=True`로 충분(구체류). 별 등 각진 메시는 "Smooth by Angle" 모디파이어 or 그냥 플랫+베벨.
3. **파츠 결합부**: 인접 파츠는 **35% 이상 깊게 묻어** 교차선이 "부품 라인"으로 읽히게. 팔-몸, 발-몸, 꼬리마디-몸 사이에 티 나는 틈/찌른 자국 금지. 손목/발목엔 작은 블렌딩 구 추가 가능.
4. **눈 소켓**: 흰자를 몸에 ~55% 묻어 눈두덩이처럼. 흰자 주위에 몸색보다 살짝 어두운 얇은 아이라인 링(선택)으로 경계 정리.

## 3. 머티리얼 (Blender 4.5 Principled 입력명 주의)
> 4.x에서 입력명 변경됨: **"Coat Weight"/"Coat Roughness"** (구 Clearcoat), **"Subsurface Weight"/"Subsurface Radius"/"Subsurface Scale"**, "Emission Color"/"Emission Strength". 존재 확인(`if name in bsdf.inputs`) 후 대입할 것.

| 파츠 | BaseColor | 파라미터 |
|---|---|---|
| Body | `#D67350` | roughness 0.40, **Coat 0.3/rough 0.12**(비닐 광), **SSS weight 0.10, radius (0.9,0.35,0.2), scale 0.3**(렌더 전용 부드러움) |
| Belly | `#E8A07E` | Body와 동일 셋업 |
| Limb(집게·발·꼬리) | `#CE6A4C` | roughness 0.45, Coat 0.25 |
| LimbDk(부챗살) | `#A9502F` | roughness 0.5, Coat 0.2 |
| EyeWhite | `#FFFFFF` | roughness 0.12, **Coat 1.0/rough 0.03** (도자기+유리) |
| Pupil | `#241F1F` | roughness 0.15, Coat 1.0/rough 0.02 (젖은 광택 — 살아있는 눈의 핵심) |
| Blush | `#FFB6C1` | roughness 0.6, emission 동색 0.15 |
| Mouth | `#8B4513` | roughness 0.5 |
| Star | `#FFD700` | metallic 0.35, roughness 0.22, **emission #FFE45A strength 3.5** |

**glTF 호환 노트**: SSS는 glTF로 안 나감(렌더 전용, Godot에선 StandardMaterial3D subsurface로 재현 예정 — 무시 가능). Coat는 `KHR_materials_clearcoat`로 익스포트됨. Emission 익스포트됨.

## 3.4 별 입체화 (v2 신규)
현재 평면 별은 측면에서 막대로 보임 → **바이피라미드 별**로:
- 4-point 별 외곽선을 위/아래 두 꼭짓점(±Y 두께 방향 아님, **±Y 정면-후면 방향으로 각 ~0.10**)으로 모아 앞뒤로 볼록한 보석형.
- 구현 자유(별 실루엣 버텍스 → 앞/뒤 정점 2개와 페이스 연결, 혹은 Solidify+양면 테이퍼). Bevel 모디파이어(0.015, 2seg)로 엣지 하이라이트.
- 몸에 살짝 박힌 채 5° 기울임. 별 아래 발광 비드 유지.

## 4. 라이팅 & 렌더 (렌더 전용 — GLB와 무관)
- **GPU 강제**: `cycles.device='GPU'`, preferences에서 `compute_device_type` OPTIX→CUDA→(실패 시 CPU) 순 시도, 모든 디바이스 enable. (사용자 GPU 여유 확인됨)
- Cycles **256 samples** + denoise, `film_transparent=True`.
- 4점 라이팅: Key(따뜻한 대형 area, 좌상전방, ~1500W/size7) · Fill(차가운 톤 우측, ~400W) · Rim(후상방, ~800W — 실루엣 분리) · Bounce(하방 약광 ~150W — 턱밑 디테일).
- **그림자 캐처 바닥**(`is_shadow_catcher=True` plane) — PNG에 부드러운 접지 그림자 포함(투명 배경 유지). GLB에는 미포함(익스포트 전 삭제 or use_selection 제어).
- World: 옅은 중성 그라데이션 0.25.
- 해상도 1200×1400 (face 클로즈업은 1200×900).
- `view_transform='Standard'`(색 순도 유지. Filmic 금지 — 파스텔 톤 죽음).

## 5. 애니메이션
- `Idle` 유지: 48f/24fps 루프, 바디 바운스 z±0.10 + 미세 z-회전 2.5°, Bezier easing. (파츠별 개별 애니는 Phase 2에서 리깅과 함께 — 이번 범위 아님)

## 6. 익스포트
- GLB: `export_apply=True`, animations, ACTIONS 모드. 그림자 캐처/라이트/카메라 제외(라이트·카메라는 기본 미포함, 캐처 plane은 명시적 제거 후 export).
- 파일 크기·씬 폴리 수를 stdout에 출력.

## 7. 검수 기준 (구현 에이전트 자체 체크 → 렌더 이미지 직접 열어 확인)
1. `claude_pet_face.png`: 동공이 흰자에 렌즈처럼 얹혀 보이고(돌출 아님/함몰 아님), 코트 광택 하이라이트가 눈에 맺힘.
2. `claude_pet_side.png`: 별이 입체(보석)로 보임. 집게 턱 2개 분리 시인. 꼬리 부채 시인.
3. `claude_pet_hero.png`: 파츠 교차선이 지저분하지 않음, 접지 그림자 자연스러움, 전체 톤이 v1보다 명백히 "피규어 렌더".
4. 콘솔: `BUILD_PET_DONE` + GLB 크기 출력, 에러/Traceback 없음.
5. 미달 시 파라미터 조정 후 재렌더 (최대 4회 반복).

## 8. 금지사항
- 산출물 파일명 변경, 캐릭터 실루엣 변경(§1), 팔레트 변경.
- 외부 에셋/텍스처 다운로드 (전부 프로시저럴).
- PowerShell 사용 (이 PC에서 deny — bash로만).
