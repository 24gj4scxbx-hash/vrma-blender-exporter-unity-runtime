"""
Blender -> VRMA Exporter v8.0
=============================
A Blender Python script that exports the active armature's pose
animation and shape-key animation as a VRMA (.vrma) file conforming
to the VRMC_vrm_animation 1.0 specification.

Pipeline highlights (kept as a short version-history note):
    v6.2: identity rest + pre-computed WORLD delta
    v7.1: read shape keys directly (works around Blender fcurve quirks)
    v7.2: shape key -> VRM preset expression mapping
    v7.3: per-shape-key mesh auto-pick (resolves duplicated keys
          across face/overlay meshes by largest value variation)
    v7.4: expression nodes are also added to scene.nodes so
          downstream importers (e.g. UniVRM) instantiate them
    v8.0: universal humanoid rig matcher (suffix-based wildcard
          matching across VRoid / VRM / Mixamo / custom FBX rigs)

How to use:
    1) Open Blender, load your rig + animation.
    2) Make sure frame Start = 0 holds a clean T-pose
       (this script will auto-clear pose transforms on frame_start
        and insert a whole-character keyframe to be safe).
    3) Open the Scripting tab, paste this file, press Run.
    4) The .vrma is written to OUTPUT_DIR (see below) along with
       a .log.txt next to it.

License: AGPL-3.0-or-later
"""
import bpy
import json
import struct
import os
from mathutils import Quaternion

# Change this to your preferred output folder.
# The script will create the directory if it does not exist.
OUTPUT_DIR = r"C:\vrma_output"
FPS = 24

# Quaternion that converts Blender's coordinate system to glTF's.
CONV = Quaternion((0.7071068, -0.7071068, 0, 0))
CONV_INV = CONV.inverted()

# ============================================================
# Humanoid bone suffix -> VRMA camelCase name
# Key   : the suffix matched at the END of the actual bone name
# Value : the canonical VRMA humanoid bone name
# ============================================================
BONE_SUFFIXES = {
    'Hips': 'hips', 'Spine': 'spine', 'Chest': 'chest', 'UpperChest': 'upperChest',
    'Neck': 'neck', 'Head': 'head',
    'FaceEye_L': 'leftEye', 'FaceEye_R': 'rightEye',
    'Shoulder_L': 'leftShoulder', 'UpperArm_L': 'leftUpperArm',
    'LowerArm_L': 'leftLowerArm', 'Hand_L': 'leftHand',
    'Shoulder_R': 'rightShoulder', 'UpperArm_R': 'rightUpperArm',
    'LowerArm_R': 'rightLowerArm', 'Hand_R': 'rightHand',
    'Thumb1_L': 'leftThumbMetacarpal', 'Thumb2_L': 'leftThumbProximal', 'Thumb3_L': 'leftThumbDistal',
    'Index1_L': 'leftIndexProximal', 'Index2_L': 'leftIndexIntermediate', 'Index3_L': 'leftIndexDistal',
    'Middle1_L': 'leftMiddleProximal', 'Middle2_L': 'leftMiddleIntermediate', 'Middle3_L': 'leftMiddleDistal',
    'Ring1_L': 'leftRingProximal', 'Ring2_L': 'leftRingIntermediate', 'Ring3_L': 'leftRingDistal',
    'Little1_L': 'leftLittleProximal', 'Little2_L': 'leftLittleIntermediate', 'Little3_L': 'leftLittleDistal',
    'Thumb1_R': 'rightThumbMetacarpal', 'Thumb2_R': 'rightThumbProximal', 'Thumb3_R': 'rightThumbDistal',
    'Index1_R': 'rightIndexProximal', 'Index2_R': 'rightIndexIntermediate', 'Index3_R': 'rightIndexDistal',
    'Middle1_R': 'rightMiddleProximal', 'Middle2_R': 'rightMiddleIntermediate', 'Middle3_R': 'rightMiddleDistal',
    'Ring1_R': 'rightRingProximal', 'Ring2_R': 'rightRingIntermediate', 'Ring3_R': 'rightRingDistal',
    'Little1_R': 'rightLittleProximal', 'Little2_R': 'rightLittleIntermediate', 'Little3_R': 'rightLittleDistal',
    'UpperLeg_L': 'leftUpperLeg', 'LowerLeg_L': 'leftLowerLeg',
    'Foot_L': 'leftFoot', 'ToeBase_L': 'leftToes',
    'UpperLeg_R': 'rightUpperLeg', 'LowerLeg_R': 'rightLowerLeg',
    'Foot_R': 'rightFoot', 'ToeBase_R': 'rightToes',
}

