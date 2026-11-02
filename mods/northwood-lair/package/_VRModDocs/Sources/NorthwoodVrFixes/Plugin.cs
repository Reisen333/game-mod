using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR;
using Uuvr;
using Uuvr.VrCamera;
using Uuvr.VrUi;

namespace NorthwoodVrFixes
{
    [BepInPlugin(Id, "Northwood VR Fixes", "1.2.3")]
    [BepInDependency("raicuparta.uuvr-legacy")]
    [DefaultExecutionOrder(30000)]
    public sealed partial class Plugin : BaseUnityPlugin
    {
        public const string Id = "local.northwood.vrfixes";
        internal static Plugin Instance;
        internal readonly Interaction Interaction = new Interaction();
        private Harmony harmony;
        private ConfigEntry<bool> poseFix, fullScreen, initialDisplay, headLockedUi;
        private ConfigEntry<int> displayWidth, displayHeight;
        private ConfigEntry<float> uiWidth, uiHeight, uiDistance, mouseSensitivity, pitchLimit;
        private ConfigEntry<KeyCode> lookKey, recenterKey;
        private ConfigEntry<string> cameraChoice, activeCameraLabel;
        private ConfigEntry<Vector3> positionOffset;
        private ConfigEntry<float> yawOffset, pitchOffset;
        private ConfigEntry<CameraMode> cameraMode;
        private ConfigEntry<bool> horizonLock, diagnostics;
        private ConfigEntry<bool> resetCalibration;
        private Camera activeCamera;
        private Component orbitController;
        private object originalOrbitLimit, originalOrbitYMin, originalOrbitYMax, originalOrbitLimitY;
        private Quaternion baseRotation, neutralHeadRotation;
        private Vector3 neutralHeadPosition;
        private float mouseYaw, mousePitch, nextCameraCheck, nextConfigCheck, nextStatus;
        private bool suppressSettings, calibrationDirty, displayDirty, uiDirty = true, hadFocus, reloadingConfig;
        private DateTime lastConfigWrite;
        private ProfileStore profiles;
        private CameraProfile profile;
        private string profileFile, statusFile;
        private readonly Dictionary<int, Quaternion> discoveredRotations = new Dictionary<int, Quaternion>();
        private VrUiManager ui;
        private RenderTexture uiTexture;
        private GameObject uiQuad, modalBlocker;
        private int uiW, uiH, pendingW, pendingH, stableSizeFrames, resizeCount, quitPatchCount;
        private Font dialogFont;
        private GUIStyle dialogText, dialogButton;
        private bool guiReady, dialogNew;
        private Vector3 uiAnchorPosition;
        private Vector3 lastCalibratedCenter, lastEyeOffset;
        private Quaternion uiAnchorRotation = Quaternion.identity;
        private bool uiAnchorValid;

        public enum CameraMode { Auto, OriginalViewpoint, PreserveOrbit }

        private static readonly FieldInfo UiTextureField = AccessTools.Field(typeof(VrUiManager), "_uiTexture");
        private static readonly FieldInfo UiQuadField = AccessTools.Field(typeof(VrUiManager), "_vrUiQuad");
        private static readonly FieldInfo UiMirrorField = AccessTools.Field(typeof(VrUiManager), "_screenMirrorPatchMode");
        private static readonly FieldInfo UiCanvasField = AccessTools.Field(typeof(VrUiManager), "_canvasRedirectPatchMode");
        private static readonly FieldInfo UiOverlayField = AccessTools.Field(typeof(VrUiManager), "_uiOverlayRenderMode");
        private static readonly FieldInfo ChildCameraField = AccessTools.Field(typeof(VrCamera), "_childCamera");

