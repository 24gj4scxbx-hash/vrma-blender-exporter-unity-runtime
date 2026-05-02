# VRMA Tools

**Universalizing VRMA** — Extending VRMA beyond the VRM 1.0 ecosystem to VRM 0.x, FBX, and diverse rigs.

🌐 [日本語](README_ja.md) | [한국어](README_ko.md)

---

## What is this project?

VRMA (VRM Animation) is a motion format exclusive to VRM 1.0. However, the real-world VRM ecosystem is predominantly 0.x, FBX models are mixed in, and official tools only handle bone rotation.

This project consists of three independent tools:

| Tool | Role |
|------|------|
| **blender_vrma_exporter** | Export VRM/FBX/custom rigs → VRMA from Blender |
| **Unity_MotionController** | Runtime load + playback of VRMA from external storage in Unity |
| **glTF_mesh_separator** | Automatically split multi-primitive meshes in glTF/VRM into independent meshes |

---

## blender_vrma_exporter (v8.0)

A universal exporter that converts any humanoid rig to VRMA from Blender.

### Beyond the official VRM Add-on

The official VRM Add-on for Blender exports VRMA targeting bone rotation of VRM 1.0 models. blender_vrma_exporter covers areas the official add-on does not:

| Scenario | Official VRM Add-on | blender_vrma_exporter v8.0 |
|----------|--------------------|-----------------------------|
| VRM model (0.x / 1.0 version agnostic) | 1.0 only | ✅ |
| FBX model (Mixamo, etc.) → VRMA | — | ✅ ⚠️ |
| Arbitrary bone naming (custom rig) | — | ✅ Wildcard matching ⚠️ |
| Expression (Shape Key) simultaneous export | — | ✅ (bone + expression) |
| Automatic T-pose guarantee | — | ✅ |
| VRMA import → edit → re-export | — | ✅ ⚠️ |

> ⚠️ Items marked with this symbol indicate technical capability. Any legal or copyright issues arising from the use of these features are the user's responsibility and are not attributable to the tool's technical functionality. Please verify the license of your motion data and models before use.

### Key Features

- **Wildcard bone matching**: `mixamorig:Hips`, `J_Bip_C_Hips`, `Character1_Hips` — automatically detects prefixes and separators (`:`, `_`, `.`, `-`) to support various naming conventions
- **Simultaneous expression export**: Automatically detects Shape Key value changes and includes them in the VRMA alongside bone rotation. Auto-classifies VRM preset/custom
- **Automatic T-pose guarantee**: Clears transforms and inserts keyframe at frame 0 on execution
- **Static non-zero expression recognition**: Recognizes non-zero values as active even if they don't change (e.g., fixed expressions)
- **Log output**: Automatically generates `.log.txt` alongside `.vrma`. Records rig type, matched bones, active Shape Keys, etc.

### VRMA Editing Workflow

You can edit existing VRMA files in Blender and re-export them:

1. Import VRMA with the official VRM Add-on (File → Import → VRM Animation)
2. Review the motion applied to the model in Blender
3. Modify poses / add expressions / adjust timing
4. Re-export with blender_vrma_exporter

### Usage

1. Open a model in Blender (VRM, FBX, or native Blender model)
2. Create motion + expression keyframes
3. Edit `OUTPUT_DIR` at the top of the file to your desired output path
4. Scripting tab → paste script → ▶ Run
5. `.vrma` + `.log.txt` generated in the output folder

### Requirements

- Blender 4.x or later
- No external dependencies (uses only Blender's built-in Python)

---

## Unity_MotionController

A Unity component that loads and plays VRMA files at runtime from external storage. Zero motion assets in build — just specify a path and any VRMA is instantly loaded and played.

### Custom Engines

UniVRM can play VRMA bone animation, but runtime control of expressions is difficult. MotionController solves this with two custom engines:

- **World Retarget**: Calculates bone animation as WORLD rotation deltas, retargeting to any VRM model
- **Direct Binary Parser**: Directly parses GLB binary from VRMA files to extract expression keyframes, interpolates over time, and applies via SetBlendShapeWeight

### Key Features

- **Simultaneous bone + expression playback**: Applies bone rotation and expression weights from VRMA simultaneously
- **VRM version agnostic**: Expressions work on VRM 0.x models. Direct matching by BlendShape name, independent of VRM spec version
- **Both preset and custom supported**: Handles VRMA preset expressions and custom expressions identically

### Architecture

```
MotionController
  LateUpdate:
    1. Bone: curr × Inv(rest) → WORLD delta → Inv(parentDelta) × delta → local rotation
    2. Expression: binary search + Lerp → SetBlendShapeWeight(index, weight × 100)
```

### Requirements

- Unity 2021.3 or later
- UniVRM (for loading VRM models)

---

## glTF_mesh_separator

A tool that automatically splits multi-primitive meshes in glTF/VRM files into independent meshes.

### Why is this needed?

Tools like VRoid Studio sometimes combine parts with different materials into a single mesh as sub-meshes. Depending on the environment, this structure can cause:

- GPU depth sorting issues depending on shader (z-fighting)
- Unable to toggle sub-meshes on/off individually → difficult costume/prop toggling
- BlendShapes applied to all sub-meshes unnecessarily → wasted computation

### Key Features

- **Automatic splitting**: 1 mesh with N sub-meshes → N independent meshes
- **Full BlendShape preservation**: Vertex index remap + BlendShape remapping
- **BoneWeight/UV/Normal preservation**: All vertex attributes maintained
- **VRM BlendShapeGroup reference update**: Automatically expanded to all split meshes
- **Analysis mode**: `--analyze` option to preview split targets without splitting

### Usage

**Drag and drop:**
Drag a `.vrm` or `.glb` file onto `glTF_mesh_separator.bat`

**Command line:**
```bash
python glTF_mesh_separator.py input.vrm                    # Auto output name
python glTF_mesh_separator.py input.vrm output.vrm         # Specify output name
python glTF_mesh_separator.py input.vrm --analyze          # Analysis only (no split)
```

### Requirements

- Python 3.x
- No external dependencies (standard library only)

---

## Verification Status

| Test | Result |
|------|--------|
| blender_vrma_exporter: VRM model → VRMA | ✅ 54/54 bones, expression OK |
| blender_vrma_exporter: FBX model → VRMA | ✅ 54/54 bones, expression OK |
| blender_vrma_exporter: VRMA import → edit → re-export | ✅ Motion + expression preserved |
| Unity_MotionController: VRM 0.x + VRMA expression | ✅ preset + custom working |
| Unity_MotionController: bone + expression simultaneous | ✅ |
| glTF_mesh_separator: 4 VRM batch split | ✅ 4/4 success, BlendShape preserved |

---

## Disclaimer

These tools provide technical functionality. The user is responsible for any legal or copyright issues arising from their use. Please verify the license and terms of use for motion data, 3D models, and other assets before use.

---

## License

AGPL-3.0

If you modify and distribute these tools, you are obligated to disclose the source code.

---

## Contributing

Bug reports, feature suggestions, and Pull Requests are welcome.

- Extending blender_vrma_exporter as a Blender add-on
- Supporting additional rig naming conventions
- Runtime players for other engines (Unreal, Godot)