# Eye-bone alternative suffixes (covers VRoid cartoon-style rigs and others).
EYE_ALTS = {
    'FaceEye_L': ['Eye_L', 'eye.L', 'LeftEye'],
    'FaceEye_R': ['Eye_R', 'eye.R', 'RightEye'],
}

# Characters that are accepted as a separator before a suffix
# (e.g. 'mixamorig:Hips', 'J_Bip_C_Hips', 'spine.Hips' all match 'Hips').
SEPARATORS = {':', '_', '.', '-'}

BONE_PARENT = {
    'hips': None,
    'spine': 'hips', 'chest': 'spine', 'upperChest': 'chest',
    'neck': 'upperChest', 'head': 'neck',
    'leftEye': 'head', 'rightEye': 'head',
    'leftShoulder': 'upperChest', 'leftUpperArm': 'leftShoulder',
    'leftLowerArm': 'leftUpperArm', 'leftHand': 'leftLowerArm',
    'rightShoulder': 'upperChest', 'rightUpperArm': 'rightShoulder',
    'rightLowerArm': 'rightUpperArm', 'rightHand': 'rightLowerArm',
    'leftThumbMetacarpal': 'leftHand', 'leftThumbProximal': 'leftThumbMetacarpal', 'leftThumbDistal': 'leftThumbProximal',
    'leftIndexProximal': 'leftHand', 'leftIndexIntermediate': 'leftIndexProximal', 'leftIndexDistal': 'leftIndexIntermediate',
    'leftMiddleProximal': 'leftHand', 'leftMiddleIntermediate': 'leftMiddleProximal', 'leftMiddleDistal': 'leftMiddleIntermediate',
    'leftRingProximal': 'leftHand', 'leftRingIntermediate': 'leftRingProximal', 'leftRingDistal': 'leftRingIntermediate',
    'leftLittleProximal': 'leftHand', 'leftLittleIntermediate': 'leftLittleProximal', 'leftLittleDistal': 'leftLittleIntermediate',
    'rightThumbMetacarpal': 'rightHand', 'rightThumbProximal': 'rightThumbMetacarpal', 'rightThumbDistal': 'rightThumbProximal',
    'rightIndexProximal': 'rightHand', 'rightIndexIntermediate': 'rightIndexProximal', 'rightIndexDistal': 'rightIndexIntermediate',
    'rightMiddleProximal': 'rightHand', 'rightMiddleIntermediate': 'rightMiddleProximal', 'rightMiddleDistal': 'rightMiddleIntermediate',
    'rightRingProximal': 'rightHand', 'rightRingIntermediate': 'rightRingProximal', 'rightRingDistal': 'rightRingIntermediate',
    'rightLittleProximal': 'rightHand', 'rightLittleIntermediate': 'rightLittleProximal', 'rightLittleDistal': 'rightLittleIntermediate',
    'leftUpperLeg': 'hips', 'leftLowerLeg': 'leftUpperLeg', 'leftFoot': 'leftLowerLeg', 'leftToes': 'leftFoot',
    'rightUpperLeg': 'hips', 'rightLowerLeg': 'rightUpperLeg', 'rightFoot': 'rightLowerLeg', 'rightToes': 'rightFoot',
}