        private void Awake()
        {
            Instance = this;
            initialDisplay = Config.Bind("01 Display", "Apply display on startup", true);
            displayWidth = Config.Bind("01 Display", "Width", 2560, "PC window resolution; independent of VR eye textures.");
            displayHeight = Config.Bind("01 Display", "Height", 1440);
            fullScreen = Config.Bind("01 Display", "Borderless fullscreen", true, "Disable to use a normal resizable window.");
            uiWidth = Config.Bind("02 VR Menu", "Maximum width", 1.6f);
            uiHeight = Config.Bind("02 VR Menu", "Maximum height", 0.9f);
            uiDistance = Config.Bind("02 VR Menu", "Distance", 1.5f);
            headLockedUi = Config.Bind("02 VR Menu", "Follow head", true, "Keep the complete menu in front of the headset.");
            lookKey = Config.Bind("03 Controls", "Toggle mouse look", KeyCode.F8);
            recenterKey = Config.Bind("03 Controls", "Recenter viewpoint", KeyCode.F6);
            mouseSensitivity = Config.Bind("03 Controls", "Mouse sensitivity", 2f);
            pitchLimit = Config.Bind("03 Controls", "Mouse pitch limit", 89f);
            poseFix = Config.Bind("04 Camera", "Enable calibrated pose", true);
            horizonLock = Config.Bind("04 Camera", "Stable horizon", true);
            cameraChoice = Config.Bind("04 Camera", "Camera override", "", "Empty selects automatically. Enter an exact camera name or the profile identifier shown below.");
            activeCameraLabel = Config.Bind("04 Camera", "Current camera", "", "Informational; selected automatically from active game cameras.");
            positionOffset = Config.Bind("05 Current camera calibration", "Position offset", Vector3.zero);
            yawOffset = Config.Bind("05 Current camera calibration", "Yaw offset", 0f);
            pitchOffset = Config.Bind("05 Current camera calibration", "Pitch offset", 0f);
            cameraMode = Config.Bind("05 Current camera calibration", "Mode", CameraMode.Auto);
            resetCalibration = Config.Bind("05 Current camera calibration", "Reset this calibration", false);
            diagnostics = Config.Bind("06 Diagnostics", "Write runtime status", true);
            BindControllers();
            InitializeActorView();
            Config.SaveOnConfigSet = false;
            profileFile = Path.Combine(Paths.ConfigPath, "NorthwoodVrFixes.cameras.json");
            statusFile = Path.Combine(Paths.BepInExRootPath, "NorthwoodVrFixes.status.json");
            LoadProfiles();
            Config.SettingChanged += SettingsChanged;
            harmony = new Harmony(Id);
            PatchQuitCalls();
            harmony.Patch(AccessTools.Method(typeof(VrCamera), "UpdateRelativeMatrix"),
                postfix: new HarmonyMethod(typeof(Hooks), "CameraPostfix"));
            harmony.Patch(AccessTools.Method(typeof(VrCamera), "Awake"),
                postfix: new HarmonyMethod(typeof(Hooks), "CameraDiscovered"));
            harmony.Patch(AccessTools.Method(typeof(VrUiManager), "SetUpUi"),
                postfix: new HarmonyMethod(typeof(Hooks), "UiCreated"));
            Type mirrorType = AccessTools.TypeByName("Uuvr.VrUi.PatchModes.ScreenMirrorPatchMode");
            if (mirrorType != null)
                harmony.Patch(AccessTools.Method(mirrorType, "SetUpTargetTexture"),
                    prefix: new HarmonyMethod(typeof(Hooks), "ResetMirrorBuffer"));
            PatchMouseControllers();
            Type sendMouse = AccessTools.TypeByName("UnityEngine.SendMouseEvents");
            MethodInfo send = sendMouse == null ? null : AccessTools.Method(sendMouse, "DoSendMouseEvents");
            if (send != null) harmony.Patch(send, prefix: new HarmonyMethod(typeof(Hooks), "SendMousePrefix"));
            displayDirty = initialDisplay.Value;
            lastConfigWrite = File.GetLastWriteTimeUtc(Config.ConfigFilePath);
            SceneManager.sceneLoaded += SceneLoaded;
            Logger.LogInfo("Ready. F8: menu/look; F6: recenter; Esc: confirm quit; F5: settings. Quit call sites: " + quitPatchCount);
        }

        private void SettingsChanged(object sender, SettingChangedEventArgs e)
        {
            if (suppressSettings) return;
            string section = e.ChangedSetting.Definition.Section;
            if (section == "01 Display") displayDirty = true;
            if (section == "02 VR Menu") uiDirty = true;
            if (section == "05 Current camera calibration" && !reloadingConfig) calibrationDirty = true;
            if (e.ChangedSetting == cameraChoice) nextCameraCheck = 0f;
        }

        private void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ActorSceneChanged();
            nextCameraCheck = 0f;
            uiDirty = true;
            uiAnchorValid = false;
            Interaction.Release();
        }

