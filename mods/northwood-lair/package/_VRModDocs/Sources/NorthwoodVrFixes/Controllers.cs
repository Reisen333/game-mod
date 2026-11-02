using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Uuvr;
using Valve.VR;
using Debug = UnityEngine.Debug;

namespace NorthwoodVrFixes
{
    public sealed partial class Plugin
    {
        private ConfigEntry<bool> controllerEnabled, controllerFocus, invertGripPitch;
        private ConfigEntry<float> stickSpeed, stickDeadzone, gripSensitivity, pointerSmoothing;
        private CVRSystem vrSystem;
        private CVRCompositor vrCompositor;
        private CVROverlay vrOverlay;
        private uint vrToken;
        private float nextVrAttach;
        private readonly TrackedDevicePose_t[] devicePoses = new TrackedDevicePose_t[64];
        private readonly ControllerBrain controllerBrain = new ControllerBrain();
        private readonly PointerLatch controllerPointerLatch = new PointerLatch();
        private readonly HandRuntime[] hands = { new HandRuntime(), new HandRuntime() };
        private ControllerFrame controllerFrame;
        private Vector3 controllerTranslation;
        private float controllerYaw, controllerPitch;
        private Quaternion lastViewRotation = Quaternion.identity, gripStart = Quaternion.identity;
        private float gripLastYaw, gripLastPitch;
        private bool gripTracking, pointerValid, leftMouseOwned, pendingClickDown, pendingClickUp, controllerInputAllowed;
        private LineRenderer pointerLine;
        private Material pointerMaterial;
        private Vector2 pointerPixel;
        private bool pointerPixelValid;
        private int controllerClicks, controllerTriggerPresses;
        private float clickRequestUntil;
        private bool oldRunInBackground;

        private sealed class HandRuntime
        {
            public uint Index = uint.MaxValue;
            public HandSample Sample;
            public bool PoseValid, AnchorValid;
            public Vector3 Position, ActivityPosition;
            public Quaternion Rotation = Quaternion.identity, ActivityRotation = Quaternion.identity;
        }

        private void BindControllers()
        {
            controllerEnabled = Config.Bind("07 PICO Controllers", "Enable controllers", true);
            stickSpeed = Config.Bind("07 PICO Controllers", "Camera translation speed", 0.45f,
                "Metres per second. Stick up/down raises/lowers the camera; left/right strafes relative to the view.");
            stickDeadzone = Config.Bind("07 PICO Controllers", "Stick deadzone", 0.2f);
            gripSensitivity = Config.Bind("07 PICO Controllers", "Grip rotation sensitivity", 1f);
            pointerSmoothing = Config.Bind("07 PICO Controllers", "Pointer smoothing", 20f);
            controllerFocus = Config.Bind("07 PICO Controllers", "Focus game on confirm press", true,
                "Only while this game owns the SteamVR scene and the dashboard is closed.");
            invertGripPitch = Config.Bind("07 PICO Controllers", "Invert grip pitch", true,
                "Reverse vertical camera rotation while holding grip.");
            oldRunInBackground = Application.runInBackground;
            Application.runInBackground = true;
        }

        private bool AttachVr()
        {
            uint token = OpenVR.GetInitToken();
            if (vrSystem != null && token == vrToken) return true;
            if (Time.unscaledTime < nextVrAttach) return false;
            nextVrAttach = Time.unscaledTime + 2f;
            vrSystem = null; vrCompositor = null; vrOverlay = null;
            EVRInitError error = EVRInitError.None;
            IntPtr system = OpenVR.GetGenericInterface("FnTable:" + OpenVR.IVRSystem_Version, ref error);
            if (error != EVRInitError.None || system == IntPtr.Zero) return false;
            vrSystem = new CVRSystem(system);
            IntPtr compositor = OpenVR.GetGenericInterface("FnTable:" + OpenVR.IVRCompositor_Version, ref error);
            if (compositor != IntPtr.Zero && error == EVRInitError.None) vrCompositor = new CVRCompositor(compositor);
            IntPtr overlay = OpenVR.GetGenericInterface("FnTable:" + OpenVR.IVROverlay_Version, ref error);
            if (overlay != IntPtr.Zero && error == EVRInitError.None) vrOverlay = new CVROverlay(overlay);
            vrToken = token;
            Logger.LogInfo("PICO input attached to the existing OpenVR session; no runtime reinitialization.");
            return true;
        }