# Keys that toggle costumes/props — varies by model. Add your own if needed.
# ★ The entries below are EXAMPLES ONLY, taken from one specific model. Every
#   VRM uses its own naming convention for costume/prop shape keys, so you
#   MUST replace this set with the names from YOUR model. These keys are
#   deliberately skipped so a pose-driven costume change does not get baked
#   into the exported VRMA.
DANGEROUS_KEYS = {'Swimsuit', 'ShowGuitar', 'ShowStandMic', 'ShowHandMic',
                  'SwimSuit', 'swimsuit', 'showGuitar', 'showStandMic', 'showHandMic'}

# Shape Key name -> VRM preset expression name.
# Anything not listed here is exported as a custom expression with its
# original name preserved.
SK_TO_PRESET = {
    'eyeBlinkLeft': 'blinkLeft',
    'eyeBlinkRight': 'blinkRight',
    'Fcl_EYE_Joy1': 'happy', 'Fcl_ALL_Joy': 'happy',
    'Fcl_EYE_Angry': 'angry', 'Fcl_ALL_Angry': 'angry',
    'Fcl_EYE_Sorrow': 'sad', 'Fcl_ALL_Sorrow': 'sad',
    'Fcl_EYE_Surprised': 'surprised', 'Fcl_ALL_Surprised': 'surprised',
    'Fcl_MTH_A': 'aa', 'Fcl_MTH_I': 'ih', 'Fcl_MTH_U': 'ou',
    'Fcl_MTH_E': 'ee', 'Fcl_MTH_O': 'oh',
    'jawOpen': 'aa', 'mouthFunnel': 'ou',
}


# ============================================================
# v8.0 core: wildcard bone matching
# ============================================================
def match_suffix(bone_name, suffix):
    """Return True if `bone_name` ends with `suffix` on a separator boundary.
    Examples:
        'mixamorig:Hips' -> 'Hips'  matches
        'J_Bip_C_Hips'   -> 'Hips'  matches
        'Hips'           -> 'Hips'  matches
        'rightHand'      -> 'Hand'  does NOT match (no separator before 'Hand')
    """
    if bone_name == suffix:
        return True
    if bone_name.endswith(suffix):
        prefix_end = bone_name[len(bone_name) - len(suffix) - 1]
        return prefix_end in SEPARATORS
    return False


def find_bone_in_armature(arm_obj, suffix, alts=None):
    """Find the first pose bone whose name matches `suffix` (or any `alts`).
    `alts` is an optional list of fallback suffixes (e.g. eye-bone aliases).
    Returns the actual bone name, or None if no match exists.
    """
    # First pass: exact suffix match.
    for pb in arm_obj.pose.bones:
        if match_suffix(pb.name, suffix):
            return pb.name
    # Second pass: fallback suffixes (e.g. eye bones on cartoon-style rigs).
    if alts:
        for alt in alts:
            for pb in arm_obj.pose.bones:
                if match_suffix(pb.name, alt):
                    return pb.name
    return None


_log_lines = []

def log(msg=""):
    """Print to console AND record into a log buffer (written to .log.txt later)."""
    print(msg)
    _log_lines.append(msg)


def detect_rig_type(found_map):
    """Best-effort guess at the rig family based on the matched bone names."""
    if not found_map:
        return "Unknown"
    sample = list(found_map.values())[0]  # An actual bone name from the rig.
    if ':' in sample:
        prefix = sample.split(':')[0]
        return f"Prefix-based ({prefix})"
    if sample.startswith('J_Bip_') or sample.startswith('J_Adj_'):
        return "VRoid/VRM"
    # If most matches are exact (no prefix), it's likely a clean VRoid/VRM rig.
    suffixes = list(BONE_SUFFIXES.keys())
    exact_count = sum(1 for bn in found_map.values() if bn in suffixes)
    if exact_count > len(found_map) * 0.8:
        return "VRoid/VRM (exact)"
    return "Custom FBX"


