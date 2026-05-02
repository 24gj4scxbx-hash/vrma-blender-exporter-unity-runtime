// =============================================================
// VRMA Motion Controller - Runtime VRMA player (slim distribution build)
//
// Three core values, intentionally nothing else:
//   1) World Retarget        : copy bone rotations from a VRMA rig onto a
//                              VRM model using a WORLD-space delta, so the
//                              same .vrma plays correctly on any VRM
//                              regardless of local-axis differences.
//   2) Direct Binary Parser  : pull expression keyframes straight from the
//                              .vrma binary (UniVRM does not currently
//                              convert VRMC_vrm_animation expression
//                              channels to AnimationClip curves).
//   3) Expression matching   : preset names + custom names are both bound
//                              to the target VRM's Face SkinnedMesh
//                              BlendShapes. Works on VRM 0.x and 1.0.
//
// This file is meant as a STARTING POINT. It is deliberately thin: drop it
// onto a GameObject, hand it a loaded Vrm10Instance, point vrmaDirectory at
// a folder of .vrma files, and call PlayVrmaFile("clip.vrma", loop: true).
// Anything you might also want - crossfade between motions, automatic
// return-to-idle, an Act/state machine on top, blink yield to a separate
// procedural blink controller, cursor/look-at safety windows, hot-swap of
// the underlying VRM - is INTENTIONALLY left out and called out inline at
// the relevant insertion points so you can wire it in your own way.
//
// License: AGPL-3.0-or-later
// =============================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UniGLTF;
using UniVRM10;

/// <summary>
/// Runtime VRMA motion player - ControlRig OFF + direct bone copy.
///
/// Strategy: load the .vrma -> the VRMA's own skeleton plays the Animation
///           component -> in LateUpdate we copy each VRMA bone's rotation
///           onto the corresponding VRM bone. ControlRig is intentionally
///           NOT used (avoids the NullReferenceException paths that hit
///           when the VRM does not expose a ControlRig).
///
/// Coordinate conversion is already baked into the .vrma data, so no extra
/// conversion is performed here. _vrm.Runtime.VrmAnimation is intentionally
/// left unset to keep the ControlRig path disabled.
/// </summary>
[DefaultExecutionOrder(32000)]
public class MotionController : MonoBehaviour
{
    // ------------------------------------------------------------------
    // Inspector-assignable refs
    // ------------------------------------------------------------------
    [Header("References")]
    [Tooltip("The loaded VRM (UniVRM 1.0 instance) this controller drives. Assign in the Inspector after your VRM finishes loading, or set programmatically before WaitForVrmAndInit polls it.")]
    public Vrm10Instance vrmInstance;

    [Header("VRMA Settings")]
    // Folder containing your .vrma files. A relative path resolves against
    // the working directory at runtime; pass an absolute path (or rewrite
    // PlayVrmaFile to take a full path) if you'd rather keep clips outside
    // the working directory. Override in the Inspector to point at any
    // folder you ship your VRMA assets in.
    public string vrmaDirectory = "VRMA";

    [Header("Build Shader Fix")]
    [Tooltip("Required when loading VRMA in a build. Assign Standard / URP-Lit shaders here in the Inspector to keep them out of the shader strip pass; OnValidate auto-fills them in the Editor.")]
    [SerializeField] private Shader _gltfFallbackShader;
    [SerializeField] private Shader _urpLitShader;

    [Header("Status (Read Only)")]
    [SerializeField] private string _currentFile = "none";
    [SerializeField] private bool _isPlayingMotion = false;
    [SerializeField] private int _mappedBones = 0;

    // ------------------------------------------------------------------
    // Public read-only state
    // ------------------------------------------------------------------
    public bool IsPlayingMotion => _isPlayingMotion;
    public bool IsInitialized => _initialized;

    /// <summary>True while the currently-playing VRMA carries any expression
    /// (face BlendShape) tracks. Read this if you have an external blink or
    /// face-animation system and want it to yield to the .vrma's own face
    /// data while a face-bearing clip is active.</summary>
    public bool HasActiveExpressions { get; private set; }

    // ------------------------------------------------------------------
    // Runtime state
    // ------------------------------------------------------------------
    private Vrm10Instance _vrm;
    private Transform _vrmRoot;
    private Vector3 _lockedPosition;
    private Quaternion _lockedRotation;
    private bool _initialized = false;

    // VRMA cache: loading a .vrma + building bone pairs is not free, so we
    // keep one entry per filename. Cleared by Reinitialize() because cached
    // entries hold Transforms that point at the OLD VRM after a hot-swap.
    private Dictionary<string, VrmaPlayData> _vrmaCache = new Dictionary<string, VrmaPlayData>();

    // Currently-playing clip data.
    private VrmaPlayData _currentPlayData;

    // Direct Binary Parser time base: PlayVrmaFile records Time.time here,
    // and LateUpdate uses (Time.time - _playStartTime) to evaluate the
    // expression keyframe curves we extracted from the .vrma binary.
    private float _playStartTime = 0f;

    // ------------------------------------------------------------------
    // Internal data classes
    // ------------------------------------------------------------------

    /// <summary>Loaded VRMA data: source instance + the bone/expression
    /// bindings we computed against the current VRM.</summary>
    private class VrmaPlayData
    {
        public RuntimeGltfInstance instance;
        public Animation animation;
        public List<BonePair> bonePairs;        // VRMA bone -> VRM bone pairs
        public float clipLength;
        // Expression tracks recovered from the .vrma file. Empty when the
        // animation does not carry any face data. The runtime convention is
        // that a VRMA expression node's translation.x stores its weight (0-1),
        // which we map to SetBlendShapeWeight(weight * 100).
        public List<ExpressionPair> expressionPairs;
    }