        private void PollControllers()
        {
            controllerInputAllowed = false;
            if (!controllerEnabled.Value || !AttachVr())
            {
                ReleaseControllerMouse();
                if (pointerLine != null) pointerLine.enabled = false;
                return;
            }
            bool sceneOwned = vrCompositor != null &&
                vrCompositor.GetCurrentSceneFocusProcess() == (uint)Process.GetCurrentProcess().Id;
            bool dashboard = vrOverlay != null && vrOverlay.IsDashboardVisible();
            if (!sceneOwned || dashboard)
            {
                ReleaseControllerMouse(); gripTracking = false;
                if (pointerLine != null) pointerLine.enabled = false;
                return;
            }
            controllerInputAllowed = true;
            vrSystem.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0, devicePoses);
            ReadHand(0, ETrackedControllerRole.LeftHand);
            ReadHand(1, ETrackedControllerRole.RightHand);
            controllerFrame = controllerBrain.Update(hands[0].Sample, hands[1].Sample,
                Mathf.Clamp(stickDeadzone.Value, 0.05f, 0.8f));
            if (controllerFrame.PanelDown) ToggleActorPanel();
            pendingClickDown |= controllerFrame.ConfirmDown;
            pendingClickUp |= controllerFrame.ConfirmUp;
            if (controllerFrame.ConfirmDown)
            {
                clickRequestUntil = Time.unscaledTime + 0.5f;
                controllerTriggerPresses++;
                Logger.LogInfo("Controller trigger pressed: " + (controllerFrame.Hand == 0 ? "LEFT" : "RIGHT"));
            }
            if (controllerFrame.Hand < 0)
            {
                gripTracking = false; ReleaseControllerMouse();
                return;
            }
            if (Interaction.ConfirmOpen || SettingsOpen() || actorPanelOpen || activeCamera == null)
            {
                gripTracking = false;
                return;
            }
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.05f);
            Vector3 right = YawOnly(lastViewRotation) * Vector3.right;
            Vector3 forwardMove = YawOnly(lastViewRotation) * Vector3.forward;
            Vector3 movement = right * controllerFrame.X + Vector3.up * controllerFrame.Y + forwardMove * controllerFrame.Z;
            if (boundActor != null) MoveActorOffset(movement * dt);
            else controllerTranslation += movement * Mathf.Clamp(stickSpeed.Value, 0.02f, 3f) * dt;
            HandRuntime hand = hands[controllerFrame.Hand];
            if (controllerFrame.Gripping && hand.PoseValid)
            {
                if (controllerFrame.GripStarted || !gripTracking)
                {
                    gripStart = hand.Rotation;
                    gripLastYaw = gripLastPitch = 0f;
                    gripTracking = true;
                }
                Quaternion delta = Quaternion.Inverse(gripStart) * hand.Rotation;
                Vector3 forward = delta * Vector3.forward;
                float yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
                float pitch = -Mathf.Atan2(forward.y, new Vector2(forward.x, forward.z).magnitude) * Mathf.Rad2Deg;
                controllerYaw = ViewMath.WrapYaw(controllerYaw + Mathf.DeltaAngle(gripLastYaw, yaw) * gripSensitivity.Value);
                controllerPitch = ViewMath.ClampPitch(controllerPitch +
                    ViewMath.GripPitchDelta(gripLastPitch, pitch, invertGripPitch.Value) * gripSensitivity.Value,
                    Mathf.Clamp(pitchLimit.Value, 1f, 89.9f));
                gripLastYaw = yaw; gripLastPitch = pitch;
            }
            else gripTracking = false;
        }

        private void ReadHand(int slot, ETrackedControllerRole role)
        {
            HandRuntime hand = hands[slot];
            uint index = vrSystem.GetTrackedDeviceIndexForControllerRole(role);
            hand.Index = index;
            VRControllerState_t state = new VRControllerState_t();
            bool valid = index < devicePoses.Length &&
                vrSystem.GetControllerState(index, ref state, (uint)Marshal.SizeOf(typeof(VRControllerState_t)));
            hand.PoseValid = valid && devicePoses[index].bDeviceIsConnected && devicePoses[index].bPoseIsValid;
            bool activity = false;
            if (hand.PoseValid)
            {
                ConvertPose(devicePoses[index].mDeviceToAbsoluteTracking, out hand.Position, out hand.Rotation);
                if (!hand.AnchorValid || Vector3.Distance(hand.Position, hand.ActivityPosition) > 0.02f ||
                    Quaternion.Angle(hand.Rotation, hand.ActivityRotation) > 3f)
                {
                    activity = hand.AnchorValid;
                    hand.ActivityPosition = hand.Position; hand.ActivityRotation = hand.Rotation; hand.AnchorValid = true;
                }
            }
            else hand.AnchorValid = false;
            hand.Sample = new HandSample {
                Valid = valid, Panel = valid && (state.ulButtonPressed & (1UL << 32)) != 0,
                Backward = valid && (state.ulButtonPressed & (1UL << 7)) != 0,
                Forward = valid && (state.ulButtonPressed & (1UL << 1)) != 0,
                Trigger = valid && ((state.ulButtonPressed & (1UL << 33)) != 0 || state.rAxis1.x > 0.65f || state.rAxis1.y > 0.65f),
                Grip = valid && ((state.ulButtonPressed & ((1UL << 2) | (1UL << 34))) != 0 ||
                    state.rAxis2.x > 0.65f || state.rAxis2.y > 0.65f),
                X = valid ? state.rAxis0.x : 0f, Y = valid ? state.rAxis0.y : 0f, Activity = activity
            };
        }

        private static void ConvertPose(HmdMatrix34_t matrix, out Vector3 position, out Quaternion rotation)
        {
            position = new Vector3(matrix.m3, matrix.m7, -matrix.m11);
            Vector3 forward = new Vector3(-matrix.m2, -matrix.m6, matrix.m10);
            Vector3 up = new Vector3(matrix.m1, matrix.m5, -matrix.m9);
            rotation = Quaternion.LookRotation(forward, up);
        }

        private bool OverlayPose(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero; rotation = Quaternion.identity;
            if (ui == null) return false;
            Component overlay = UiOverlayField.GetValue(ui) as Component;
            Camera camera = overlay == null ? null : overlay.GetComponentInChildren<Camera>();
            if (camera == null) return false;
            Matrix4x4 left = camera.GetStereoViewMatrix(Camera.StereoscopicEye.Left).inverse;
            Matrix4x4 right = camera.GetStereoViewMatrix(Camera.StereoscopicEye.Right).inverse;
            position = ((Vector3)left.GetColumn(3) + (Vector3)right.GetColumn(3)) * 0.5f;
            rotation = Quaternion.LookRotation(-left.GetColumn(2), left.GetColumn(1));
            return true;
        }

        private void UpdateControllerPointer()
        {
            pointerValid = false;
            bool releaseFrame = pendingClickUp;
            if (pendingClickUp) { pendingClickUp = false; ReleaseControllerMouse(); }
            if (!controllerInputAllowed || controllerFrame.Hand < 0 || uiQuad == null)
            {
                pendingClickDown = false;
                if (pointerLine != null) pointerLine.enabled = false;
                return;
            }
            HandRuntime hand = hands[controllerFrame.Hand];
            Vector3 overlayPosition;
            Quaternion overlayRotation;
            if (!hand.PoseValid || !devicePoses[0].bPoseIsValid || !OverlayPose(out overlayPosition, out overlayRotation))
            {
                pendingClickDown = false; ReleaseControllerMouse();
                if (pointerLine != null) pointerLine.enabled = false;
                return;
            }
            Vector3 headPosition;
            Quaternion headRotation;
            ConvertPose(devicePoses[0].mDeviceToAbsoluteTracking, out headPosition, out headRotation);
            Quaternion alignment = overlayRotation * Quaternion.Inverse(headRotation);
            Vector3 start = overlayPosition + alignment * (hand.Position - headPosition);
            Quaternion orientation = alignment * hand.Rotation;
            Ray ray = new Ray(start, orientation * Vector3.forward);
            Plane plane = new Plane(uiQuad.transform.forward, uiQuad.transform.position);
            float distance;
            Vector3 end = start + ray.direction * 3f;
            if (plane.Raycast(ray, out distance) && distance > 0 && distance < 8f)
            {
                end = ray.GetPoint(distance);
                Vector3 local = uiQuad.transform.InverseTransformPoint(end);
                bool mirror = ModConfiguration.Instance.PreferredUiPatchMode.Value == ModConfiguration.UiPatchMode.Mirror;
                float[] pixels = ViewMath.PointerPixels(local.x, local.y, Screen.width, Screen.height, mirror);
                if (pixels != null)
                {
                    Vector2 target = new Vector2(pixels[0], pixels[1]);
                    float response = 1f - Mathf.Exp(-Mathf.Clamp(pointerSmoothing.Value, 1f, 100f) *
                        Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.05f));
                    float[] stabilized = controllerPointerLatch.Move(target.x, target.y,
                        pendingClickDown, controllerFrame.ConfirmHeld, Mathf.Max(20f, Screen.height * 0.025f));
                    target = new Vector2(stabilized[0], stabilized[1]);
                    pointerPixel = !pointerPixelValid || pendingClickDown ? target : Vector2.Lerp(pointerPixel, target, response);
                    pointerPixelValid = true; pointerValid = true;
                }
            }
            if (!pointerValid) pointerPixelValid = false;
            EnsurePointerLine();
            pointerLine.enabled = true; pointerLine.gameObject.layer = uiQuad.layer;
            pointerLine.startColor = pointerLine.endColor = pointerValid ? Color.cyan : new Color(0.4f, 0.4f, 0.4f);
            pointerLine.SetPosition(0, start); pointerLine.SetPosition(1, end);
            if (pendingClickDown)
            {
                Interaction.Release();
                Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
                if (!controllerFrame.ConfirmHeld || Time.unscaledTime > clickRequestUntil)
                    pendingClickDown = false;
                else if (pointerValid && NativeMouse.FocusGame(controllerFocus.Value))
                {
                    NativeMouse.MoveToClient(pointerPixel.x, pointerPixel.y, Screen.width, Screen.height);
                    if (NativeMouse.LeftButton(true))
                    {
                        pendingClickDown = false; leftMouseOwned = true; controllerClicks++;
                        Logger.LogInfo("Controller confirm: " + (controllerFrame.Hand == 0 ? "LEFT TRIGGER" : "RIGHT TRIGGER"));
                    }
                }
            }
            if (!releaseFrame && pointerValid && !Interaction.Looking && NativeMouse.IsGameForeground())
                NativeMouse.MoveToClient(pointerPixel.x, pointerPixel.y, Screen.width, Screen.height);
        }

        private void EnsurePointerLine()
        {
            if (pointerLine != null) return;
            GameObject beam = new GameObject("NorthwoodControllerPointer");
            beam.transform.SetParent(transform, false);
            pointerLine = beam.AddComponent<LineRenderer>();
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Canvas.GetDefaultCanvasMaterial().shader;
            pointerMaterial = new Material(shader);
            pointerLine.material = pointerMaterial;
            pointerLine.useWorldSpace = true; pointerLine.positionCount = 2;
            pointerLine.startWidth = 0.003f; pointerLine.endWidth = 0.002f;
            pointerMaterial.renderQueue = 5000;
        }

        private void ReleaseControllerMouse()
        {
            if (!leftMouseOwned) return;
            NativeMouse.LeftButton(false);
            leftMouseOwned = false;
            controllerPointerLatch.Reset();
        }

        private void ResetControllerView()
        {
            controllerTranslation = Vector3.zero;
            controllerYaw = controllerPitch = 0f;
            gripTracking = false;
        }

        private void FillControllerStatus(RuntimeStatus status)
        {
            status.LeftController = hands[0].Sample.Valid;
            status.RightController = hands[1].Sample.Valid;
            status.ActiveHand = controllerFrame.Hand;
            status.ControllerGrip = controllerFrame.Gripping;
            status.ControllerPointer = pointerValid;
            status.ControllerOffset = controllerTranslation;
            status.ControllerClicks = controllerClicks;
            status.ControllerTriggerPresses = controllerTriggerPresses;
            status.NativeWindowFound = NativeMouse.HasWindow;
            status.ForegroundGame = NativeMouse.IsGameForeground();
            status.MouseMoveSucceeded = NativeMouse.LastMoveSucceeded;
            status.NativeMouseError = NativeMouse.LastError;
            status.ControllerPointerPixels = pointerPixel;
            status.UnityMousePixels = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
        }

        private void DisposeControllers()
        {
            ReleaseControllerMouse();
            if (pointerMaterial != null) Destroy(pointerMaterial);
            Application.runInBackground = oldRunInBackground;
        }

        private static class NativeMouse
        {
            private static IntPtr cachedWindow;
            private static readonly uint processId = (uint)Process.GetCurrentProcess().Id;
            public static bool LastMoveSucceeded { get; private set; }
            public static int LastError { get; private set; }
            [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
            [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
            [StructLayout(LayoutKind.Sequential)] private struct MouseInput
            {
                public int X, Y;
                public uint Data, Flags, Time;
                public UIntPtr Extra;
            }
            [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public MouseInput Mouse; }
            [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
            private delegate bool EnumWindowCallback(IntPtr window, IntPtr data);
            [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr data);
            [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
            [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
            [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int size);
            [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
            [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
            [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
            [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
            [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);

            private static IntPtr Window
            {
                get
                {
                    if (cachedWindow != IntPtr.Zero && IsWindow(cachedWindow)) return cachedWindow;
                    IntPtr fallback = IntPtr.Zero;
                    EnumWindows(delegate(IntPtr handle, IntPtr data) {
                        uint pid; GetWindowThreadProcessId(handle, out pid);
                        if (pid != processId || !IsWindowVisible(handle)) return true;
                        fallback = handle;
                        StringBuilder name = new StringBuilder(256);
                        GetClassName(handle, name, name.Capacity);
                        if (name.ToString().IndexOf("UnityWndClass", StringComparison.OrdinalIgnoreCase) >= 0)
                        { cachedWindow = handle; return false; }
                        return true;
                    }, IntPtr.Zero);
                    if (cachedWindow == IntPtr.Zero) cachedWindow = fallback;
                    return cachedWindow;
                }
            }
            public static bool HasWindow { get { return Window != IntPtr.Zero; } }
            public static bool IsGameForeground() { return Window != IntPtr.Zero && GetForegroundWindow() == Window; }
            public static bool FocusGame(bool allowFocus)
            {
                if (IsGameForeground()) return true;
                return allowFocus && Window != IntPtr.Zero && SetForegroundWindow(Window) && IsGameForeground();
            }
            public static void MoveToClient(float x, float y, int width, int height)
            {
                if (!IsGameForeground()) return;
                Rect rect;
                if (!GetClientRect(Window, out rect)) return;
                Point point = new Point {
                    X = Mathf.RoundToInt(x * (rect.Right - rect.Left) / Math.Max(1, width)),
                    Y = Mathf.RoundToInt(y * (rect.Bottom - rect.Top) / Math.Max(1, height))
                };
                if (ClientToScreen(Window, ref point))
                {
                    LastMoveSucceeded = SetCursorPos(point.X, point.Y);
                    LastError = LastMoveSucceeded ? 0 : Marshal.GetLastWin32Error();
                }
            }
            public static bool LeftButton(bool down)
            {
                if (down && !IsGameForeground()) return false;
                Input input = new Input { Type = 0, Mouse = new MouseInput { Flags = down ? 2u : 4u } };
                bool sent = SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Input))) == 1;
                LastError = sent ? 0 : Marshal.GetLastWin32Error();
                return sent;
            }
        }
    }
}