def to_gltf(q):
    """Rotate a Blender quaternion into glTF's coordinate system."""
    return CONV @ q @ CONV_INV


def main():
    scene = bpy.context.scene

    arm_obj = None
    for obj in bpy.data.objects:
        if obj.type == 'ARMATURE':
            arm_obj = obj
            break

    if not arm_obj:
        log("ERROR: No armature found!")
        return

    # v8.0: enforce a clean T-pose at frame_start by clearing all pose
    # transforms and inserting a whole-character keyframe there.
    scene.frame_set(scene.frame_start)
    bpy.context.view_layer.objects.active = arm_obj
    bpy.ops.object.mode_set(mode='POSE')
    bpy.ops.pose.select_all(action='SELECT')
    bpy.ops.pose.transforms_clear()  # equivalent to Alt+G, Alt+R, Alt+S together
    bpy.ops.anim.keyframe_insert_by_name(type='WholeCharacter')
    bpy.ops.object.mode_set(mode='OBJECT')
    log(f"[VRMA v8.0] T-pose auto-set at frame {scene.frame_start}")

    # v8.0: wildcard-match each humanoid suffix to a real bone in the armature.
    # found_map: {camel_case_humanoid_name: actual_bone_name}
    found_map = {}
    for suffix, camel_name in BONE_SUFFIXES.items():
        if camel_name in found_map:
            continue  # Skip if this humanoid slot is already filled (e.g. avoid double-binding leftEye).
        alts = EYE_ALTS.get(suffix)
        actual = find_bone_in_armature(arm_obj, suffix, alts)
        if actual:
            found_map[camel_name] = actual

    rig_type = detect_rig_type(found_map)
    found_bones = [(found_map[cm], cm) for cm in found_map]
    found_camels = set(found_map.keys())

    log(f"[VRMA v8.0] Rig type: {rig_type}")
    log(f"[VRMA v8.0] Found {len(found_bones)}/{len(BONE_SUFFIXES)} humanoid bones")

    # Report any humanoid slots that could not be filled.
    missing = [suffix for suffix, cm in BONE_SUFFIXES.items() if cm not in found_map]
    if missing:
        log(f"[VRMA v8.0] Missing bones ({len(missing)}): {', '.join(missing[:10])}{'...' if len(missing) > 10 else ''}")

    # Report bones whose actual name had to be remapped (i.e. carried a prefix).
    remapped = [(actual, cm) for cm, actual in found_map.items() if actual != cm and actual not in BONE_SUFFIXES]
    if remapped:
        log(f"[VRMA v8.0] Remapped bones:")
        for actual, cm in remapped[:5]:
            log(f"  {actual} -> {cm}")
        if len(remapped) > 5:
            log(f"  ... and {len(remapped) - 5} more")

    frame_start = scene.frame_start
    frame_end = scene.frame_end
    anim_start = frame_start + 1
    num_anim_frames = frame_end - anim_start + 1

    if num_anim_frames < 1:
        log("ERROR: Need at least 2 frames")
        return

    log(f"[VRMA v8.0] Animation: {anim_start}~{frame_end} ({num_anim_frames} frames)")

    # ============================================================
    # Phase 1: Collect every (shape_key, mesh) pair so we can later
    # auto-pick the best mesh for each shape key based on actual
    # value variation across the animation.
    # ============================================================
    sk_all_meshes = {}
    for obj in bpy.data.objects:
        if obj.type == 'MESH' and obj.data.shape_keys:
            for kb in obj.data.shape_keys.key_blocks:
                if kb.name == 'Basis' or kb.name in DANGEROUS_KEYS:
                    continue
                if kb.name not in sk_all_meshes:
                    sk_all_meshes[kb.name] = []
                sk_all_meshes[kb.name].append(obj)

    all_sk_names = set(sk_all_meshes.keys())
    log(f"[VRMA v8.0] Total Shape Keys: {len(all_sk_names)}")

    # ============================================================
    # Phase 2: Capture the T-pose WORLD-space rotation for every
    # matched bone. This is the "rest" we measure deltas against.
    # ============================================================
    scene.frame_set(frame_start)
    bpy.context.view_layer.update()

    camel_list = [cm for _, cm in found_bones]
    node_idx = {cm: i for i, cm in enumerate(camel_list)}

    world_rest = {}
    for actual_name, camel_name in found_bones:
        pb = arm_obj.pose.bones[actual_name]
        world_rest[camel_name] = to_gltf(pb.matrix.to_quaternion())

    # ============================================================
    # Phase 3: Walk every animation frame and capture both bone
    # rotations (as world deltas, then converted to local) and
    # shape-key values per (shape_key, mesh).
    # ============================================================
    bone_keyframes = {cm: [] for cm in camel_list}
    sk_per_mesh = {}
    neg_count = 0

    for frame in range(anim_start, frame_end + 1):
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        time = (frame - anim_start) / FPS

        # Bone rotations: capture WORLD, then convert to a parent-relative local delta.
        world_curr = {}
        for actual_name, camel_name in found_bones:
            pb = arm_obj.pose.bones[actual_name]
            world_curr[camel_name] = to_gltf(pb.matrix.to_quaternion())

        world_deltas = {}
        for actual_name, camel_name in found_bones:
            w_delta = world_curr[camel_name] @ world_rest[camel_name].inverted()
            world_deltas[camel_name] = w_delta
            parent_camel = BONE_PARENT.get(camel_name)
            parent_delta = world_deltas.get(parent_camel, Quaternion((1, 0, 0, 0)))
            target_local = parent_delta.inverted() @ w_delta
            target_local.normalize()
            if target_local.w < 0:
                target_local.negate()
                neg_count += 1
            bone_keyframes[camel_name].append((time, [target_local.x, target_local.y, target_local.z, target_local.w]))

        # Shape keys: read from every candidate mesh; we'll pick the best one per key in Phase 4.
        for sk_name, mesh_list in sk_all_meshes.items():
            for obj in mesh_list:
                kb = obj.data.shape_keys.key_blocks.get(sk_name)
                if kb:
                    key = (sk_name, obj.name)
                    if key not in sk_per_mesh:
                        sk_per_mesh[key] = []
                    sk_per_mesh[key].append((time, kb.value))

    # ============================================================
    # Phase 4: For each shape key, pick the mesh whose values
    # actually animate (largest range, then largest absolute peak).
    # This avoids exporting flat zero-valued duplicates from
    # secondary meshes that share the same key name.
    # ============================================================
    sk_active = {}
    for sk_name in all_sk_names:
        best_values = None
        best_range = 0.0
        best_max_val = 0.0
        for obj in sk_all_meshes[sk_name]:
            key = (sk_name, obj.name)
            if key not in sk_per_mesh:
                continue
            values = sk_per_mesh[key]
            vals = [v for _, v in values]
            val_range = max(vals) - min(vals)
            max_val = max(abs(v) for v in vals)
            # Prefer the mesh with the larger value range; break ties on absolute peak.
            if val_range > best_range or (val_range == best_range and max_val > best_max_val):
                best_range = val_range
                best_max_val = max_val
                best_values = values
        # v8.0: keep the key if it varies OR if it stays at a non-zero hold (a fixed expression pose).
        if best_values and (best_range > 0.001 or best_max_val > 0.001):
            sk_active[sk_name] = best_values

    # Classify each surviving shape key as a VRM preset or a custom expression.
    preset_expressions = {}
    custom_expressions = {}
    sk_export_list = []

    used_presets = set()
    for sk_name in sorted(sk_active.keys()):
        preset_name = SK_TO_PRESET.get(sk_name)
        if preset_name and preset_name not in used_presets:
            sk_export_list.append((preset_name, sk_name, sk_active[sk_name], 'preset'))
            used_presets.add(preset_name)
        else:
            sk_export_list.append((sk_name, sk_name, sk_active[sk_name], 'custom'))

    log(f"[VRMA v8.0] Active Shape Keys: {len(sk_active)}")
    for display, original, values, category in sk_export_list:
        vals = [v for _, v in values]
        preset_tag = f" -> preset:{display}" if category == 'preset' else " -> custom"
        log(f"  -> {original} (min={min(vals):.3f}, max={max(vals):.3f}){preset_tag}")

    log(f"[VRMA v8.0] Bones: {num_anim_frames * len(found_bones)} ({neg_count} W-negated)")

    # ============================================================
    # Phase 5: Build the glTF JSON + binary buffer for the .vrma.
    # ============================================================

    # Bone nodes
    nodes = []
    human_bones = {}
    for i, camel_name in enumerate(camel_list):
        children = [node_idx[cm] for cm in camel_list
                    if BONE_PARENT.get(cm) == camel_name and cm in node_idx]
        node = {"name": camel_name, "rotation": [0.0, 0.0, 0.0, 1.0]}
        if children:
            node["children"] = children
        nodes.append(node)
        human_bones[camel_name] = {"node": i}

    root_nodes = [node_idx[cm] for cm in camel_list
                  if BONE_PARENT.get(cm) is None or BONE_PARENT.get(cm) not in found_camels]

    # Expression nodes
    sk_node_indices = {}
    for display, original, values, category in sk_export_list:
        idx = len(nodes)
        nodes.append({"name": display, "translation": [0.0, 0.0, 0.0]})
        sk_node_indices[original] = idx
        if category == 'preset':
            preset_expressions[display] = {"node": idx}
        else:
            custom_expressions[display] = {"node": idx}

    # v7.4: include expression nodes in the scene as well.
    root_nodes += list(sk_node_indices.values())

    # Binary buffer
    bin_data = bytearray()
    accessors = []
    buffer_views = []
    channels = []
    samplers = []

    # Bone rotation channels
    for cm in camel_list:
        kf = bone_keyframes[cm]
        t_off = len(bin_data)
        for t, q in kf:
            bin_data += struct.pack('<f', t)
        buffer_views.append({"buffer": 0, "byteOffset": t_off, "byteLength": num_anim_frames * 4})
        t_acc = len(accessors)
        accessors.append({"bufferView": len(buffer_views)-1, "componentType": 5126, "count": num_anim_frames, "type": "SCALAR", "min": [kf[0][0]], "max": [kf[-1][0]]})

        r_off = len(bin_data)
        for t, q in kf:
            bin_data += struct.pack('<ffff', *q)
        buffer_views.append({"buffer": 0, "byteOffset": r_off, "byteLength": num_anim_frames * 16})
        r_acc = len(accessors)
        accessors.append({"bufferView": len(buffer_views)-1, "componentType": 5126, "count": num_anim_frames, "type": "VEC4"})

        s_idx = len(samplers)
        samplers.append({"input": t_acc, "output": r_acc, "interpolation": "LINEAR"})
        channels.append({"sampler": s_idx, "target": {"node": node_idx[cm], "path": "rotation"}})

    # Expression translation channels (weight is encoded in translation.x).
    for display, original, values, category in sk_export_list:
        node_i = sk_node_indices[original]

        t_off = len(bin_data)
        for t, v in values:
            bin_data += struct.pack('<f', t)
        buffer_views.append({"buffer": 0, "byteOffset": t_off, "byteLength": num_anim_frames * 4})
        t_acc = len(accessors)
        accessors.append({"bufferView": len(buffer_views)-1, "componentType": 5126, "count": num_anim_frames, "type": "SCALAR", "min": [values[0][0]], "max": [values[-1][0]]})

        tr_off = len(bin_data)
        for t, v in values:
            bin_data += struct.pack('<fff', v, 0.0, 0.0)
        buffer_views.append({"buffer": 0, "byteOffset": tr_off, "byteLength": num_anim_frames * 12})
        tr_acc = len(accessors)
        accessors.append({"bufferView": len(buffer_views)-1, "componentType": 5126, "count": num_anim_frames, "type": "VEC3"})

        s_idx = len(samplers)
        samplers.append({"input": t_acc, "output": tr_acc, "interpolation": "LINEAR"})
        channels.append({"sampler": s_idx, "target": {"node": node_i, "path": "translation"}})

    # glTF JSON
    vrm_anim = {"specVersion": "1.0", "humanoid": {"humanBones": human_bones}}
    if preset_expressions or custom_expressions:
        expr = {}
        if preset_expressions:
            expr["preset"] = preset_expressions
        if custom_expressions:
            expr["custom"] = custom_expressions
        vrm_anim["expressions"] = expr

    gltf = {
        "asset": {"version": "2.0", "generator": "VRMA-Exporter-v8.0"},
        "extensionsUsed": ["VRMC_vrm_animation"],
        "extensions": {"VRMC_vrm_animation": vrm_anim},
        "scene": 0, "scenes": [{"nodes": root_nodes}],
        "nodes": nodes, "buffers": [{"byteLength": len(bin_data)}],
        "bufferViews": buffer_views, "accessors": accessors,
        "animations": [{"name": "Animation", "channels": channels, "samplers": samplers}]
    }

    # Validation: refuse to write if any NaN slipped into the rotation data.
    nan_count = sum(1 for c in camel_list for _, q in bone_keyframes[c] if any(v != v for v in q))
    if nan_count:
        log(f"[ERROR] {nan_count} NaN!")
        return

    # Write the .vrma (binary glTF) file.
    os.makedirs(OUTPUT_DIR, exist_ok=True)
    action = arm_obj.animation_data.action if arm_obj.animation_data else None
    name = (action.name if action else os.path.splitext(os.path.basename(bpy.data.filepath))[0]).replace(" ", "_")
    output_path = os.path.join(OUTPUT_DIR, f"{name}.vrma")

    jb = json.dumps(gltf, ensure_ascii=False, separators=(',', ':')).encode('utf-8')
    while len(jb) % 4: jb += b' '
    while len(bin_data) % 4: bin_data += b'\x00'

    total = 12 + 8 + len(jb) + 8 + len(bin_data)
    with open(output_path, 'wb') as f:
        f.write(b'glTF'); f.write(struct.pack('<I', 2)); f.write(struct.pack('<I', total))
        f.write(struct.pack('<I', len(jb))); f.write(b'JSON'); f.write(jb)
        f.write(struct.pack('<I', len(bin_data))); f.write(b'BIN\x00'); f.write(bytes(bin_data))

    preset_count = len(preset_expressions)
    custom_count = len(custom_expressions)

    log(f"")
    log(f"[VRMA v8.0] ========================================")
    log(f"[VRMA v8.0] EXPORT COMPLETE")
    log(f"[VRMA v8.0] Rig: {rig_type}")
    log(f"[VRMA v8.0] File: {output_path}")
    log(f"[VRMA v8.0] Bones: {len(found_bones)} | Frames: {num_anim_frames}")
    log(f"[VRMA v8.0] Expressions: {preset_count} preset + {custom_count} custom")
    log(f"[VRMA v8.0] ========================================")

    # v8.0: dump the captured log next to the .vrma.
    log_path = output_path.replace('.vrma', '.log.txt')
    with open(log_path, 'w', encoding='utf-8') as f:
        f.write('\n'.join(_log_lines))
    log(f"[VRMA v8.0] Log: {log_path}")

    scene.frame_set(frame_start)

if __name__ == '__main__':
    main()
