using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NorthwoodVrFixes
{
    public sealed partial class Plugin
    {
        private ConfigEntry<float> actorAdjustSpeed;
        private ConfigEntry<bool> actorHideHead;
        private bool actorPanelOpen, actorProfilesDirty;
        private readonly List<ActorCandidate> actorCandidates = new List<ActorCandidate>();
        private readonly List<string> actorRejectedDetails = new List<string>();
        private readonly List<OcclusionMesh> actorOcclusion = new List<OcclusionMesh>();
        private readonly Dictionary<string, Mesh> actorMeshCache = new Dictionary<string, Mesh>();
        private readonly Stack<Camera> actorRenderStack = new Stack<Camera>();
        private ActorCandidate boundActor;
        private ActorCalibration actorProfile;
        private ActorCalibrationStore actorProfiles;
        private string actorProfileFile, actorNotice = "进入三维场景后，刷新并选择角色。";
        private Vector2 actorScroll;
        private Vector3 actorEye, actorOffset, actorBodyForward;
        private Vector3 actorEyeSurfaceOffset;
        private string actorAnchorKind = "";
        private Quaternion actorHeading = Quaternion.identity;
        private bool actorRenderingChanged;
        private int actorHidden, actorClipped, actorUnsupported, actorScanRejected;
        private float nextActorSave;
        private int actorScans, actorMeshBuilds;

        private sealed class ActorCandidate
        {
            public Transform Root, Head, LeftEye, RightEye, LeftEyeTip, RightEyeTip, Neck, Body;
            public string Name, Key, Role;
            public readonly List<SkinnedMeshRenderer> Skins = new List<SkinnedMeshRenderer>();
            public bool Valid
            {
                get
                {
                    if (Head == null || !Head.gameObject.activeInHierarchy) return false;
                    // Temporary head hiding during a camera render must not invalidate the actor.
                    for (int i = 0; i < Skins.Count; i++)
                        if (Skins[i] != null && Skins[i].gameObject.activeInHierarchy) return true;
                    return false;
                }
            }
        }

        private sealed class OcclusionMesh
        {
            public Renderer Renderer;
            public Mesh Original, Clipped;
            public bool Hide, Applied, EnabledBefore;
        }

        private void InitializeActorView()
        {
            actorAdjustSpeed = Config.Bind("08 Character View", "Eye adjustment speed", 0.05f,
                "Metres per second while bound. Each offset axis is limited to +/-0.25 metres.");
            actorHideHead = Config.Bind("08 Character View", "Hide own head in first person", true);
            actorProfileFile = Path.Combine(Paths.ConfigPath, "NorthwoodVrFixes.actors.json");
            try { actorProfiles = File.Exists(actorProfileFile) ?
                CalibrationJson.Read<ActorCalibrationStore>(File.ReadAllText(actorProfileFile)) : new ActorCalibrationStore(); }
            catch (Exception e) { Logger.LogWarning("Actor calibration unreadable; preserving file: " + e.Message); }
            if (actorProfiles == null) actorProfiles = new ActorCalibrationStore();
            if (actorProfiles.Items == null) actorProfiles.Items = new List<ActorCalibration>();
            Camera.onPreCull += ActorBeforeCull;
            Camera.onPostRender += ActorAfterRender;
        }

        private void ToggleActorPanel()
        {
            if (Interaction.ConfirmOpen) return;
            actorPanelOpen = !actorPanelOpen;
            Interaction.Release();
            gripTracking = false;
            uiAnchorValid = false;
            if (actorPanelOpen)
            {
                RefreshActors();
                EnsureModalBlocker();
                modalBlocker.SetActive(true);
            }
            Logger.LogInfo("Character panel: " + (actorPanelOpen ? "OPEN" : "CLOSED"));
        }

        private static Transform CommonAncestor(Transform a, Transform b)
        {
            for (Transform t = a; t != null; t = t.parent)
                if (t == b || b.IsChildOf(t)) return t;
            return a;
        }

        private static string TransformPath(Transform t)
        {
            List<string> parts = new List<string>();
            for (; t != null; t = t.parent) parts.Add(t.name + "#" + t.GetSiblingIndex());
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private void RefreshActors()
        {
            actorScans++;
            actorCandidates.Clear();
            actorRejectedDetails.Clear();
            actorScanRejected = 0;
            Dictionary<int, ActorCandidate> byHead = new Dictionary<int, ActorCandidate>();
            foreach (SkinnedMeshRenderer skin in UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>())
            {
                if (skin == null || !skin.enabled || skin.sharedMesh == null ||
                    !skin.gameObject.activeInHierarchy ||
                    skin.gameObject.scene != SceneManager.GetActiveScene()) continue;
                Animator animator = skin.GetComponentInParent<Animator>();
                Transform head = null, left = null, right = null, body = null, neck = null;
                if (animator != null && animator.isHuman)
                {
                    try
                    {
                        head = animator.GetBoneTransform(HumanBodyBones.Head);
                        left = animator.GetBoneTransform(HumanBodyBones.LeftEye);
                        right = animator.GetBoneTransform(HumanBodyBones.RightEye);
                        neck = animator.GetBoneTransform(HumanBodyBones.Neck);
                        body = animator.GetBoneTransform(HumanBodyBones.Chest);
                        if (body == null) body = animator.GetBoneTransform(HumanBodyBones.Hips);
                    }
                    catch (InvalidOperationException) { }
                }
                Transform[] nodes = skin.rootBone != null ?
                    skin.rootBone.GetComponentsInChildren<Transform>(true) : skin.bones;
                HashSet<Transform> scope = new HashSet<Transform>(nodes.Where(t => t != null));
                foreach (Transform bone in skin.bones)
                    for (Transform t = bone; t != null; t = t.parent)
                    {
                        scope.Add(t);
                        if (animator != null && t == animator.transform) break;
                    }
                nodes = scope.ToArray();
                if (head == null) head = nodes.FirstOrDefault(t => t != null && ActorMath.IsHead(t.name));
                if (head == null)
                {
                    foreach (Transform bone in skin.bones)
                    {
                        for (Transform t = bone; t != null; t = t.parent)
                            if (ActorMath.IsHead(t.name)) { head = t; break; }
                        if (head != null) break;
                    }
                }
                if (head == null)
                {
                    actorScanRejected++;
                    actorRejectedDetails.Add("UNRECOGNIZED " + TransformPath(skin.transform) +
                        " | mesh=" + skin.sharedMesh.name + " | rootBone=" +
                        (skin.rootBone == null ? "-" : skin.rootBone.name) + " | animator=" +
                        (animator == null ? "-" : animator.name) + " | weighted bones=" +
                        string.Join(", ", skin.bones.Where(t => t != null).Select(t => t.name).ToArray()) +
                        " | hierarchy=" + string.Join(", ", nodes.Where(t => t != null).Select(t => t.name).ToArray()));
                    continue;
                }
                List<Transform> heads = nodes.Where(t => t != null && t.gameObject.activeInHierarchy &&
                    ActorMath.IsHead(t.name)).OrderBy(t => TransformPath(t)).ToList();
                if (!heads.Contains(head)) heads.Insert(0, head);
                foreach (Transform currentHead in heads)
                {
                    ActorCandidate candidate;
                    if (!byHead.TryGetValue(currentHead.GetInstanceID(), out candidate))
                    {
                        Transform root = animator != null ? animator.transform : CommonAncestor(currentHead, skin.transform);
                        Transform[] face = currentHead.GetComponentsInChildren<Transform>(true);
                        Transform currentNeck = null, currentBody = null;
                        for (Transform t = currentHead.parent; t != null; t = t.parent)
                        {
                            string key = ActorMath.BoneKey(t.name);
                            if (currentNeck == null && (key == "neck" || key == "首")) currentNeck = t;
                            if (currentBody == null && (key == "torso2" || key == "torso3" || key == "chest" ||
                                key == "upperbody" || key == "上半身" || key == "hips" || key == "pelvis")) currentBody = t;
                            if (t == root) break;
                        }
                        candidate = new ActorCandidate {
                            Root = root, Head = currentHead,
                            LeftEye = currentHead == head && left != null ? left : face.FirstOrDefault(t => ActorMath.IsLeftEye(t.name)),
                            RightEye = currentHead == head && right != null ? right : face.FirstOrDefault(t => ActorMath.IsRightEye(t.name)),
                            LeftEyeTip = face.FirstOrDefault(t => ActorMath.BoneKey(t.name) == "lefteyetip"),
                            RightEyeTip = face.FirstOrDefault(t => ActorMath.BoneKey(t.name) == "righteyetip"),
                            Neck = currentNeck != null ? currentNeck : currentHead == head ? neck : null,
                            Body = currentBody != null ? currentBody : currentHead == head && body != null ? body : root,
                            Role = ActorMath.RoleLabel(currentHead.name),
                            Name = ActorMath.RoleLabel(currentHead.name) + " · " + root.name +
                                (heads.Count > 1 ? " · " + (heads.IndexOf(currentHead) + 1) : ""),
                            Key = skin.gameObject.scene.buildIndex + ":" + TransformPath(currentHead)
                        };
                        byHead.Add(currentHead.GetInstanceID(), candidate);
                        actorCandidates.Add(candidate);
                    }
                    candidate.Skins.Add(skin);
                }
            }
            actorCandidates.Sort((a, b) => {
                int priority = ActorMath.RolePriority(a.Head.name).CompareTo(ActorMath.RolePriority(b.Head.name));
                return priority != 0 ? priority : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            actorNotice = actorCandidates.Count == 0 ?
                "当前没有可识别的角色。请进入三维场景，再点刷新。" +
                (actorScanRejected > 0 ? " 有 " + actorScanRejected + " 个网格无法可靠识别头骨。" : "") :
                "可识别 " + actorCandidates.Count + " 个角色；Man / Monster 优先排列，点击即可绑定。";
            Logger.LogInfo("Actor scan: " + actorCandidates.Count + " distinct heads, " + actorScanRejected + " meshes without a recognized head.");
            WriteActorInventory();
        }

        private void WriteActorInventory()
        {
            if (!diagnostics.Value) return;
            try
            {
                List<string> lines = new List<string>();
                foreach (ActorCandidate a in actorCandidates)
                {
                    lines.Add(a.Name + " | head=" + a.Head.name + " | eyes=" +
                        (a.LeftEye == null ? "-" : a.LeftEye.name) + "/" + (a.RightEye == null ? "-" : a.RightEye.name) +
                        " | skins=" + a.Skins.Count + " | readable=" +
                        a.Skins.Count(s => s.sharedMesh != null && s.sharedMesh.isReadable) + " | " + a.Key);
                    lines.Add("  headPosition=" + a.Head.position.ToString("F5") + " | leftEye=" +
                        (a.LeftEye == null ? "-" : a.LeftEye.position.ToString("F5")) + " | rightEye=" +
                        (a.RightEye == null ? "-" : a.RightEye.position.ToString("F5")) + " | leftTip=" +
                        (a.LeftEyeTip == null ? "-" : a.LeftEyeTip.position.ToString("F5")) + " | rightTip=" +
                        (a.RightEyeTip == null ? "-" : a.RightEyeTip.position.ToString("F5")) +
                        " | body=" + a.Body.name);
                    foreach (SkinnedMeshRenderer s in a.Skins)
                        lines.Add("  " + s.name + " | mesh=" + s.sharedMesh.name + " | readable=" +
                            s.sharedMesh.isReadable + " | bones=" + s.bones.Length + " | bounds=" + s.bounds.size);
                }
                lines.AddRange(actorRejectedDetails);
                File.WriteAllLines(Path.Combine(Paths.BepInExRootPath, "NorthwoodVrFixes.actors.txt"), lines.ToArray());
            }
            catch (Exception e) { Logger.LogWarning("Actor inventory write failed: " + e.Message); }
        }

        private void BindActor(ActorCandidate actor)
        {
            if (actor == null || !actor.Valid) { actorNotice = "该角色已失效，请刷新列表。"; return; }
            ClearActorBinding(false);
            boundActor = actor;
            actorProfile = actorProfiles.Items.FirstOrDefault(p => p.Key == actor.Key);
            if (actorProfile == null)
            {
                actorProfile = new ActorCalibration { Key = actor.Key };
                actorProfiles.Items.Add(actorProfile);
            }
            Vector3 savedOffset = new Vector3(actorProfile.X, actorProfile.Y, actorProfile.Z);
            actorOffset = Finite(savedOffset) ? new Vector3(ActorMath.ClampOffset(savedOffset.x),
                ActorMath.ClampOffset(savedOffset.y), ActorMath.ClampOffset(savedOffset.z)) : Vector3.zero;
            actorHeading = YawOnly(actor.Root.rotation);
            actorBodyForward = actor.Body.InverseTransformDirection(actorHeading * Vector3.forward);
            // Bind poses recover anatomical forward even when binding halfway through an animated turn.
            if (actor.Body != actor.Root)
                foreach (SkinnedMeshRenderer skin in actor.Skins)
                {
                    int index = Array.IndexOf(skin.bones, actor.Body);
                    if (index < 0) continue;
                    try
                    {
                        Matrix4x4[] bind = skin.sharedMesh.bindposes;
                        if (index >= bind.Length) continue;
                        Vector3 forward = bind[index].MultiplyVector(
                            skin.transform.InverseTransformDirection(actor.Root.forward));
                        if (!Finite(forward) || forward.sqrMagnitude < 0.0001f) continue;
                        actorBodyForward = forward.normalized;
                        break;
                    }
                    catch (Exception e) { Logger.LogWarning("Body bind pose unavailable: " + e.Message); }
                }
            CalibrateActorEyeSurface(actor);
            BuildActorOcclusion(actor);
            RecenterView(false);
            actorPanelOpen = false;
            actorNotice = "已绑定 " + actor.Name + (actor.LeftEye != null && actor.RightEye != null ? "（眼骨）" : "（估算眼点）");
            if (actorUnsupported > 0) actorNotice += "；部分头部网格无法局部裁剪。";
            Logger.LogInfo("Actor bound: " + actor.Name + "; eye bones=" + (actor.LeftEye != null && actor.RightEye != null) +
                "; hidden=" + actorHidden + "; clipped=" + actorClipped + "; unsupported=" + actorUnsupported);
            actorProfilesDirty = true;
            SaveActorProfiles();
        }

        private Quaternion ActorHeading()
        {
            if (boundActor == null || boundActor.Body == null) return actorHeading;
            Vector3 forward = boundActor.Body.TransformDirection(actorBodyForward);
            forward.y = 0;
            if (forward.sqrMagnitude > 0.0001f) actorHeading = Quaternion.LookRotation(forward.normalized, Vector3.up);
            return actorHeading * Quaternion.Euler(0, actorProfile == null ? 0 : actorProfile.Yaw, 0);
        }

        private Vector3 ActorEyePoint()
        {
            if (boundActor.LeftEye != null && boundActor.RightEye != null)
                return (boundActor.LeftEye.position + boundActor.RightEye.position) * 0.5f +
                    boundActor.Head.TransformVector(actorEyeSurfaceOffset);
            return boundActor.Head.position + boundActor.Head.TransformVector(actorEyeSurfaceOffset);
        }

        private sealed class EyeSurface
        {
            public Vector3 Sum;
            public int Count;
            public float Front = float.NegativeInfinity;
            public void Add(Vector3 point, Vector3 forward)
            {
                Sum += point; Count++;
                Front = Mathf.Max(Front, Vector3.Dot(point, forward));
            }
            public Vector3 Point(Vector3 forward)
            {
                Vector3 mean = Sum / Count;
                return mean + forward * (Front - Vector3.Dot(mean, forward));
            }
        }

        private void CalibrateActorEyeSurface(ActorCandidate actor)
        {
            actorEyeSurfaceOffset = Vector3.zero;
            actorAnchorKind = "HeadEstimate";
            Vector3 forward = actor.Root.forward, up = actor.Root.up;
            foreach (SkinnedMeshRenderer skin in actor.Skins)
            {
                int index = Array.IndexOf(skin.bones, actor.Head);
                if (index < 0) continue;
                try
                {
                    Matrix4x4[] bind = skin.sharedMesh.bindposes;
                    if (index < bind.Length)
                    {
                        forward = actor.Head.TransformDirection(bind[index].MultiplyVector(
                            skin.transform.InverseTransformDirection(actor.Root.forward))).normalized;
                        up = actor.Head.TransformDirection(bind[index].MultiplyVector(
                            skin.transform.InverseTransformDirection(actor.Root.up))).normalized;
                    }
                    break;
                }
                catch (Exception e) { Logger.LogWarning("Eye forward bind pose unavailable: " + e.Message); }
            }
            if (actor.LeftEye == null || actor.RightEye == null)
            {
                float size = actor.Neck == null ? 1f :
                    Mathf.Clamp(Vector3.Distance(actor.Head.position, actor.Neck.position) / 0.12f, 0.3f, 5f);
                actorEyeSurfaceOffset = actor.Head.InverseTransformVector((up * 0.08f + forward * 0.06f) * size);
                return;
            }
            EyeSurface left = new EyeSurface(), right = new EyeSurface();
            Mesh baked = new Mesh();
            try
            {
                foreach (SkinnedMeshRenderer skin in actor.Skins)
                {
                    if (!skin.sharedMesh.isReadable) continue;
                    Transform[] bones = skin.bones;
                    bool[] leftMask = bones.Select(b => b != null && (b == actor.LeftEye || b.IsChildOf(actor.LeftEye))).ToArray();
                    bool[] rightMask = bones.Select(b => b != null && (b == actor.RightEye || b.IsChildOf(actor.RightEye))).ToArray();
                    if (!leftMask.Any(v => v) && !rightMask.Any(v => v)) continue;
                    BoneWeight[] weights = skin.sharedMesh.boneWeights;
                    skin.BakeMesh(baked);
                    Vector3[] vertices = baked.vertices;
                    if (vertices.Length != weights.Length) continue;
                    for (int i = 0; i < weights.Length; i++)
                    {
                        BoneWeight w = weights[i];
                        float lw = HeadWeight(leftMask, w.boneIndex0, w.weight0) +
                            HeadWeight(leftMask, w.boneIndex1, w.weight1) + HeadWeight(leftMask, w.boneIndex2, w.weight2) +
                            HeadWeight(leftMask, w.boneIndex3, w.weight3);
                        float rw = HeadWeight(rightMask, w.boneIndex0, w.weight0) +
                            HeadWeight(rightMask, w.boneIndex1, w.weight1) + HeadWeight(rightMask, w.boneIndex2, w.weight2) +
                            HeadWeight(rightMask, w.boneIndex3, w.weight3);
                        if (lw < 0.35f && rw < 0.35f) continue;
                        Vector3 point = skin.transform.TransformPoint(vertices[i]);
                        if (lw >= 0.35f) left.Add(point, forward);
                        if (rw >= 0.35f) right.Add(point, forward);
                    }
                }
            }
            catch (Exception e) { Logger.LogWarning("Eye surface sampling unavailable: " + e.Message); }
            finally { Destroy(baked); }
            Vector3 correction = forward * 0.035f;
            actorAnchorKind = "EyesForwardOffset";
            if (left.Count >= 3 && right.Count >= 3)
            {
                Vector3 surface = (left.Point(forward) + right.Point(forward)) * 0.5f;
                Vector3 delta = surface - (actor.LeftEye.position + actor.RightEye.position) * 0.5f;
                if (Finite(delta) && delta.magnitude <= 0.15f)
                {
                    correction = delta;
                    actorAnchorKind = "EyesMeshSurface";
                }
            }
            actorEyeSurfaceOffset = actor.Head.InverseTransformVector(correction);
            Logger.LogInfo("Eye surface: " + actor.Name + "; kind=" + actorAnchorKind +
                "; local correction=" + actorEyeSurfaceOffset.ToString("F5") +
                "; sampled vertices=" + left.Count + "/" + right.Count);
        }

        private void ApplyActorPose(ref Vector3 center, ref Quaternion rotation)
        {
            if (boundActor == null || !boundActor.Valid) return;
            Quaternion heading = ActorHeading();
            Vector3 headMove = Quaternion.Inverse(neutralHeadRotation) *
                (UnityEngine.XR.InputTracking.GetLocalPosition(UnityEngine.XR.XRNode.CenterEye) - neutralHeadPosition);
            actorEye = ActorEyePoint();
            center = actorEye + heading * (actorOffset + headMove);
            Quaternion viewAdjust = Quaternion.Euler(mousePitch + controllerPitch, mouseYaw + controllerYaw, 0);
            rotation = heading * viewAdjust * Quaternion.Inverse(neutralHeadRotation) *
                UnityEngine.XR.InputTracking.GetLocalRotation(UnityEngine.XR.XRNode.CenterEye);
        }

        private void MoveActorOffset(Vector3 movement)
        {
            Vector3 local = Quaternion.Inverse(ActorHeading()) * movement * Mathf.Clamp(actorAdjustSpeed.Value, 0.005f, 0.2f);
            actorOffset += local;
            actorOffset = new Vector3(ActorMath.ClampOffset(actorOffset.x),
                ActorMath.ClampOffset(actorOffset.y), ActorMath.ClampOffset(actorOffset.z));
            actorProfilesDirty = true;
        }

        private void ResetActorOffset()
        {
            actorOffset = Vector3.zero;
            actorProfilesDirty = true;
            SaveActorProfiles();
        }

        private void SaveActorProfiles()
        {
            if (actorProfile != null)
            { actorProfile.X = actorOffset.x; actorProfile.Y = actorOffset.y; actorProfile.Z = actorOffset.z; }
            if (!actorProfilesDirty) return;
            try
            {
                string temporary = actorProfileFile + ".tmp";
                File.WriteAllText(temporary, CalibrationJson.Write(actorProfiles));
                if (File.Exists(actorProfileFile)) File.Replace(temporary, actorProfileFile, actorProfileFile + ".bak");
                else File.Move(temporary, actorProfileFile);
                actorProfilesDirty = false;
            }
            catch (Exception e) { Logger.LogWarning("Actor calibration save failed: " + e.Message); }
        }

        private void UpdateActorView()
        {
            RestoreActorRendering();
            actorRenderStack.Clear();
            if (boundActor != null && !boundActor.Valid)
            {
                ClearActorBinding(true);
                actorNotice = "原角色已失效，已切回自由视角；请重新选择。";
            }
            if (actorProfilesDirty && Time.unscaledTime >= nextActorSave)
            {
                nextActorSave = Time.unscaledTime + 1f;
                SaveActorProfiles();
            }
        }

        private void ActorSceneChanged()
        {
            ClearActorBinding(false);
            DisposeActorMeshCache();
            actorCandidates.Clear();
            actorPanelOpen = false;
            actorNotice = "场景已切换，角色绑定已解除。";
        }

        private void ClearActorBinding(bool recenter)
        {
            SaveActorProfiles();
            RestoreActorRendering();
            actorRenderStack.Clear();
            actorOcclusion.Clear();
            bool hadActor = boundActor != null;
            boundActor = null; actorProfile = null;
            actorHidden = actorClipped = actorUnsupported = 0;
            if (hadActor && recenter) RecenterView(false);
        }

        private void DisposeActorMeshCache()
        {
            foreach (Mesh mesh in actorMeshCache.Values) if (mesh != null) Destroy(mesh);
            actorMeshCache.Clear();
        }

        private static bool HeadPartName(string name)
        {
            string n = name.ToLowerInvariant();
            return !n.Contains("body") && !n.Contains("身体") && (n.Contains("hair") || n.Contains("head") || n.Contains("face") ||
                n.Contains("eye") || n.Contains("髪") || n.Contains("頭") || n.Contains("目") || n.Contains("发") || n.Contains("脸"));
        }

        private void BuildActorOcclusion(ActorCandidate actor)
        {
            actorHidden = actorClipped = actorUnsupported = 0;
            Bounds actorBounds = actor.Skins[0].bounds;
            foreach (SkinnedMeshRenderer s in actor.Skins) actorBounds.Encapsulate(s.bounds);
            float bodySize = actorBounds.size.magnitude;
            foreach (SkinnedMeshRenderer skin in actor.Skins)
            {
                Mesh source = skin.sharedMesh;
                if (source == null) continue;
                // A suggestive name alone never justifies hiding a potentially combined body mesh.
                bool compactHeadPart = skin.bounds.size.magnitude < bodySize * 0.55f &&
                    Vector3.Distance(skin.bounds.center, actor.Head.position) < bodySize * 0.4f;
                bool independentHead = compactHeadPart &&
                    (skin.transform.IsChildOf(actor.Head) || HeadPartName(skin.name));
                if (independentHead)
                {
                    actorOcclusion.Add(new OcclusionMesh { Renderer = skin, Hide = true });
                    actorHidden++; continue;
                }
                Transform[] bones = skin.bones;
                bool[] headBones = bones.Select(b => b != null && (b == actor.Head || b.IsChildOf(actor.Head))).ToArray();
                if (!headBones.Any(b => b)) continue;
                if (!source.isReadable) { actorUnsupported++; continue; }
                string cacheKey = skin.GetInstanceID() + ":" + source.GetInstanceID() + ":" + actor.Head.GetInstanceID();
                Mesh cached;
                if (actorMeshCache.TryGetValue(cacheKey, out cached) && cached != null)
                {
                    actorOcclusion.Add(new OcclusionMesh { Renderer = skin, Original = source, Clipped = cached });
                    actorClipped++; continue;
                }
                Mesh clipped = null;
                try
                {
                    BoneWeight[] weights = source.boneWeights;
                    if (weights.Length != source.vertexCount) { actorUnsupported++; continue; }
                    bool[] vertices = new bool[weights.Length];
                    for (int i = 0; i < weights.Length; i++)
                    {
                        BoneWeight w = weights[i];
                        float sum = HeadWeight(headBones, w.boneIndex0, w.weight0) +
                            HeadWeight(headBones, w.boneIndex1, w.weight1) +
                            HeadWeight(headBones, w.boneIndex2, w.weight2) +
                            HeadWeight(headBones, w.boneIndex3, w.weight3);
                        vertices[i] = sum >= 0.5f;
                    }
                    if (!vertices.Any(v => v)) continue;
                    if (vertices.All(v => v) && compactHeadPart)
                    {
                        actorOcclusion.Add(new OcclusionMesh { Renderer = skin, Hide = true });
                        actorHidden++; continue;
                    }
                    if (vertices.All(v => v)) { actorUnsupported++; continue; }
                    clipped = Instantiate(source);
                    actorMeshBuilds++;
                    clipped.name = "NorthwoodHeadless_" + source.name;
                    int removed = 0;
                    for (int sub = 0; sub < source.subMeshCount; sub++)
                    {
                        if (source.GetTopology(sub) != MeshTopology.Triangles) continue;
                        int[] indices = source.GetTriangles(sub);
                        int[] retained = ActorMath.ClipHeadTriangles(indices, vertices);
                        removed += indices.Length - retained.Length;
                        clipped.SetTriangles(retained, sub, false);
                    }
                    if (removed > 0)
                    {
                        actorMeshCache[cacheKey] = clipped;
                        actorOcclusion.Add(new OcclusionMesh { Renderer = skin, Original = source, Clipped = clipped });
                        actorClipped++;
                    }
                    else Destroy(clipped);
                }
                catch (Exception e)
                {
                    if (clipped != null) Destroy(clipped);
                    actorUnsupported++; Logger.LogWarning("Head mesh clipping unsupported: " + e.Message);
                }
            }
            foreach (Renderer renderer in actor.Root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is SkinnedMeshRenderer || !renderer.transform.IsChildOf(actor.Head)) continue;
                actorOcclusion.Add(new OcclusionMesh { Renderer = renderer, Hide = true });
                actorHidden++;
            }
        }

        private static float HeadWeight(bool[] mask, int index, float weight)
        {
            return index >= 0 && index < mask.Length && mask[index] ? weight : 0;
        }

        private void ApplyActorRendering()
        {
            foreach (OcclusionMesh entry in actorOcclusion)
            {
                if (entry.Renderer == null) continue;
                entry.EnabledBefore = entry.Renderer.enabled;
                if (entry.Hide) entry.Renderer.enabled = false;
                else
                {
                    SkinnedMeshRenderer skin = entry.Renderer as SkinnedMeshRenderer;
                    if (skin == null || skin.sharedMesh != entry.Original) continue;
                    skin.sharedMesh = entry.Clipped;
                }
                entry.Applied = true;
            }
            actorRenderingChanged = true;
        }

        private void RestoreActorRendering()
        {
            if (!actorRenderingChanged) return;
            foreach (OcclusionMesh entry in actorOcclusion)
            {
                if (!entry.Applied) continue;
                if (entry.Renderer != null)
                {
                    if (entry.Hide) entry.Renderer.enabled = entry.EnabledBefore;
                    else
                    {
                        SkinnedMeshRenderer skin = entry.Renderer as SkinnedMeshRenderer;
                        if (skin != null && skin.sharedMesh == entry.Clipped) skin.sharedMesh = entry.Original;
                    }
                }
                entry.Applied = false;
            }
            actorRenderingChanged = false;
        }

        private void ActorBeforeCull(Camera camera)
        {
            RestoreActorRendering();
            actorRenderStack.Push(camera);
            if (camera == activeCamera && boundActor != null && actorHideHead.Value && boundActor.Valid)
                ApplyActorRendering();
        }

        private void ActorAfterRender(Camera camera)
        {
            RestoreActorRendering();
            if (actorRenderStack.Count > 0 && actorRenderStack.Peek() == camera) actorRenderStack.Pop();
            else actorRenderStack.Clear();
            if (actorRenderStack.Count > 0 && actorRenderStack.Peek() == activeCamera &&
                boundActor != null && actorHideHead.Value) ApplyActorRendering();
        }

        private void DrawActorPanel()
        {
            GUI.depth = -31000;
            Matrix4x4 previous = GUI.matrix;
            float scale = Mathf.Max(0.5f, Mathf.Min(Screen.width / 1280f, Screen.height / 720f));
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            float width = Screen.width / scale, height = Screen.height / scale;
            GUI.ModalWindow(19873605, new Rect((width - 760) * 0.5f, (height - 560) * 0.5f, 760, 560),
                DrawActorWindow, "角色第一人称视角");
            GUI.matrix = previous;
        }

        private void DrawActorWindow(int id)
        {
            GUIStyle info = new GUIStyle(dialogText) { fontSize = 20, alignment = TextAnchor.MiddleLeft, wordWrap = true };
            GUI.Label(new Rect(20, 30, 720, 45), boundActor == null ? "当前：自由视角" : "当前角色：" + boundActor.Name, info);
            string notice = actorNotice;
            if (boundActor != null && actorUnsupported > 0)
                notice += " 当前角色有 " + actorUnsupported + " 个网格无法安全裁剪，可能仍有遮挡；可微调眼点。";
            GUI.Label(new Rect(20, 75, 720, 65), notice, info);
            actorScroll = GUI.BeginScrollView(new Rect(20, 145, 720, 255), actorScroll,
                new Rect(0, 0, 690, Mathf.Max(255, actorCandidates.Count * 55)));
            for (int i = 0; i < actorCandidates.Count; i++)
            {
                ActorCandidate actor = actorCandidates[i];
                string label = actor.Name + (actor.LeftEye != null && actor.RightEye != null ? "  [眼点]" : "  [头骨估算]");
                if (GUI.Button(new Rect(0, i * 55, 680, 48), label, dialogButton)) BindActor(actor);
            }
            GUI.EndScrollView();
            if (GUI.Button(new Rect(20, 410, 170, 48), "刷新角色", dialogButton)) RefreshActors();
            if (GUI.Button(new Rect(200, 410, 170, 48), "自由视角", dialogButton))
            { ClearActorBinding(true); actorPanelOpen = false; actorNotice = "已切回原游戏当前镜头。"; }
            if (GUI.Button(new Rect(380, 410, 170, 48), "重置眼点", dialogButton))
            { if (boundActor != null) Recenter(); }
            if (GUI.Button(new Rect(560, 410, 170, 48), "关闭", dialogButton)) actorPanelOpen = false;
            GUI.Label(new Rect(20, 470, 720, 55), "按下摇杆切换面板。绑定后关闭面板，用原位移按键微调眼点。", info);
        }

        private void FillActorStatus(RuntimeStatus status)
        {
            status.CharacterView = boundActor != null;
            status.CharacterPanel = actorPanelOpen;
            status.BoundCharacter = boundActor == null ? "" : boundActor.Name;
            status.CharacterAnchor = boundActor == null ? "" : actorAnchorKind;
            status.CharacterNotice = actorNotice;
            status.CharacterCandidates = actorCandidates.Count;
            status.CharacterEye = actorEye; status.CharacterOffset = actorOffset;
            status.HeadMeshesHidden = actorHidden; status.HeadMeshesClipped = actorClipped;
            status.HeadMeshesUnsupported = actorUnsupported;
            status.CharacterScans = actorScans; status.CharacterMeshBuilds = actorMeshBuilds;
        }

        private void DisposeActorView()
        {
            Camera.onPreCull -= ActorBeforeCull;
            Camera.onPostRender -= ActorAfterRender;
            ClearActorBinding(false);
            DisposeActorMeshCache();
        }
    }
}