    /// <summary>VRMA expression -> Face SkinnedMesh BlendShape binding.
    /// Populated by the direct binary parser: time/weight arrays come straight
    /// out of the GLB BIN chunk, so playback does not depend on UniVRM having
    /// generated a GameObject for the expression node (it does not, for
    /// VRMC_vrm_animation, the curves are not converted to AnimationClip
    /// curves either - hence the binary parse).</summary>
    private class ExpressionPair
    {
        public SkinnedMeshRenderer mesh;        // Face mesh (chosen carefully to avoid overlay/emote meshes)
        public int blendShapeIndex;             // BlendShape index on the Face mesh
        public string name;                     // original expression name (for logging)
        // Direct binary parser keyframes (used by InterpolateExpression):
        public float[] times;                   // time array (seconds)
        public float[] weights;                 // weight array (0-1, taken from translation.x)
    }

    /// <summary>One expression track extracted from a VRMA's binary buffer.</summary>
    private class VrmaExpressionData
    {
        public string name;
        public float[] times;
        public float[] weights;
    }

    /// <summary>World-Space Retarget container.
    /// We use a class (not struct) so foreach can mutate fields in place, and
    /// so each pair is heap-allocated exactly once.
    /// Why world-space: a Blender-authored .vrma can have non-identity rest
    /// rotations. Computing the delta in WORLD space - which is shared by
    /// the source and target skeletons regardless of local axis differences -
    /// makes parent-chain mismatches self-correct, so we do not need to
    /// pre-align local axes between the .vrma rig and the target VRM.</summary>
    private class BonePair
    {
        public Transform vrmaBone;              // source (animated by VRMA Animation)
        public Transform vrmBone;               // target (VRM model bone)
        public string name;
        public Quaternion vrmaRestWorld;        // VRMA bone's T-pose world rotation, captured before Play
        public string parentBoneName;           // humanoid parent bone name (null = root)
    }

    // =========================================================================
    // Initialization
    // =========================================================================

#if UNITY_EDITOR
    void OnValidate()
    {
        // Auto-fill shader references (prevents shader stripping in builds).
        if (_gltfFallbackShader == null)
            _gltfFallbackShader = Shader.Find("Standard");
        if (_urpLitShader == null)
            _urpLitShader = Shader.Find("Universal Render Pipeline/Lit");
    }
#endif

    void Start()
    {
        StartCoroutine(WaitForVrmAndInit());
    }

    private IEnumerator WaitForVrmAndInit()
    {
        // Wait until the host application hands us a loaded Vrm10Instance.
        while (vrmInstance == null)
            yield return new WaitForSeconds(0.5f);

        _vrm = vrmInstance;
        _vrmRoot = _vrm.gameObject.transform;
        _lockedPosition = _vrmRoot.localPosition;
        _lockedRotation = _vrmRoot.localRotation;

        // Disable Animator: we drive bone rotations manually in LateUpdate.
        var animator = _vrm.gameObject.GetComponent<Animator>();
        if (animator != null)
        {
            animator.enabled = false;
            Debug.Log("[Motion] Animator disabled (direct bone copy mode)");
        }

        _initialized = true;
        Debug.Log($"[Motion] Initialized. VRMA dir: {vrmaDirectory}");
    }

    // =========================================================================
    // VRMA Loading + Playback (ControlRig bypass)
    // =========================================================================