        private void Update()
        {
            UpdateActorView();
            bool focused = Application.isFocused;
            if (hadFocus && !focused) Interaction.Release();
            hadFocus = focused;
            if (focused && Input.GetKeyDown(KeyCode.Escape)) HandleEscape();
            if (focused && Input.GetKeyDown(KeyCode.F5)) { Interaction.Release(); uiAnchorValid = false; }
            if (SettingsOpen() || actorPanelOpen) Interaction.Release();
            if (focused && Input.GetKeyDown(lookKey.Value) && !SettingsOpen() && !actorPanelOpen)
            {
                Interaction.ToggleLook();
                uiAnchorValid = false;
                Logger.LogInfo("Interaction: " + (Interaction.Looking ? "LOOK" : "MENU"));
            }
            if (focused && Input.GetKeyDown(recenterKey.Value)) Recenter();
            if (Interaction.Looking && focused && !Interaction.ConfirmOpen && !SettingsOpen() && !actorPanelOpen)
            {
                mouseYaw = ViewMath.WrapYaw(mouseYaw + Input.GetAxisRaw("Mouse X") * mouseSensitivity.Value);
                mousePitch = ViewMath.ClampPitch(mousePitch - Input.GetAxisRaw("Mouse Y") * mouseSensitivity.Value,
                    Mathf.Clamp(pitchLimit.Value, 1f, 89.9f));
            }
            if (Time.unscaledTime >= nextCameraCheck)
            {
                nextCameraCheck = Time.unscaledTime + 0.25f;
                SelectCamera();
            }
            PollControllers();
            if (displayDirty)
            {
                displayDirty = false;
                Screen.SetResolution(Mathf.Clamp(displayWidth.Value, 640, 7680),
                    Mathf.Clamp(displayHeight.Value, 480, 4320),
                    fullScreen.Value ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed);
                Logger.LogInfo("Requested PC display " + displayWidth.Value + "x" + displayHeight.Value +
                    (fullScreen.Value ? " borderless" : " windowed"));
            }
            if (calibrationDirty) SaveCurrentCalibration();
            if (Time.unscaledTime >= nextConfigCheck)
            {
                nextConfigCheck = Time.unscaledTime + 2f;
                DateTime stamp = File.GetLastWriteTimeUtc(Config.ConfigFilePath);
                if (stamp != lastConfigWrite) { lastConfigWrite = stamp; ReloadSettings(); }
            }
            if (diagnostics.Value && Time.unscaledTime >= nextStatus)
            {
                nextStatus = Time.unscaledTime + 3f;
                WriteStatus();
            }
        }

        private void ReloadSettings()
        {
            Vector3 livePosition = positionOffset.Value;
            float liveYaw = yawOffset.Value, livePitch = pitchOffset.Value;
            CameraMode liveMode = cameraMode.Value;
            reloadingConfig = true;
            try { Config.Reload(); }
            catch (Exception e) { Logger.LogWarning("Settings reload failed: " + e.Message); }
            finally
            {
                suppressSettings = true;
                positionOffset.Value = livePosition; yawOffset.Value = liveYaw; pitchOffset.Value = livePitch;
                cameraMode.Value = liveMode;
                if (profile != null) activeCameraLabel.Value = profile.Key;
                suppressSettings = false; reloadingConfig = false;
            }
        }

        private void LateUpdate()
        {
            RefreshUi();
            bool lockMouse = Application.isFocused && Interaction.Looking && !Interaction.ConfirmOpen && !SettingsOpen() && !actorPanelOpen;
            Cursor.lockState = lockMouse ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !lockMouse;
            if (modalBlocker != null) modalBlocker.SetActive(Interaction.ConfirmOpen || actorPanelOpen);
            UpdateControllerPointer();
        }

        private bool SettingsOpen()
        {
            try
            {
                Type type = AccessTools.TypeByName("ConfigManager.UI.UIManager");
                if (type == null) return false;
                object manager = AccessTools.Property(type, "Instance").GetValue(null, null);
                return manager != null && (bool)AccessTools.Property(type, "ShowMenu").GetValue(manager, null);
            }
            catch { return false; }
        }

        internal void HandleEscape()
        {
            actorPanelOpen = false;
            bool wasOpen = Interaction.ConfirmOpen;
            Interaction.Escape(Time.frameCount);
            if (wasOpen != Interaction.ConfirmOpen)
            {
                uiAnchorValid = false;
                EnsureModalBlocker();
                modalBlocker.SetActive(Interaction.ConfirmOpen);
                dialogNew = Interaction.ConfirmOpen;
                Logger.LogInfo("Quit confirmation: " + (Interaction.ConfirmOpen ? "OPEN" : "CANCEL"));
            }
        }

