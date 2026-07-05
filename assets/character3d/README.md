# character3d — 3D 캐릭터 에셋 (Blender 프로시저럴 파이프라인)

Godot 3D 오버레이 트랙(`docs/3D_OVERLAY_DESIGN.md` §5)에서 쓰는 캐릭터를
**코드로(프로시저럴)** 생성하는 Blender 파이프라인과 그 산출물이 들어있는 폴더입니다.
텍스처/외부 에셋 다운로드 없이 `build_pet.py` 하나로 모델·머티리얼·애니메이션·렌더까지 전부 만듭니다.

캐릭터: **둥근 블롭 + 가재 하이브리드**(사용자 확정 디자인) — 큰 촉촉한 눈, 볼터치, 미소,
벌어진 집게, 마디 꼬리 + 5장 부챗살(uropod), 머리 위 입체 별. Claude 오렌지 팔레트.

## 파일 인벤토리

| 파일 | 내용 |
|---|---|
| `SPEC_v2.md` | v2("게임 퀄리티") 구현 스펙 원본 — 지오메트리·머티리얼·라이팅·검수기준 상세 |
| `build_pet.py` | 빌드 스크립트 (Blender `--background --python` 로 실행) |
| `claude_pet.glb` | **Godot 임포트용 GLB** — 메시+머티리얼+9본 아마추어+애니메이션 5종(idle/walk/sleep/celebrate/worried) |
| `claude_pet.blend` | Blender 소스 파일 (편집용) |
| `claude_pet_hero.png` | 정면 3/4 뷰티샷 (1200×1400) |
| `claude_pet_front.png` | 정면 (1200×1400) |
| `claude_pet_side.png` | 측면 — 집게·꼬리 확인용 (1200×1400) |
| `claude_pet_back.png` | 후면 3/4 — 꼬리 부채 확인용 (1200×1400) |
| `claude_pet_face.png` | 얼굴 클로즈업 — 눈 퀄리티 검수용 (1200×900) |
| `claude_pet.blend1` | Blender 자동 백업(이전 저장본). 삭제해도 무방, 커밋 대상 아님 |
| `claude_pet_anim_*.png` | 신규 애니 포즈 렌더(검수용, `.gitignore`로 커밋 제외) |

## 재빌드 방법

Blender 4.5 LTS가 winget으로 설치되어 있어야 합니다: `C:\Program Files\Blender Foundation\Blender 4.5\blender.exe`

Git Bash에서:

```bash
export TMP="C:\temp\gradle-tmp"
export TEMP="C:\temp\gradle-tmp"
BL="C:\Program Files\Blender Foundation\Blender 4.5\blender.exe"
"$BL" --background --python build_pet.py
```

- `TMP`/`TEMP` 우회는 이 PC의 백신 훅/소켓 충돌 회피용(전역 규칙 #8과 동일 이유)이며 필수입니다.
- 이 폴더(`assets/character3d/`)에서 실행해야 상대 경로 산출물이 올바른 위치에 생성됩니다.
- 참고 소요시간: GPU OPTIX, 256 samples 기준 **RTX 3060에서 전체(모델+5개 렌더) 약 2분**.

## 파이프라인 특징 (v2 요약)

자세한 내용은 [`SPEC_v2.md`](SPEC_v2.md) 참고. 여기선 핵심만:

- **머티리얼**: Blender 4.5 Principled BSDF — Coat(비닐 광택), Subsurface(살성 렌더용), Emission(별 발광) 사용.
- **지오메트리**: 고밀도 구체 + shade smooth, 파츠 결합부 35%+ 매입으로 이음선 최소화, 별은 평면이 아닌 **입체 바이피라미드**(측면에서 막대로 안 보임).
- **렌더**: GPU Cycles(OPTIX→CUDA→CPU 순 폴백), 4점 라이팅(Key/Fill/Rim/Bounce), 그림자 캐처 바닥, `Standard` 색 공간(Filmic 아님 — 파스텔 톤 보존).
- **애니메이션**: 9본 아마추어(강체 본-파렌팅) 리깅 완료. 상태별 애니메이션 5종 — `idle`/`walk`/`sleep`/`celebrate`/`worried` — 모두 glb에 포함.

## GLB → Godot 이관 시 주의사항

| 요소 | Godot로 이관됨? |
|---|---|
| 메시/UV/버텍스컬러 | ✅ 그대로 |
애니메이션 5종(idle/walk/sleep/celebrate/worried) | ✅ `AnimationPlayer`로 재생 가능 |
| Coat(클리어코트) | ✅ `KHR_materials_clearcoat`로 익스포트됨 |
| Emission(별 발광) | ✅ `KHR_materials_emissive_strength`로 익스포트됨 |
| **Subsurface(SSS, 살성 눈/피부 렌더 부드러움)** | ❌ **렌더 전용 — glTF로 안 나감.** Godot에서 비슷한 느낌을 원하면 `StandardMaterial3D`의 subsurface 관련 파라미터로 **직접 재현**해야 함 |
| 그림자 캐처 바닥/조명/카메라 | ❌ 익스포트 전 의도적으로 제외됨(렌더용 산출물에만 사용) |

## 알려진 한계 (Phase 2 과제)

- **프리미티브 조립 메시** — 스컬핑/리토폴로지 안 된 상태(구·실린더 등 기본 도형 조합).
- **아마추어/스키닝 완료** — 9본 리그로 파츠별(팔/집게/꼬리) 애니메이션 5종 제작 완료(Phase 2).
- **UV 텍스처 없음** — 전부 절차적 머티리얼(색상+파라미터)만 사용, 텍스처 페인팅 없음.
- **눈은 지오메트리** — 셰이더/텍스처가 아니라 흰자+동공을 별도 메시로 만들어 표현(표정 변화 애니메이션 미지원).

## 관련 문서

- 캐릭터 상세 스펙: [`SPEC_v2.md`](SPEC_v2.md)
- 3D 오버레이 전체 설계(Godot 통합 맥락): [`../../docs/3D_OVERLAY_DESIGN.md`](../../docs/3D_OVERLAY_DESIGN.md)
