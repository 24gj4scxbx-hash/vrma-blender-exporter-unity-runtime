# VRMA Tools

**VRMA의 범용화** — VRM 1.0 생태계에 한정되어 있던 VRMA를 VRM 0.x, FBX, 다양한 rig로 확장하는 도구 모음.

🌐 [English](README.md) | [日本語](README_ja.md)

---

## 이 프로젝트는 무엇인가

VRMA(VRM Animation)는 VRM 1.0 전용 모션 포맷입니다. 그러나 현실의 VRM 생태계는 대부분 0.x이고, FBX 모델도 혼재하며, 공식 도구는 bone rotation만 다룹니다.

이 프로젝트는 세 가지 독립 도구로 구성되어 있습니다:

| 도구 | 역할 |
|------|------|
| **blender_vrma_exporter** | Blender에서 VRM/FBX/커스텀 rig → VRMA 익스포트 |
| **Unity_MotionController** | Unity에서 VRMA를 외부 저장소로부터 런타임 로드 + 재생 |
| **glTF_mesh_separator** | glTF/VRM의 다중 서브메쉬를 독립 메쉬로 자동 분리 |

---

## blender_vrma_exporter (v8.0)

Blender에서 다양한 humanoid rig를 VRMA로 내보내는 범용 익스포터.

### 공식 VRM 애드온이 커버하지 않는 영역

공식 VRM Add-on for Blender의 VRMA 익스포트는 VRM 1.0 모델의 bone rotation을 대상으로 동작합니다. blender_vrma_exporter는 공식이 다루지 않는 영역을 커버합니다:

| 시나리오 | 공식 VRM 애드온 | blender_vrma_exporter v8.0 |
|---------|---------------|---------------------------|
| VRM 모델 (0.x / 1.0 버전 무관) | 1.0만 | ✅ |
| FBX 모델 (Mixamo 등)에서 모션 제작 → VRMA | — | ✅ ⚠️ |
| 임의의 bone 네이밍 규칙 (커스텀 rig) | — | ✅ 와일드카드 매칭 ⚠️ |
| Expression (Shape Key) 동시 익스포트 | — | ✅ (bone + expression) |
| T-pose 자동 보장 | — | ✅ |
| 기존 VRMA 임포트 → 편집 → 재익스포트 | — | ✅ ⚠️ |

> ⚠️ 표시 항목은 기술적으로 가능함을 명시하는 것입니다. 이 기능을 사용하여 발생하는 법적/저작권 문제는 사용자의 책임이며, 본 도구의 기술적 기능에 기인한 문제가 아닙니다. 모션 데이터나 모델의 라이선스를 반드시 확인한 후 사용하시기 바랍니다.

### 핵심 기능

- **와일드카드 bone 매칭**: `mixamorig:Hips`, `J_Bip_C_Hips`, `Character1_Hips` — 접두사와 구분자(`:`, `_`, `.`, `-`)를 자동 감지하여 다양한 네이밍 규칙에 대응
- **Expression 동시 익스포트**: Shape Key 값 변화를 자동 감지하여 bone rotation과 함께 VRMA에 포함. VRM preset/custom 자동 분류
- **T-pose 자동 보장**: 실행 시 frame 0에서 Clear Transform + 키프레임 자동 삽입
- **고정 비제로 표정 인식**: 값이 변하지 않더라도 비제로 값이 있으면 활성으로 인식 (예: 고정 표정)
- **로그 출력**: `.vrma` 옆에 `.log.txt` 자동 생성. Rig 유형, 매칭된 bone, 활성 Shape Key 등을 기록

### VRMA 편집 워크플로우

기존 VRMA 파일을 Blender에서 편집하고 다시 내보낼 수 있습니다:

1. 공식 VRM 애드온으로 VRMA 임포트 (File → Import → VRM Animation)
2. 모델에 적용된 모션을 Blender에서 확인
3. 포즈 수정 / 표정 추가 / 타이밍 조절
4. blender_vrma_exporter로 재익스포트

### 사용법

1. Blender에서 모델 열기 (VRM, FBX, 또는 Blender 자체 모델)
2. 모션 + 표정 키프레임 작업
3. 파일 상단의 `OUTPUT_DIR`을 원하는 출력 경로로 수정
4. Scripting 탭 → 스크립트 붙여넣기 → ▶ 실행
5. 출력 폴더에 `.vrma` + `.log.txt` 생성

### 요구사항

- Blender 4.x 이상
- 외부 의존성 없음 (Blender 내장 Python만 사용)

---

## Unity_MotionController

VRMA 파일을 외부 저장소에서 런타임 로드하여 재생하는 Unity 컴포넌트. 빌드 내 모션 자산 0 — 경로만 지정하면 어떤 VRMA든 즉시 로드하여 재생합니다.