        private void EnsureModalBlocker()
        {
            if (modalBlocker != null) return;
            modalBlocker = new GameObject("NorthwoodQuitInputBlocker", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            DontDestroyOnLoad(modalBlocker);
            Canvas canvas = modalBlocker.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32767;
            GameObject image = new GameObject("InputShield", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            image.transform.SetParent(modalBlocker.transform, false);
            RectTransform rect = image.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            image.GetComponent<Image>().color = Color.clear;
            image.GetComponent<Image>().raycastTarget = true;
        }

        private void OnGUI()
        {
            if (!Interaction.ConfirmOpen && actorPanelOpen) { EnsureGuiStyles(); DrawActorPanel(); return; }
            if (!Interaction.ConfirmOpen) return;
            EnsureGuiStyles();
            GUI.depth = -32000;
            Matrix4x4 old = GUI.matrix;
            float scale = Mathf.Max(0.5f, Mathf.Min(Screen.width / 1280f, Screen.height / 720f));
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float width = Screen.width / scale, height = Screen.height / scale;
            Color saved = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.7f);
            GUI.DrawTexture(new Rect(0, 0, width, height), Texture2D.whiteTexture);
            GUI.color = saved;
            GUI.ModalWindow(19873604, new Rect((width - 560f) / 2f, (height - 250f) / 2f, 560f, 250f),
                DrawQuitDialog, "退出确认");
            GUI.matrix = old;
        }

        private void EnsureGuiStyles()
        {
            if (!guiReady)
            {
                dialogFont = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "Arial" }, 24);
                dialogText = new GUIStyle(GUI.skin.label) { font = dialogFont, fontSize = 24, alignment = TextAnchor.MiddleCenter };
                dialogButton = new GUIStyle(GUI.skin.button) { font = dialogFont, fontSize = 23 };
                guiReady = true;
            }
        }

        private void DrawQuitDialog(int id)
        {
            GUI.Label(new Rect(25, 35, 510, 65), "确定要退出游戏吗？", dialogText);
            GUI.Label(new Rect(25, 95, 510, 35), "再次按 Esc 可以返回游戏", dialogText);
            GUI.SetNextControlName("Continue");
            if (GUI.Button(new Rect(45, 160, 215, 55), "继续游戏", dialogButton))
            {
                Interaction.Cancel();
                Logger.LogInfo("Quit confirmation: CANCEL by button");
            }
            if (GUI.Button(new Rect(300, 160, 215, 55), "退出游戏", dialogButton))
            {
                Logger.LogInfo("Quit confirmation: explicitly confirmed by button");
                Application.Quit();
            }
            if (dialogNew) { GUI.FocusControl("Continue"); dialogNew = false; }
        }

        private void PatchQuitCalls()
        {
            Assembly game = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Assembly-CSharp");
            MethodInfo quit = AccessTools.Method(typeof(Application), "Quit", Type.EmptyTypes);
            foreach (Type type in game.GetTypes())
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.ContainsGenericParameters || method.IsAbstract) continue;
                MethodBody body;
                try { body = method.GetMethodBody(); } catch { continue; }
                if (body == null) continue;
                byte[] bytes = body.GetILAsByteArray();
                bool callsQuit = false;
                for (int i = 0; i + 4 < bytes.Length; i++)
                {
                    if (bytes[i] != 0x28 && bytes[i] != 0x6f) continue;
                    try
                    {
                        MethodBase target = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, i + 1));
                        if (target == quit) { callsQuit = true; break; }
                    }
                    catch { }
                }
                if (!callsQuit) continue;
                harmony.Patch(method, transpiler: new HarmonyMethod(typeof(Hooks), "QuitTranspiler"));
                quitPatchCount++;
            }
            if (quitPatchCount == 0) throw new InvalidOperationException("No managed quit call sites found; refusing to claim Esc protection.");
        }

        private void PatchMouseControllers()
        {
            foreach (string name in new[] { "MouseLook", "UltimateOrbitCamera", "ten_demo_ControllerFreeCamera", "FreeCamera" })
            {
                Type type = AccessTools.TypeByName(name);
                if (type == null) continue;
                foreach (string methodName in new[] { "Update", "LateUpdate", "Move" })
                {
                    MethodInfo method = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly).FirstOrDefault(m => m.Name == methodName);
                    if (method == null || method.IsStatic || method.DeclaringType != type) continue;
                    harmony.Patch(method, transpiler: new HarmonyMethod(typeof(Hooks), "MouseTranspiler"));
                }
            }
        }

        internal bool Related(object controller)
        {
            Component c = controller as Component;
            if (c == null || activeCamera == null) return false;
            Transform a = activeCamera.transform, b = c.transform;
            return a == b || a.IsChildOf(b) || b.IsChildOf(a);
        }

        private static bool Usable(Camera camera)
        {
            return camera != null && camera.enabled && camera.gameObject.activeInHierarchy &&
                camera.targetTexture == null && camera.cullingMask != 0 &&
                !ViewMath.IsUiCameraName(camera.name) && !VrCamera.IgnoredCameras.Contains(camera);
        }

        private bool PreserveOrbit()
        {
            return cameraMode.Value == CameraMode.PreserveOrbit ||
                (cameraMode.Value == CameraMode.Auto && orbitController != null &&
                activeCamera != null && !ViewMath.IsFirstPersonCameraName(activeCamera.name));
        }

        private void SelectCamera()
        {
            Camera selected = null;
            float bestScore = float.NegativeInfinity;
            foreach (Camera c in VrCamera.VrCameras)
            {
                if (!Usable(c)) continue;
                string key = CameraKey(c);
                bool chosen = cameraChoice.Value.Length > 0 && (c.name == cameraChoice.Value || key == cameraChoice.Value);
                float score = c.depth * 100f;
                if ((c.cullingMask & 1) != 0) score += 5f;
                if (c.name.IndexOf("POV", StringComparison.OrdinalIgnoreCase) >= 0) score += 10f;
                if (chosen) score += 100000f;
                if (score > bestScore) { selected = c; bestScore = score; }
            }
            if (selected == activeCamera) return;
            SwitchCamera(selected);
        }

        private static string CameraKey(Camera camera)
        {
            List<string> pieces = new List<string>();
            for (Transform t = camera.transform; t != null; t = t.parent)
                pieces.Add(t.name + "#" + t.GetSiblingIndex());
            pieces.Reverse();
            return camera.gameObject.scene.buildIndex + ":" + string.Join("/", pieces.ToArray());
        }

        private void SwitchCamera(Camera selected)
        {
            if (calibrationDirty) SaveCurrentCalibration();
            RestoreOrbit();
            activeCamera = selected;
            profile = null;
            Interaction.Release();
            if (selected == null) return;
            string key = CameraKey(selected);
            profile = profiles.Items.FirstOrDefault(p => p.Key == key);
            if (profile == null) { profile = new CameraProfile { Key = key }; profiles.Items.Add(profile); }
            suppressSettings = true;
            activeCameraLabel.Value = key;
            positionOffset.Value = new Vector3(profile.X, profile.Y, profile.Z);
            yawOffset.Value = profile.Yaw; pitchOffset.Value = profile.Pitch;
            cameraMode.Value = (CameraMode)profile.Mode;
            suppressSettings = false;
            orbitController = selected.GetComponentsInParent<MonoBehaviour>(true)
                .FirstOrDefault(c => c != null && c.GetType().Name == "UltimateOrbitCamera");
            if (orbitController != null)
            {
                FieldInfo limit = AccessTools.Field(orbitController.GetType(), "limitX");
                if (limit != null) { originalOrbitLimit = limit.GetValue(orbitController); limit.SetValue(orbitController, false); }
                originalOrbitYMin = ReplaceOrbitField("yMinLimit", -Mathf.Clamp(pitchLimit.Value, 1f, 89.9f));
                originalOrbitYMax = ReplaceOrbitField("yMaxLimit", Mathf.Clamp(pitchLimit.Value, 1f, 89.9f));
                originalOrbitLimitY = ReplaceOrbitField("limitY", true);
            }
            if (boundActor == null) Recenter();
            Logger.LogInfo("Active camera: " + key + "; original orbit: " + (orbitController != null));
        }

        private void RestoreOrbit()
        {
            if (orbitController != null && originalOrbitLimit != null)
            {
                FieldInfo field = AccessTools.Field(orbitController.GetType(), "limitX");
                if (field != null) field.SetValue(orbitController, originalOrbitLimit);
                if (originalOrbitYMin != null) ReplaceOrbitField("yMinLimit", originalOrbitYMin);
                if (originalOrbitYMax != null) ReplaceOrbitField("yMaxLimit", originalOrbitYMax);
                if (originalOrbitLimitY != null) ReplaceOrbitField("limitY", originalOrbitLimitY);
            }
            orbitController = null; originalOrbitLimit = null;
            originalOrbitYMin = originalOrbitYMax = originalOrbitLimitY = null;
        }

        private object ReplaceOrbitField(string name, object value)
        {
            FieldInfo field = AccessTools.Field(orbitController.GetType(), name);
            if (field == null) return null;
            object old = field.GetValue(orbitController);
            field.SetValue(orbitController, value);
            return old;
        }

        private static Quaternion YawOnly(Quaternion q)
        {
            Vector3 f = q * Vector3.forward;
            f.y = 0;
            return f.sqrMagnitude < 0.0001f ? Quaternion.identity : Quaternion.LookRotation(f.normalized, Vector3.up);
        }

        private void Recenter()
        {
            RecenterView(true);
        }

        private void RecenterView(bool resetActorOffset)
        {
            if (boundActor != null && resetActorOffset) ResetActorOffset();
            ResetControllerView();
            mouseYaw = mousePitch = 0f;
            neutralHeadPosition = InputTracking.GetLocalPosition(XRNode.CenterEye);
            neutralHeadRotation = YawOnly(InputTracking.GetLocalRotation(XRNode.CenterEye));
            if (activeCamera != null)
            {
                Quaternion original;
                if (!discoveredRotations.TryGetValue(activeCamera.GetInstanceID(), out original)) original = activeCamera.transform.rotation;
                baseRotation = horizonLock.Value ? YawOnly(original) : original;
            }
            uiAnchorValid = false;
            Logger.LogInfo("View recentered; headset origin removed. Current camera: " + (activeCamera == null ? "(none)" : activeCamera.name));
        }

        internal void CameraDiscovered(VrCamera camera)
        {
            if (camera.ParentCamera != null)
                discoveredRotations[camera.ParentCamera.GetInstanceID()] = camera.ParentCamera.transform.rotation;
        }

        internal void ApplyPose(VrCamera vrCamera)
        {
            Camera parent = vrCamera.ParentCamera;
            if (!poseFix.Value || parent == null || !Usable(parent)) return;
            if (!Usable(activeCamera)) SelectCamera();
            if (parent != activeCamera || profile == null) return;
            if (ModConfiguration.Instance.CameraTracking.Value != ModConfiguration.CameraTrackingMode.RelativeMatrix) return;
            Camera child = ChildCameraField.GetValue(vrCamera) as Camera;
            if (child == null) return;
            bool orbit = PreserveOrbit();
            Quaternion body = orbit ? parent.transform.rotation : baseRotation;
            body *= Quaternion.Euler(pitchOffset.Value, yawOffset.Value, 0f);
            if (!orbit) body *= Quaternion.Euler(mousePitch, mouseYaw, 0f);
            body *= Quaternion.Euler(controllerPitch, controllerYaw, 0f);
            Quaternion head = InputTracking.GetLocalRotation(XRNode.CenterEye);
            Quaternion worldRotation = body * Quaternion.Inverse(neutralHeadRotation) * head;
            Vector3 displacement = Quaternion.Inverse(neutralHeadRotation) *
                (InputTracking.GetLocalPosition(XRNode.CenterEye) - neutralHeadPosition);
            Vector3 center = parent.transform.position + YawOnly(body) * (positionOffset.Value + displacement) + controllerTranslation;
            ApplyActorPose(ref center, ref worldRotation);
            lastViewRotation = worldRotation;

            // Native eye matrices supply the headset IPD. Build views from one calibrated center pose.
            Matrix4x4 rawLeft = child.GetStereoViewMatrix(Camera.StereoscopicEye.Left).inverse;
            Matrix4x4 rawRight = child.GetStereoViewMatrix(Camera.StereoscopicEye.Right).inverse;
            Vector3 rawL = rawLeft.GetColumn(3), rawR = rawRight.GetColumn(3);
            Vector3 rawCenter = (rawL + rawR) * 0.5f;
            Quaternion rawRotation = Quaternion.LookRotation(-rawLeft.GetColumn(2), rawLeft.GetColumn(1));
            Vector3 leftOffset = Quaternion.Inverse(rawRotation) * (rawL - rawCenter);
            Vector3 rightOffset = Quaternion.Inverse(rawRotation) * (rawR - rawCenter);
            if (!Finite(center) || !Finite(leftOffset) || !Finite(rightOffset)) return;
            lastCalibratedCenter = center;
            lastEyeOffset = rightOffset - leftOffset;
            Matrix4x4 left = View(center + worldRotation * leftOffset, worldRotation);
            Matrix4x4 right = View(center + worldRotation * rightOffset, worldRotation);
            parent.SetStereoViewMatrix(Camera.StereoscopicEye.Left, left);
            parent.SetStereoViewMatrix(Camera.StereoscopicEye.Right, right);
            parent.worldToCameraMatrix = parent.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? right : left;
        }

        private static Matrix4x4 View(Vector3 position, Quaternion rotation)
        {
            return Matrix4x4.Scale(new Vector3(1, 1, -1)) *
                Matrix4x4.TRS(position, rotation, Vector3.one).inverse;
        }

        private static bool Finite(Vector3 v)
        {
            return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
                float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
        }

        private void RefreshUi()
        {
            if (ui == null) return;
            uiQuad = UiQuadField.GetValue(ui) as GameObject;
            if (uiQuad == null) return;
            uiTexture = UiTextureField.GetValue(ui) as RenderTexture;
            if (Screen.width != pendingW || Screen.height != pendingH)
            {
                pendingW = Screen.width; pendingH = Screen.height; stableSizeFrames = 0;
            }
            else stableSizeFrames++;
            if (stableSizeFrames >= 2 && pendingW > 0 && pendingH > 0 &&
                (uiTexture == null || uiTexture.width != pendingW || uiTexture.height != pendingH))
                ResizeUi(pendingW, pendingH);
            if (uiTexture == null) return;
            uiW = uiTexture.width; uiH = uiTexture.height;
            float[] fit = ViewMath.Fit(uiW, uiH, Mathf.Clamp(uiWidth.Value, 0.3f, 4f),
                Mathf.Clamp(uiHeight.Value, 0.2f, 3f));
            bool mirror = ModConfiguration.Instance.PreferredUiPatchMode.Value == ModConfiguration.UiPatchMode.Mirror;
            uiQuad.transform.localScale = new Vector3(fit[0], mirror ? -fit[1] : fit[1], 1f);
            Component overlay = UiOverlayField.GetValue(ui) as Component;
            Camera overlayCamera = overlay == null ? null : overlay.GetComponentInChildren<Camera>();
            if (overlayCamera != null && (headLockedUi.Value || !uiAnchorValid))
            {
                Matrix4x4 left = overlayCamera.GetStereoViewMatrix(Camera.StereoscopicEye.Left).inverse;
                Matrix4x4 right = overlayCamera.GetStereoViewMatrix(Camera.StereoscopicEye.Right).inverse;
                uiAnchorPosition = ((Vector3)left.GetColumn(3) + (Vector3)right.GetColumn(3)) * 0.5f;
                uiAnchorRotation = Quaternion.LookRotation(-left.GetColumn(2), left.GetColumn(1));
                uiAnchorValid = true;
            }
            if (uiAnchorValid)
            {
                uiQuad.transform.position = uiAnchorPosition + uiAnchorRotation * Vector3.forward *
                    Mathf.Clamp(uiDistance.Value, 0.5f, 5f);
                uiQuad.transform.rotation = uiAnchorRotation;
            }
            if (uiDirty)
            {
                uiDirty = false;
                Logger.LogInfo("VR UI: texture " + uiW + "x" + uiH + ", bounded plane " + fit[0] + "x" + fit[1]);
            }
        }

        private void ResizeUi(int width, int height)
        {
            object mirror = UiMirrorField.GetValue(ui);
            object canvas = UiCanvasField.GetValue(ui);
            if (mirror != null) AccessTools.Method(mirror.GetType(), "Reset").Invoke(mirror, null);
            RenderTexture old = uiTexture;
            RenderTexture texture = new RenderTexture(width, height, 16, RenderTextureFormat.ARGB32);
            texture.name = "NorthwoodVR UI"; texture.Create();
            UiTextureField.SetValue(ui, texture);
            uiQuad.GetComponent<Renderer>().material.mainTexture = texture;
            if (mirror != null) AccessTools.Method(mirror.GetType(), "SetUpTargetTexture").Invoke(mirror, new object[] { texture });
            if (canvas != null) AccessTools.Method(canvas.GetType(), "SetUpTargetTexture").Invoke(canvas, new object[] { texture });
            uiTexture = texture;
            if (old != null) { old.Release(); Destroy(old); }
            resizeCount++; uiDirty = true;
        }

        private void LoadProfiles()
        {
            try { profiles = File.Exists(profileFile) ? CalibrationJson.Read<ProfileStore>(File.ReadAllText(profileFile)) : new ProfileStore(); }
            catch (Exception e) { Logger.LogWarning("Camera profiles unreadable; keeping file and using defaults: " + e.Message); }
            if (profiles == null) profiles = new ProfileStore();
            if (profiles.Items == null) profiles.Items = new List<CameraProfile>();
        }

        private void SaveCurrentCalibration()
        {
            calibrationDirty = false;
            if (profile == null) return;
            if (resetCalibration.Value)
            {
                suppressSettings = true;
                positionOffset.Value = Vector3.zero; yawOffset.Value = pitchOffset.Value = 0f;
                cameraMode.Value = CameraMode.Auto; resetCalibration.Value = false;
                suppressSettings = false;
                Recenter();
            }
            Vector3 offset = positionOffset.Value;
            profile.X = offset.x; profile.Y = offset.y; profile.Z = offset.z;
            profile.Yaw = yawOffset.Value; profile.Pitch = pitchOffset.Value; profile.Mode = (int)cameraMode.Value;
            string temporary = profileFile + ".tmp";
            File.WriteAllText(temporary, CalibrationJson.Write(profiles));
            if (File.Exists(profileFile)) File.Replace(temporary, profileFile, profileFile + ".bak");
            else File.Move(temporary, profileFile);
        }

        private void WriteStatus()
        {
            try
            {
                RuntimeStatus status = new RuntimeStatus {
                    Frame = Time.frameCount, Scene = SceneManager.GetActiveScene().buildIndex,
                    Camera = activeCamera == null ? "" : activeCamera.name,
                    Profile = profile == null ? "" : profile.Key,
                    Looking = Interaction.Looking, Confirmation = Interaction.ConfirmOpen,
                    Focused = Application.isFocused, CursorLocked = Cursor.lockState == CursorLockMode.Locked,
                    ScreenWidth = Screen.width, ScreenHeight = Screen.height, Fullscreen = Screen.fullScreen,
                    TextureWidth = uiW, TextureHeight = uiH, UiResizes = resizeCount,
                    QuitCallSites = quitPatchCount, PoseFix = poseFix.Value, Time = DateTime.Now.ToString("o")
                };
                status.HeadPosition = InputTracking.GetLocalPosition(XRNode.CenterEye);
                status.CameraPosition = activeCamera == null ? Vector3.zero : activeCamera.transform.position;
                status.CalibratedPosition = lastCalibratedCenter;
                status.EyeSeparation = lastEyeOffset;
                status.PreserveOrbit = PreserveOrbit();
                FillControllerStatus(status);
                FillActorStatus(status);
                status.OwnedUiTextures = Resources.FindObjectsOfTypeAll<RenderTexture>().Count(t => t.name == "NorthwoodVR UI");
                File.WriteAllText(statusFile, JsonUtility.ToJson(status, true));
            }
            catch (Exception e) { Logger.LogWarning("Status write failed: " + e.Message); }
        }

        private void OnDestroy()
        {
            DisposeActorView();
            SceneManager.sceneLoaded -= SceneLoaded;
            Config.SettingChanged -= SettingsChanged;
            if (calibrationDirty) SaveCurrentCalibration();
            RestoreOrbit();
            if (harmony != null) harmony.UnpatchSelf();
            DisposeControllers();
            if (modalBlocker != null) Destroy(modalBlocker);
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            if (Instance == this) Instance = null;
        }

        internal static class Hooks
        {
            public static void QuitBridge()
            {
                Plugin p = Instance;
                if (p != null && p.Interaction.InterceptQuit(Input.GetKeyDown(KeyCode.Escape)))
                {
                    if (Input.GetKeyDown(KeyCode.Escape)) p.HandleEscape();
                    return;
                }
                Application.Quit();
            }

            public static IEnumerable<CodeInstruction> QuitTranspiler(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo quit = AccessTools.Method(typeof(Application), "Quit", Type.EmptyTypes);
                foreach (CodeInstruction instruction in instructions)
                {
                    if (instruction.Calls(quit))
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = AccessTools.Method(typeof(Hooks), "QuitBridge");
                    }
                    yield return instruction;
                }
            }

            public static float CameraAxis(string axis, object controller)
            {
                Plugin p = Instance;
                if (p != null && p.Related(controller) && axis.StartsWith("Mouse", StringComparison.Ordinal))
                {
                    bool orbit = p.PreserveOrbit();
                    if (!p.Interaction.Looking || p.Interaction.ConfirmOpen || !Application.isFocused ||
                        (p.poseFix.Value && !orbit)) return 0f;
                }
                return Input.GetAxis(axis);
            }

            public static IEnumerable<CodeInstruction> MouseTranspiler(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo getAxis = AccessTools.Method(typeof(Input), "GetAxis", new[] { typeof(string) });
                foreach (CodeInstruction instruction in instructions)
                {
                    if (instruction.Calls(getAxis))
                    {
                        CodeInstruction instance = new CodeInstruction(OpCodes.Ldarg_0);
                        instance.labels.AddRange(instruction.labels); instruction.labels.Clear();
                        instance.blocks.AddRange(instruction.blocks); instruction.blocks.Clear();
                        yield return instance;
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = AccessTools.Method(typeof(Hooks), "CameraAxis");
                    }
                    yield return instruction;
                }
            }

            public static void CameraPostfix(VrCamera __instance)
            {
                if (Instance != null) Instance.ApplyPose(__instance);
            }
            public static void CameraDiscovered(VrCamera __instance)
            {
                if (Instance != null) Instance.CameraDiscovered(__instance);
            }
            public static void UiCreated(VrUiManager __instance)
            {
                if (Instance != null) { Instance.ui = __instance; Instance.uiDirty = true; Instance.uiAnchorValid = false; }
            }
            public static void ResetMirrorBuffer(object __instance)
            {
                AccessTools.Method(__instance.GetType(), "Reset").Invoke(__instance, null);
            }
            public static bool SendMousePrefix()
            {
                return Instance == null || (!Instance.Interaction.ConfirmOpen && !Instance.actorPanelOpen);
            }
        }

        [Serializable] public sealed class ProfileStore { public List<CameraProfile> Items = new List<CameraProfile>(); }
        [Serializable] public sealed class CameraProfile
        {
            public string Key;
            public float X, Y, Z, Yaw, Pitch;
            public int Mode;
        }
        [Serializable] public sealed class RuntimeStatus
        {
            public int Frame, Scene, ScreenWidth, ScreenHeight, TextureWidth, TextureHeight, UiResizes, QuitCallSites;
            public string Camera, Profile, Time;
            public bool Looking, Confirmation, Focused, CursorLocked, Fullscreen, PoseFix;
            public bool PreserveOrbit;
            public int OwnedUiTextures;
            public Vector3 HeadPosition, CameraPosition, CalibratedPosition, EyeSeparation;
            public bool LeftController, RightController, ControllerGrip, ControllerPointer;
            public int ActiveHand, ControllerClicks, ControllerTriggerPresses, NativeMouseError;
            public bool NativeWindowFound, ForegroundGame, MouseMoveSucceeded;
            public Vector2 ControllerPointerPixels, UnityMousePixels;
            public Vector3 ControllerOffset;
            public bool CharacterView, CharacterPanel;
            public string BoundCharacter, CharacterAnchor, CharacterNotice;
            public int CharacterCandidates, HeadMeshesHidden, HeadMeshesClipped, HeadMeshesUnsupported;
            public int CharacterScans, CharacterMeshBuilds;
            public Vector3 CharacterEye, CharacterOffset;
        }
    }
}