    /// <summary>Load and play a VRMA animation file from <see cref="vrmaDirectory"/>.
    /// loop=true plays the clip on a continuous loop; loop=false plays once and
    /// holds the final pose (it does not auto-return to any idle - if you want
    /// idle-return, schedule it from your own coroutine after PlayVrmaFile).</summary>
    public async void PlayVrmaFile(string filename, bool loop = false)
    {
        if (!_initialized)
        {
            Debug.LogWarning($"[Motion] Not initialized, cannot play {filename}");
            return;
        }

        string fullPath = Path.Combine(vrmaDirectory, filename);
        if (!File.Exists(fullPath))
        {
            Debug.LogError($"[Motion] VRMA not found: {fullPath}");
            return;
        }

        // Stop the current clip's Animation playback.
        if (_currentPlayData?.animation != null)
            _currentPlayData.animation.Stop();

        try
        {
            VrmaPlayData playData;

            if (_vrmaCache.TryGetValue(filename, out var cached))
            {
                playData = cached;
                Debug.Log($"[Motion] Cache hit: {filename}");
            }
            else
            {
                // Load VRMA
                byte[] bytes = File.ReadAllBytes(fullPath);
                using var gltfData = new GlbLowLevelParser(fullPath, bytes).Parse();
                using var loader = new VrmAnimationImporter(new VrmAnimationData(gltfData));
                var vrmaInstance = await loader.LoadAsync(new ImmediateCaller());

                if (vrmaInstance == null)
                {
                    Debug.LogError($"[Motion] Failed to load VRMA: {filename}");
                    return;
                }

                // Hide the VRMA skeleton: we use it purely as a pose source.
                vrmaInstance.gameObject.SetActive(true);
                foreach (var r in vrmaInstance.gameObject.GetComponentsInChildren<Renderer>(true))
                    r.enabled = false;

                // Build bone pair mappings (VRMA bone -> VRM bone).
                var bonePairs = BuildBonePairs(vrmaInstance.gameObject.transform, _vrmRoot);

                // Get Animation component.
                var anim = vrmaInstance.GetComponent<Animation>();
                float clipLen = 0f;
                if (anim != null)
                {
                    foreach (AnimationState state in anim)
                        clipLen = Mathf.Max(clipLen, state.length);
                }

                playData = new VrmaPlayData
                {
                    instance = vrmaInstance,
                    animation = anim,
                    bonePairs = bonePairs,
                    clipLength = clipLen,
                    expressionPairs = null  // populated below (after the cache branch)
                };

                _vrmaCache[filename] = playData;
                Debug.Log($"[Motion] Loaded VRMA: {filename}, bones={bonePairs.Count}, clipLen={clipLen:F2}s");
            }

            // ----------------------------------------------------------------
            // Direct Binary Parser: pull expression keyframes straight from
            // the .vrma binary buffer, then bind each to a Face BlendShape
            // index on the target VRM. UniVRM does not currently turn
            // VRMC_vrm_animation expression channels into AnimationClip
            // curves, so this binary parse is the only reliable path.
            // We run it on both cache hit and cache miss; if the cached entry
            // already has expressionPairs, we skip rebuilding (the binding is
            // identical for the same VRM).
            // ----------------------------------------------------------------
            {
                var exprDataList = ExtractVrmaExpressions(fullPath);
                HasActiveExpressions = exprDataList.Count > 0;
                Debug.Log($"[Motion] Expression detect: path={Path.GetFileName(fullPath)}, expressions={exprDataList.Count}");

                // Build ExpressionPair list (Face mesh + preset name -> BlendShape index).
                if (playData.expressionPairs == null)
                {
                    var newPairs = new List<ExpressionPair>();
                    if (exprDataList.Count > 0)
                    {
                        // Pick the Face mesh: prefer a mesh literally named
                        // "Face" (avoids accidentally targeting overlay /
                        // emoticon-style meshes that share BlendShape names).
                        SkinnedMeshRenderer faceMesh = null;
                        foreach (var smr in _vrmRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        {
                            if (smr.name.Contains("Face") && smr.sharedMesh != null && smr.sharedMesh.blendShapeCount > 0)
                            { faceMesh = smr; break; }
                        }
                        if (faceMesh == null)
                        {
                            int maxBs = 0;
                            foreach (var smr in _vrmRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                            {
                                if (smr.sharedMesh != null && smr.sharedMesh.blendShapeCount > maxBs)
                                { maxBs = smr.sharedMesh.blendShapeCount; faceMesh = smr; }
                            }
                        }

                        if (faceMesh != null)
                        {
                            var presetMap = new Dictionary<string, string[]>
                            {
                                {"blinkLeft",  new[]{"eyeBlinkLeft","Fcl_EYE_Close_L"}},
                                {"blinkRight", new[]{"eyeBlinkRight","Fcl_EYE_Close_R"}},
                                {"happy",      new[]{"Fcl_EYE_Joy1","Fcl_ALL_Joy"}},
                                {"angry",      new[]{"Fcl_EYE_Angry","Fcl_ALL_Angry"}},
                                {"sad",        new[]{"Fcl_EYE_Sorrow","Fcl_ALL_Sorrow"}},
                                {"surprised",  new[]{"Fcl_EYE_Surprised","Fcl_ALL_Surprised"}},
                                {"relaxed",    new[]{"Fcl_EYE_Natural","Fcl_ALL_Fun"}},
                                {"aa",         new[]{"Fcl_MTH_A","jawOpen"}},
                                {"ih",         new[]{"Fcl_MTH_I"}},
                                {"ou",         new[]{"Fcl_MTH_U","mouthFunnel"}},
                                {"ee",         new[]{"Fcl_MTH_E"}},
                                {"oh",         new[]{"Fcl_MTH_O"}},
                            };

                            foreach (var ed in exprDataList)
                            {
                                int bsIdx = faceMesh.sharedMesh.GetBlendShapeIndex(ed.name);
                                if (bsIdx < 0 && presetMap.TryGetValue(ed.name, out var candidates))
                                {
                                    foreach (var c in candidates)
                                    {
                                        bsIdx = faceMesh.sharedMesh.GetBlendShapeIndex(c);
                                        if (bsIdx >= 0) break;
                                    }
                                }
                                if (bsIdx >= 0)
                                {
                                    newPairs.Add(new ExpressionPair
                                    {
                                        mesh = faceMesh,
                                        blendShapeIndex = bsIdx,
                                        name = ed.name,
                                        times = ed.times,
                                        weights = ed.weights
                                    });
                                    Debug.Log($"[Motion] Expression: {ed.name} → blendShape[{bsIdx}] on {faceMesh.name} ({ed.times.Length} keyframes)");
                                }
                            }
                            Debug.Log($"[Motion] Expression pairs: {newPairs.Count}");
                        }
                    }
                    playData.expressionPairs = newPairs;
                }
            }

            // Play animation on the VRMA skeleton (NOT on the VRM - that path
            // would route through ControlRig, which we deliberately avoid).
            if (playData.animation != null)
            {
                foreach (AnimationState state in playData.animation)
                    state.wrapMode = loop ? WrapMode.Loop : WrapMode.Once;
                playData.animation.Play();
            }
            // Anchor the wall-clock for the Direct Binary Parser's curve evaluation.
            _playStartTime = Time.time;

            _currentPlayData = playData;
            _currentFile = filename;
            _mappedBones = playData.bonePairs.Count;
            _isPlayingMotion = true;

            Debug.Log($"[Motion] Playing: {filename} (loop={loop}, bones={playData.bonePairs.Count})");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Motion] Error: {e.Message}\n{e.StackTrace}");
        }
    }

    // VRMA humanoid name -> Unity HumanBodyBones (Humanoid API generic mapping).
    // Finger bones (30 entries) follow the VRM 1.0 humanoid spec.
    //   Thumb caveat: VRM spec defines {Metacarpal, Proximal, Distal} for the
    //   thumb, but Unity HumanBodyBones only has {ThumbProximal, Intermediate,
    //   Distal}. We map VRM Metacarpal -> Unity ThumbProximal, VRM Proximal ->
    //   Unity ThumbIntermediate, VRM Distal -> Unity ThumbDistal.
    private static readonly Dictionary<string, HumanBodyBones> VRMA_TO_HUMANOID = new Dictionary<string, HumanBodyBones>
    {
        {"hips", HumanBodyBones.Hips}, {"spine", HumanBodyBones.Spine},
        {"chest", HumanBodyBones.Chest}, {"upperChest", HumanBodyBones.UpperChest},
        {"neck", HumanBodyBones.Neck}, {"head", HumanBodyBones.Head},
        {"leftShoulder", HumanBodyBones.LeftShoulder}, {"leftUpperArm", HumanBodyBones.LeftUpperArm},
        {"leftLowerArm", HumanBodyBones.LeftLowerArm}, {"leftHand", HumanBodyBones.LeftHand},
        {"rightShoulder", HumanBodyBones.RightShoulder}, {"rightUpperArm", HumanBodyBones.RightUpperArm},
        {"rightLowerArm", HumanBodyBones.RightLowerArm}, {"rightHand", HumanBodyBones.RightHand},
        {"leftUpperLeg", HumanBodyBones.LeftUpperLeg}, {"leftLowerLeg", HumanBodyBones.LeftLowerLeg},
        {"leftFoot", HumanBodyBones.LeftFoot}, {"leftToes", HumanBodyBones.LeftToes},
        {"rightUpperLeg", HumanBodyBones.RightUpperLeg}, {"rightLowerLeg", HumanBodyBones.RightLowerLeg},
        {"rightFoot", HumanBodyBones.RightFoot}, {"rightToes", HumanBodyBones.RightToes},
        {"leftEye", HumanBodyBones.LeftEye}, {"rightEye", HumanBodyBones.RightEye},
        // -- Left fingers (15) ---------------------------------------------
        {"leftThumbMetacarpal",    HumanBodyBones.LeftThumbProximal},     // VRM Metacarpal = Unity Proximal
        {"leftThumbProximal",      HumanBodyBones.LeftThumbIntermediate}, // VRM Proximal   = Unity Intermediate
        {"leftThumbDistal",        HumanBodyBones.LeftThumbDistal},
        {"leftIndexProximal",      HumanBodyBones.LeftIndexProximal},
        {"leftIndexIntermediate",  HumanBodyBones.LeftIndexIntermediate},
        {"leftIndexDistal",        HumanBodyBones.LeftIndexDistal},
        {"leftMiddleProximal",     HumanBodyBones.LeftMiddleProximal},
        {"leftMiddleIntermediate", HumanBodyBones.LeftMiddleIntermediate},
        {"leftMiddleDistal",       HumanBodyBones.LeftMiddleDistal},
        {"leftRingProximal",       HumanBodyBones.LeftRingProximal},
        {"leftRingIntermediate",   HumanBodyBones.LeftRingIntermediate},
        {"leftRingDistal",         HumanBodyBones.LeftRingDistal},
        {"leftLittleProximal",     HumanBodyBones.LeftLittleProximal},
        {"leftLittleIntermediate", HumanBodyBones.LeftLittleIntermediate},
        {"leftLittleDistal",       HumanBodyBones.LeftLittleDistal},
        // -- Right fingers (15) --------------------------------------------
        {"rightThumbMetacarpal",    HumanBodyBones.RightThumbProximal},
        {"rightThumbProximal",      HumanBodyBones.RightThumbIntermediate},
        {"rightThumbDistal",        HumanBodyBones.RightThumbDistal},
        {"rightIndexProximal",      HumanBodyBones.RightIndexProximal},
        {"rightIndexIntermediate",  HumanBodyBones.RightIndexIntermediate},
        {"rightIndexDistal",        HumanBodyBones.RightIndexDistal},
        {"rightMiddleProximal",     HumanBodyBones.RightMiddleProximal},
        {"rightMiddleIntermediate", HumanBodyBones.RightMiddleIntermediate},
        {"rightMiddleDistal",       HumanBodyBones.RightMiddleDistal},
        {"rightRingProximal",       HumanBodyBones.RightRingProximal},
        {"rightRingIntermediate",   HumanBodyBones.RightRingIntermediate},
        {"rightRingDistal",         HumanBodyBones.RightRingDistal},
        {"rightLittleProximal",     HumanBodyBones.RightLittleProximal},
        {"rightLittleIntermediate", HumanBodyBones.RightLittleIntermediate},
        {"rightLittleDistal",       HumanBodyBones.RightLittleDistal},
    };

    /// <summary>
    /// Build bone pairs: find matching bones between the VRMA skeleton and
    /// the VRM model. Match order: 1) Humanoid API (rig-agnostic),
    /// 2) direct name match, 3) J_Bip name fallback (VRoid convention).
    /// </summary>
    private List<BonePair> BuildBonePairs(Transform vrmaRoot, Transform vrmRoot)
    {
        var pairs = new List<BonePair>();
        var allVrmaBones = new List<Transform>();
        CollectAllChildren(vrmaRoot, allVrmaBones);

        // Animator reference. GetBoneTransform works even when enabled=false,
        // as long as an Avatar is assigned.
        var animator = vrmRoot.GetComponentInParent<Animator>();
        if (animator == null) animator = vrmRoot.GetComponent<Animator>();
        bool hasAnimator = animator != null && animator.avatar != null;

        int matched = 0, skipped = 0;

        foreach (var vrmaBone in allVrmaBones)
        {
            Transform vrmBone = null;

            // 1) Humanoid API: Avatar-driven, name-agnostic.
            if (hasAnimator && VRMA_TO_HUMANOID.TryGetValue(vrmaBone.name, out var humanBone))
            {
                vrmBone = animator.GetBoneTransform(humanBone);
            }

            // 2) Direct name match (works on rigs that use the VRMA names verbatim).
            if (vrmBone == null)
                vrmBone = FindBoneRecursive(vrmRoot, vrmaBone.name);

            // 3) J_Bip name fallback (VRoid).
            if (vrmBone == null && VRMA_TO_VRM.TryGetValue(vrmaBone.name, out string vrmName))
                vrmBone = FindBoneRecursive(vrmRoot, vrmName);

            if (vrmBone != null)
            {
                // World-Space Retarget setup: capture the VRMA bone's T-pose
                // world rotation BEFORE Animation.Play(). At this point the
                // VRMA rig is still at the rest pose authored by the exporter.
                // parentBoneName is filled in by a second pass below.
                pairs.Add(new BonePair
                {
                    vrmaBone = vrmaBone,
                    vrmBone = vrmBone,
                    name = vrmaBone.name,
                    vrmaRestWorld = vrmaBone.rotation, // world rotation (parent-chain accumulated)
                    parentBoneName = null              // filled in by second pass
                });
                matched++;
            }
            else
            {
                skipped++;
            }
        }

        // Second pass for World-Space Retarget: for each pair, find the
        // closest *included* humanoid ancestor and remember its name. The
        // LateUpdate loop uses worldDeltas[parentBoneName] to peel the
        // parent's contribution off and recover a clean local rotation.
        var pairsByName = new HashSet<string>();
        foreach (var p in pairs) pairsByName.Add(p.name);

        foreach (var pair in pairs)
        {
            Transform vrmaParent = pair.vrmaBone.parent;
            while (vrmaParent != null)
            {
                if (pairsByName.Contains(vrmaParent.name))
                {
                    pair.parentBoneName = vrmaParent.name;
                    break;
                }
                vrmaParent = vrmaParent.parent;
            }
        }

        Debug.Log($"[Motion] Bone pairs: {matched} matched, {skipped} skipped (Humanoid API={hasAnimator})");

        return pairs;
    }

    // VRMA humanoid names -> VRM J_Bip names (the VRoid naming convention).
    // 30 finger entries follow the VRoid pattern: J_Bip_L_Thumb1..3
    // (Metacarpal/Proximal/Distal), J_Bip_L_Index1..3 (Proximal/Intermediate/
    // Distal), and similar for the other fingers.
    private static readonly Dictionary<string, string> VRMA_TO_VRM = new Dictionary<string, string>
    {
        {"hips", "J_Bip_C_Hips"}, {"spine", "J_Bip_C_Spine"}, {"chest", "J_Bip_C_Chest"},
        {"upperChest", "J_Bip_C_UpperChest"}, {"neck", "J_Bip_C_Neck"}, {"head", "J_Bip_C_Head"},
        {"leftShoulder", "J_Bip_L_Shoulder"}, {"leftUpperArm", "J_Bip_L_UpperArm"},
        {"leftLowerArm", "J_Bip_L_LowerArm"}, {"leftHand", "J_Bip_L_Hand"},
        {"rightShoulder", "J_Bip_R_Shoulder"}, {"rightUpperArm", "J_Bip_R_UpperArm"},
        {"rightLowerArm", "J_Bip_R_LowerArm"}, {"rightHand", "J_Bip_R_Hand"},
        {"leftUpperLeg", "J_Bip_L_UpperLeg"}, {"leftLowerLeg", "J_Bip_L_LowerLeg"},
        {"leftFoot", "J_Bip_L_Foot"}, {"leftToes", "J_Bip_L_ToeBase"},
        {"rightUpperLeg", "J_Bip_R_UpperLeg"}, {"rightLowerLeg", "J_Bip_R_LowerLeg"},
        {"rightFoot", "J_Bip_R_Foot"}, {"rightToes", "J_Bip_R_ToeBase"},
        // Eye = canonical J_Bip names only. VRoid's J_Adj_FaceEye lives in a
        // different coordinate frame and gets skipped automatically by the
        // matcher; eye gaze should be handled by an external LookAt /
        // Expression-API controller.
        {"leftEye", "J_Bip_L_Eye"}, {"rightEye", "J_Bip_R_Eye"},
        // -- Left fingers (J_Bip fallback, 15) -----------------------------
        {"leftThumbMetacarpal",    "J_Bip_L_Thumb1"},
        {"leftThumbProximal",      "J_Bip_L_Thumb2"},
        {"leftThumbDistal",        "J_Bip_L_Thumb3"},
        {"leftIndexProximal",      "J_Bip_L_Index1"},
        {"leftIndexIntermediate",  "J_Bip_L_Index2"},
        {"leftIndexDistal",        "J_Bip_L_Index3"},
        {"leftMiddleProximal",     "J_Bip_L_Middle1"},
        {"leftMiddleIntermediate", "J_Bip_L_Middle2"},
        {"leftMiddleDistal",       "J_Bip_L_Middle3"},
        {"leftRingProximal",       "J_Bip_L_Ring1"},
        {"leftRingIntermediate",   "J_Bip_L_Ring2"},
        {"leftRingDistal",         "J_Bip_L_Ring3"},
        {"leftLittleProximal",     "J_Bip_L_Little1"},
        {"leftLittleIntermediate", "J_Bip_L_Little2"},
        {"leftLittleDistal",       "J_Bip_L_Little3"},
        // -- Right fingers (J_Bip fallback, 15) ----------------------------
        {"rightThumbMetacarpal",    "J_Bip_R_Thumb1"},
        {"rightThumbProximal",      "J_Bip_R_Thumb2"},
        {"rightThumbDistal",        "J_Bip_R_Thumb3"},
        {"rightIndexProximal",      "J_Bip_R_Index1"},
        {"rightIndexIntermediate",  "J_Bip_R_Index2"},
        {"rightIndexDistal",        "J_Bip_R_Index3"},
        {"rightMiddleProximal",     "J_Bip_R_Middle1"},
        {"rightMiddleIntermediate", "J_Bip_R_Middle2"},
        {"rightMiddleDistal",       "J_Bip_R_Middle3"},
        {"rightRingProximal",       "J_Bip_R_Ring1"},
        {"rightRingIntermediate",   "J_Bip_R_Ring2"},
        {"rightRingDistal",         "J_Bip_R_Ring3"},
        {"rightLittleProximal",     "J_Bip_R_Little1"},
        {"rightLittleIntermediate", "J_Bip_R_Little2"},
        {"rightLittleDistal",       "J_Bip_R_Little3"},
    };

    private static void CollectAllChildren(Transform parent, List<Transform> result)
    {
        foreach (Transform child in parent)
        {
            result.Add(child);
            CollectAllChildren(child, result);
        }
    }

    /// <summary>Parse GLB binary to extract expression keyframes.
    /// Walks the .vrma's JSON chunk to map nodes/expressions/animations,
    /// then reads the BIN chunk for the actual time/translation arrays.
    /// VRMC_vrm_animation convention: an expression channel uses the
    /// "translation" path on the expression node, with the weight encoded
    /// in translation.x.
    /// Flow: nodes[].name -> expressions{} node indices -> animations[0]
    /// channels (keep the ones that target an expression node with path
    /// "translation") -> samplers[] -> accessors[] -> bufferViews[] -> BIN.</summary>
    private List<VrmaExpressionData> ExtractVrmaExpressions(string vrmaPath)
    {
        var result = new List<VrmaExpressionData>();
        try
        {
            byte[] file = System.IO.File.ReadAllBytes(vrmaPath);
            if (file.Length < 28) return result;

            int jsonLen = System.BitConverter.ToInt32(file, 12);
            if (jsonLen <= 0 || jsonLen > 100 * 1024 * 1024) return result;
            string json = System.Text.Encoding.UTF8.GetString(file, 20, jsonLen);

            int binOffset = 20 + jsonLen;
            while (binOffset % 4 != 0) binOffset++;
            int binStart = binOffset + 8;

            if (!json.Contains("\"expressions\"")) return result;

            // 1. Collect every node's "name" in file order (= node index order).
            //    Match "nodes":[{ specifically, to disambiguate from scenes.nodes=[0,54].
            var nodeNames = new List<string>();
            {
                int nodesArr = json.IndexOf("\"nodes\":[{");
                if (nodesArr < 0) return result;
                string nodesBlock = ExtractJsonArray(json, nodesArr);
                int sf = 0;
                while (true)
                {
                    int ni = nodesBlock.IndexOf("\"name\"", sf);
                    if (ni < 0) break;
                    int c = nodesBlock.IndexOf(":", ni + 6);
                    if (c < 0) break;
                    int q1 = nodesBlock.IndexOf("\"", c + 1);
                    if (q1 < 0) break;
                    int q2 = nodesBlock.IndexOf("\"", q1 + 1);
                    if (q2 < 0) break;
                    nodeNames.Add(nodesBlock.Substring(q1 + 1, q2 - q1 - 1));
                    sf = q2 + 1;
                }
            }

            // 2. Map expression entries -> node indices (so we can later check
            //    "is this animation channel pointed at an expression node").
            var exprNodes = new Dictionary<int, string>();
            {
                int exprStart = json.IndexOf("\"expressions\"");
                int braceStart = json.IndexOf("{", exprStart);
                if (braceStart < 0) return result;
                int d = 1, braceEnd = braceStart + 1;
                while (braceEnd < json.Length && d > 0)
                {
                    if (json[braceEnd] == '{') d++;
                    else if (json[braceEnd] == '}') d--;
                    braceEnd++;
                }
                string eb = json.Substring(braceStart, braceEnd - braceStart);
                int sf = 0;
                while (true)
                {
                    int nk = eb.IndexOf("\"node\"", sf);
                    if (nk < 0) break;
                    int col = eb.IndexOf(":", nk + 6);
                    if (col < 0) break;
                    int ns = col + 1;
                    while (ns < eb.Length && !char.IsDigit(eb[ns])) ns++;
                    int ne = ns;
                    while (ne < eb.Length && char.IsDigit(eb[ne])) ne++;
                    if (ns < ne && int.TryParse(eb.Substring(ns, ne - ns), out int ni2) && ni2 >= 0 && ni2 < nodeNames.Count)
                        exprNodes[ni2] = nodeNames[ni2];
                    sf = ne;
                }
            }
            if (exprNodes.Count == 0) return result;

            // 3. Parse bufferViews (we only need byteOffset + byteLength).
            var bvList = new List<int[]>(); // [byteOffset, byteLength]
            {
                int bvStart = json.IndexOf("\"bufferViews\"");
                if (bvStart < 0) return result;
                string bvBlock = ExtractJsonArray(json, bvStart);
                int sf = 0;
                while (true)
                {
                    int os = bvBlock.IndexOf("{", sf);
                    if (os < 0) break;
                    int oe = bvBlock.IndexOf("}", os);
                    if (oe < 0) break;
                    string obj = bvBlock.Substring(os, oe - os + 1);
                    int bo = ParseIntField(obj, "byteOffset");
                    int bl = ParseIntField(obj, "byteLength");
                    bvList.Add(new int[] { bo, bl });
                    sf = oe + 1;
                }
            }

            // 4. Parse accessors as [bufferView, byteOffset, count, componentSize].
            var accList = new List<int[]>();
            {
                int accStart = json.IndexOf("\"accessors\"");
                if (accStart < 0) return result;
                string accBlock = ExtractJsonArray(json, accStart);
                int sf = 0;
                while (true)
                {
                    int os = accBlock.IndexOf("{", sf);
                    if (os < 0) break;
                    int oe = accBlock.IndexOf("}", os);
                    if (oe < 0) break;
                    string obj = accBlock.Substring(os, oe - os + 1);
                    int bv = ParseIntField(obj, "bufferView");
                    int bo = ParseIntField(obj, "byteOffset");
                    int cnt = ParseIntField(obj, "count");
                    string tp = ParseStringField(obj, "type");
                    int cs = (tp == "VEC3") ? 12 : (tp == "VEC4") ? 16 : 4; // SCALAR=4
                    accList.Add(new int[] { bv, bo, cnt, cs });
                    sf = oe + 1;
                }
            }

            // 5. animations[0] channels + samplers
            int animStart = json.IndexOf("\"animations\"");
            if (animStart < 0) return result;
            int chStart = json.IndexOf("\"channels\"", animStart);
            if (chStart < 0) return result;
            string chBlock = ExtractJsonArray(json, chStart);

            var channels = new List<int[]>(); // [samplerIdx, nodeIdx, isTranslation]
            {
                int sf = 0;
                while (true)
                {
                    int os = chBlock.IndexOf("{", sf);
                    if (os < 0) break;
                    int d = 1, oe = os + 1;
                    while (oe < chBlock.Length && d > 0)
                    {
                        if (chBlock[oe] == '{') d++;
                        else if (chBlock[oe] == '}') d--;
                        oe++;
                    }
                    string obj = chBlock.Substring(os, oe - os);
                    int si = ParseIntField(obj, "sampler");
                    int ni = ParseIntField(obj, "node");
                    string p = ParseStringField(obj, "path");
                    channels.Add(new int[] { si, ni, p == "translation" ? 1 : 0 });
                    sf = oe;
                }
            }

            int spStart = json.IndexOf("\"samplers\"", animStart);
            if (spStart < 0) return result;
            string spBlock = ExtractJsonArray(json, spStart);
            var samplers = new List<int[]>(); // [inputAcc, outputAcc]
            {
                int sf = 0;
                while (true)
                {
                    int os = spBlock.IndexOf("{", sf);
                    if (os < 0) break;
                    int oe = spBlock.IndexOf("}", os);
                    if (oe < 0) break;
                    string obj = spBlock.Substring(os, oe - os + 1);
                    int inp = ParseIntField(obj, "input");
                    int outp = ParseIntField(obj, "output");
                    samplers.Add(new int[] { inp, outp });
                    sf = oe + 1;
                }
            }

            // 6. For each channel that targets an expression node with the
            //    "translation" path, read the time/weight pairs out of BIN.
            foreach (var ch in channels)
            {
                if (ch[2] != 1) continue; // translation only
                if (!exprNodes.ContainsKey(ch[1])) continue; // expression nodes only

                string ename = exprNodes[ch[1]];
                int samplerIdx = ch[0];
                if (samplerIdx < 0 || samplerIdx >= samplers.Count) continue;

                int timeAccIdx = samplers[samplerIdx][0];
                int transAccIdx = samplers[samplerIdx][1];
                if (timeAccIdx < 0 || timeAccIdx >= accList.Count) continue;
                if (transAccIdx < 0 || transAccIdx >= accList.Count) continue;

                var timeAcc = accList[timeAccIdx];
                var transAcc = accList[transAccIdx];
                int count = Mathf.Min(timeAcc[2], transAcc[2]);
                if (count <= 0) continue;

                int timeBvIdx = timeAcc[0];
                int transBvIdx = transAcc[0];
                if (timeBvIdx < 0 || timeBvIdx >= bvList.Count) continue;
                if (transBvIdx < 0 || transBvIdx >= bvList.Count) continue;

                int timeDataOffset = binStart + bvList[timeBvIdx][0] + timeAcc[1];
                int transDataOffset = binStart + bvList[transBvIdx][0] + transAcc[1];

                float[] times = new float[count];
                float[] weights = new float[count];
                int validCount = count;

                for (int i = 0; i < count; i++)
                {
                    int tOff = timeDataOffset + i * 4;
                    int wOff = transDataOffset + i * 12; // VEC3 stride = 12, x at offset 0
                    if (tOff + 4 > file.Length || wOff + 4 > file.Length) { validCount = i; break; }
                    times[i] = System.BitConverter.ToSingle(file, tOff);
                    weights[i] = System.BitConverter.ToSingle(file, wOff);
                }

                if (validCount > 0)
                {
                    if (validCount < count)
                    {
                        System.Array.Resize(ref times, validCount);
                        System.Array.Resize(ref weights, validCount);
                    }
                    // Manual min/max scan (avoids a Linq dependency).
                    float wmin = weights[0], wmax = weights[0];
                    for (int i = 1; i < weights.Length; i++)
                    {
                        if (weights[i] < wmin) wmin = weights[i];
                        if (weights[i] > wmax) wmax = weights[i];
                    }
                    result.Add(new VrmaExpressionData { name = ename, times = times, weights = weights });
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[Motion] ExtractVrmaExpressions failed: {e.Message}");
        }
        return result;
    }

    /// <summary>Extract a balanced JSON array starting at or after `keyStart`.</summary>
    private string ExtractJsonArray(string json, int keyStart)
    {
        int arrStart = json.IndexOf("[", keyStart);
        if (arrStart < 0) return "[]";
        int depth = 1, arrEnd = arrStart + 1;
        while (arrEnd < json.Length && depth > 0)
        {
            if (json[arrEnd] == '[') depth++;
            else if (json[arrEnd] == ']') depth--;
            arrEnd++;
        }
        return json.Substring(arrStart, arrEnd - arrStart);
    }

    /// <summary>Parse an integer field out of a stringified JSON object (supports negatives).</summary>
    private int ParseIntField(string obj, string fieldName)
    {
        int idx = obj.IndexOf("\"" + fieldName + "\"");
        if (idx < 0) return 0;
        int colon = obj.IndexOf(":", idx);
        if (colon < 0) return 0;
        int numStart = colon + 1;
        while (numStart < obj.Length && (obj[numStart] == ' ' || obj[numStart] == '\t')) numStart++;
        bool negative = numStart < obj.Length && obj[numStart] == '-';
        if (negative) numStart++;
        int numEnd = numStart;
        while (numEnd < obj.Length && char.IsDigit(obj[numEnd])) numEnd++;
        if (numStart < numEnd && int.TryParse(obj.Substring(numStart, numEnd - numStart), out int val))
            return negative ? -val : val;
        return 0;
    }

    /// <summary>Parse a string field out of a stringified JSON object.</summary>
    private string ParseStringField(string obj, string fieldName)
    {
        int idx = obj.IndexOf("\"" + fieldName + "\"");
        if (idx < 0) return "";
        int colon = obj.IndexOf(":", idx);
        if (colon < 0) return "";
        int q1 = obj.IndexOf("\"", colon + 1);
        if (q1 < 0) return "";
        int q2 = obj.IndexOf("\"", q1 + 1);
        if (q2 < 0) return "";
        return obj.Substring(q1 + 1, q2 - q1 - 1);
    }

    /// <summary>Binary search + Lerp for expression weight at a given time.</summary>
    private float InterpolateExpression(float[] times, float[] weights, float t)
    {
        if (times == null || times.Length == 0) return 0f;
        if (t <= times[0]) return weights[0];
        if (t >= times[times.Length - 1]) return weights[weights.Length - 1];

        int lo = 0, hi = times.Length - 1;
        while (lo < hi - 1)
        {
            int mid = (lo + hi) / 2;
            if (times[mid] <= t) lo = mid;
            else hi = mid;
        }
        float t0 = times[lo], t1 = times[hi];
        float w0 = weights[lo], w1 = weights[hi];
        float frac = (t1 > t0) ? (t - t0) / (t1 - t0) : 0f;
        return Mathf.Lerp(w0, w1, frac);
    }

    private static Transform FindBoneRecursive(Transform parent, string boneName)
    {
        if (parent.name == boneName) return parent;
        foreach (Transform child in parent)
        {
            var found = FindBoneRecursive(child, boneName);
            if (found != null) return found;
        }
        return null;
    }

    // =========================================================================
    // LateUpdate - VRMA bone -> VRM bone rotation copy + expression playback
    // =========================================================================

    void LateUpdate()
    {
        if (!_initialized || _vrmRoot == null) return;

        // Lock root position/rotation so the .vrma cannot translate / rotate
        // the model away from where the host application placed it.
        _vrmRoot.localPosition = _lockedPosition;
        _vrmRoot.localRotation = _lockedRotation;

        // World-Space Retarget - VRMA bone -> VRM bone (single unified loop).
        //   vrmaWorldN = pair.vrmaBone.rotation                     (current world rotation)
        //   worldDelta = vrmaWorldN * Inv(vrmaRestWorld)            (delta from T-pose, in WORLD space)
        //   parentDelta = worldDeltas[parentBoneName]               (computed earlier this loop, top-down)
        //   targetLocal = Inv(parentDelta) * worldDelta             (decompose into VRM bone's local rotation)
        // Why world-space: WORLD is shared between source and target rigs, so
        // local-axis differences and parent-chain mismatches self-correct.
        // The same formula works for BVH (identity rest), Mixamo, and
        // Blender-authored VRMA with non-identity rest.
        if (_currentPlayData?.bonePairs != null)
        {
            // World deltas - top-down (BuildBonePairs walks the hierarchy in order, so
            // every parent has already been written by the time we reach a child).
            var worldDeltas = new Dictionary<string, Quaternion>(_currentPlayData.bonePairs.Count);

            foreach (var pair in _currentPlayData.bonePairs)
            {
                if (pair.vrmaBone == null || pair.vrmBone == null) continue;

                // 1. Current world rotation of the VRMA bone (Unity's hierarchy update gives us this for free).
                Quaternion vrmaWorldN = pair.vrmaBone.rotation;

                // 2. World delta from the captured T-pose. Order matters:
                //    curr * Inv(rest) is the WORLD delta. (Inv(rest) * curr is the LOCAL delta - not what we want here.)
                Quaternion worldDelta = vrmaWorldN * Quaternion.Inverse(pair.vrmaRestWorld);
                worldDeltas[pair.name] = worldDelta;

                // 3. Decompose into a local rotation by peeling off the parent's world delta.
                Quaternion parentWorldDelta = Quaternion.identity;
                if (pair.parentBoneName != null && worldDeltas.TryGetValue(pair.parentBoneName, out var pwd))
                {
                    parentWorldDelta = pwd;
                }

                Quaternion targetLocal = Quaternion.Inverse(parentWorldDelta) * worldDelta;

                // 4. Apply.
                pair.vrmBone.localRotation = targetLocal;
            }

            // Direct Binary Parser playback: drive each bound BlendShape from
            // the keyframe arrays we extracted at load time. UniVRM does not
            // apply VRMC_vrm_animation expression channels itself, so we run
            // our own evaluator here and call SetBlendShapeWeight directly.
            // ExecutionOrder is 32000 - leave room for downstream controllers
            // (a procedural blink, an iris reactor, etc.) at higher orders to
            // react. For looping clips we wrap with `% clipLength`; for
            // one-shot clips InterpolateExpression naturally clamps to the
            // last value once `elapsed` exceeds the curve domain.
            if (_currentPlayData.expressionPairs != null && _currentPlayData.expressionPairs.Count > 0)
            {
                float elapsed = _currentPlayData.clipLength > 0f
                    ? (Time.time - _playStartTime) % _currentPlayData.clipLength
                    : 0f;
                foreach (var expr in _currentPlayData.expressionPairs)
                {
                    if (expr.mesh == null || expr.times == null || expr.weights == null) continue;
                    if (expr.times.Length == 0) continue;
                    float weight = InterpolateExpression(expr.times, expr.weights, elapsed);
                    expr.mesh.SetBlendShapeWeight(expr.blendShapeIndex, weight * 100f);
                }
            }
        }
    }

    // =========================================================================
    // Lifecycle helpers
    // =========================================================================

    /// <summary>Stop the currently-playing clip and clear the initialized
    /// flag. Call <see cref="Reinitialize"/> afterward (or assign a fresh
    /// <see cref="vrmInstance"/> and let WaitForVrmAndInit re-poll) to
    /// resume.</summary>
    public void StopCurrentMotion()
    {
        if (_currentPlayData != null && _currentPlayData.animation != null)
            _currentPlayData.animation.Stop();
        _isPlayingMotion = false;
        Debug.Log("[Motion] StopCurrentMotion: all stopped");
    }

    /// <summary>Re-initialize after a VRM swap: drop the VRMA cache (each
    /// cached entry holds Transforms from the OLD VRM), and re-run
    /// WaitForVrmAndInit so the controller binds to whatever Vrm10Instance
    /// the host assigns next.</summary>
    public void Reinitialize()
    {
        // Clear VRMA cache (each cached entry holds Transforms from the previous VRM).
        foreach (var kvp in _vrmaCache)
        {
            if (kvp.Value?.instance != null)
                Destroy(kvp.Value.instance.gameObject);
        }
        _vrmaCache.Clear();

        _vrm = null;
        _vrmRoot = null;
        _currentPlayData = null;
        _initialized = false;
        _currentFile = "none";
        _mappedBones = 0;

        StartCoroutine(WaitForVrmAndInit());
        Debug.Log("[Motion] Reinitialize: cache cleared, waiting for new VRM");
    }

    void OnDestroy()
    {
        if (_currentPlayData?.animation != null)
            _currentPlayData.animation.Stop();

        foreach (var kvp in _vrmaCache)
        {
            if (kvp.Value?.instance != null)
                Destroy(kvp.Value.instance.gameObject);
        }
        _vrmaCache.Clear();
    }
}