### 자체 엔진

UniVRM은 VRMA의 bone animation을 재생할 수 있지만, expression(표정)은 런타임에서 직접 제어가 어렵습니다. MotionController는 두 가지 자체 엔진으로 이를 해결합니다:

- **World Retarget**: bone animation을 WORLD rotation delta로 계산하여 어떤 VRM 모델에든 리타겟팅
- **Direct Binary Parser**: VRMA 파일의 GLB 바이너리를 직접 파싱하여 expression 키프레임을 추출하고, 시간 보간 후 SetBlendShapeWeight로 적용

### 핵심 기능

- **Bone + Expression 동시 재생**: VRMA에 포함된 bone rotation과 expression weight를 동시에 적용
- **VRM 버전 무관**: VRM 0.x 모델에서도 expression이 동작. BlendShape 이름 기반 직접 매칭으로 VRM 스펙 버전에 의존하지 않음
- **preset/custom 모두 지원**: VRMA의 preset expression과 custom expression을 동일하게 처리

### 아키텍처

```
MotionController
  LateUpdate:
    1. Bone: curr × Inv(rest) → WORLD delta → Inv(parentDelta) × delta → local rotation
    2. Expression: binary search + Lerp → SetBlendShapeWeight(index, weight × 100)
```

### 요구사항

- Unity 2021.3 이상
- UniVRM (VRM 모델 로드용)

---

## glTF_mesh_separator

glTF/VRM 파일의 다중 서브메쉬(multi-primitive mesh)를 독립 메쉬로 자동 분리하는 도구.

### 왜 필요한가

VRoid Studio 등의 도구는 머티리얼이 다른 파츠를 하나의 메쉬에 서브메쉬로 합치는 경우가 있습니다. 이 구조는 환경에 따라 다음과 같은 문제를 일으킬 수 있습니다:

- 셰이더에 따라 GPU 깊이 정렬 문제 발생 (z-fighting)
- 서브메쉬 단위 on/off 불가 → 의상/소품 개별 토글 어려움
- BlendShape가 불필요한 서브메쉬에도 전부 적용 → 불필요한 연산

### 핵심 기능

- **자동 분리**: 1메쉬 N서브메쉬 → N개 독립 메쉬
- **BlendShape 완전 보존**: vertex index remap + BlendShape 재매핑
- **BoneWeight/UV/Normal 보존**: 모든 vertex attribute 유지
- **VRM BlendShapeGroup 참조 갱신**: 분리된 메쉬 전부로 자동 확장
- **분석 모드**: `--analyze` 옵션으로 분리 대상만 미리 확인

### 사용법

**드래그앤드롭:**
`.vrm` 또는 `.glb` 파일을 `glTF_mesh_separator.bat` 위에 끌어다 놓기

**커맨드라인:**
```bash
python glTF_mesh_separator.py input.vrm                    # 자동 출력명
python glTF_mesh_separator.py input.vrm output.vrm         # 출력명 지정
python glTF_mesh_separator.py input.vrm --analyze          # 분석만 (분리 안 함)
```

### 요구사항

- Python 3.x
- 외부 의존성 없음 (표준 라이브러리만 사용)

---

## 검증 현황

| 테스트 | 결과 |
|--------|------|
| blender_vrma_exporter: VRM 모델 → VRMA | ✅ 54/54 bone, expression 정상 |
| blender_vrma_exporter: FBX 모델 → VRMA | ✅ 54/54 bone, expression 정상 |
| blender_vrma_exporter: VRMA 임포트 → 편집 → 재익스포트 | ✅ 모션 + 표정 보존 |
| Unity_MotionController: VRM 0.x + VRMA expression | ✅ preset + custom 동작 |
| Unity_MotionController: bone + expression 동시 재생 | ✅ |
| glTF_mesh_separator: VRM 4종 배치 분리 | ✅ 4/4 성공, BlendShape 보존 |

---

## 면책 조항

이 도구들은 기술적 기능을 제공하는 것이며, 사용으로 인해 발생하는 법적, 저작권적 문제에 대한 책임은 사용자에게 있습니다. 모션 데이터, 3D 모델, 기타 자산의 라이선스 및 이용 조건을 반드시 확인한 후 사용하시기 바랍니다.

---

## 라이선스

AGPL-3.0

이 도구들을 수정하여 배포하는 경우, 소스 코드 공개 의무가 있습니다.

---

## 기여

버그 리포트, 기능 제안, Pull Request를 환영합니다.

- blender_vrma_exporter를 Blender 애드온으로 확장
- 추가 rig 네이밍 규칙 지원
- 다른 엔진 (Unreal, Godot) 용 런타임 플레이어
