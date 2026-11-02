using System;
using NorthwoodVrFixes;

public static class CoreTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
    public static void Main()
    {
        Check(ViewMath.IsUiCameraName("UI Cam") && ViewMath.IsUiCameraName("UICamera") &&
            ViewMath.IsUiCameraName("GUI Camera") && !ViewMath.IsUiCameraName("GuideCamera") &&
            !ViewMath.IsUiCameraName("MonsPOV2"), "UI cameras must not receive viewpoint calibration");
        Check(ViewMath.IsFirstPersonCameraName("MonsPOV") && ViewMath.IsFirstPersonCameraName("MonsPOV2") &&
            !ViewMath.IsFirstPersonCameraName("Main Mons Cam"), "POV views must not be converted into third-person orbit views");
        Interaction state = new Interaction();
        state.ToggleLook();
        Check(state.Looking, "F8 enters look");
        state.Escape(10);
        state.Escape(10);
        Check(state.ConfirmOpen && !state.Looking, "Duplicate Esc handlers in one frame must not dismiss confirmation");
        Check(state.InterceptQuit(false), "Underlying quit button must be blocked while modal is open");
        state.ToggleLook();
        Check(!state.Looking, "Modal must prevent mouse capture");
        state.Escape(11);
        Check(!state.ConfirmOpen && !state.Looking, "A second distinct Esc cancels; it must not quit");
        state.ToggleLook();
        state.Release();
        Check(!state.Looking, "Focus loss releases cursor");
        Check(state.InterceptQuit(true), "Esc exit call must be redirected");
        Check(!state.InterceptQuit(false), "Normal window/menu quit remains possible outside modal");

        float[] small = ViewMath.Fit(1280, 720, 1.6f, 0.9f);
        float[] large = ViewMath.Fit(2560, 1440, 1.6f, 0.9f);
        Check(Math.Abs(small[0] - large[0]) < 0.00001f && Math.Abs(small[1] - large[1]) < 0.00001f,
            "Doubling PC resolution must not double the VR menu");
        foreach (int[] size in new[] { new[] { 800, 600 }, new[] { 3440, 1440 }, new[] { 720, 1280 }, new[] { 7680, 4320 } })
        {
            float[] fitted = ViewMath.Fit(size[0], size[1], 1.6f, 0.9f);
            Check(fitted[0] <= 1.60001f && fitted[1] <= 0.90001f, "Full UI must fit within both bounds");
            Check(Math.Abs(fitted[0] / fitted[1] - (float)size[0] / size[1]) < 0.0001f, "No aspect distortion");
        }
        float yaw = 0;
        for (int i = 0; i < 100000; i++) yaw = ViewMath.WrapYaw(yaw + 17);
        Check(Math.Abs(yaw - ViewMath.WrapYaw(1700000)) < 0.001f, "Unlimited turns must not accumulate a fixed range clamp");
        Check(ViewMath.ClampPitch(110, 89) == 89 && ViewMath.ClampPitch(-110, 89) == -89, "Mouse pitch cannot flip");
        ControllerBrain brain = new ControllerBrain();
        HandSample l = new HandSample { Valid = true };
        HandSample r = new HandSample { Valid = true };
        brain.Update(l, r, 0.2f);
        l.Trigger = true;
        ControllerFrame frame = brain.Update(l, r, 0.2f);
        Check(frame.Hand == 0 && frame.ConfirmDown, "Left trigger selects the left pointer and clicks");
        r.Trigger = true;
        frame = brain.Update(l, r, 0.2f);
        Check(!frame.ConfirmDown && !frame.ConfirmUp, "Two held confirm buttons must not double click");
        l.Trigger = false;
        frame = brain.Update(l, r, 0.2f);
        Check(frame.Hand == 1 && !frame.ConfirmUp, "Switching between held buttons must not release the click");
        r.Valid = false;
        frame = brain.Update(l, r, 0.2f);
        Check(frame.ConfirmUp, "Disconnect must release a controller-owned mouse button");
        r = new HandSample { Valid = true };
        l.Forward = true;
        frame = brain.Update(l, r, 0.2f);
        Check(frame.Z == 1 && frame.Hand == 0 && !frame.ConfirmDown, "Left Y moves forward without clicking or returning");
        r.Forward = true;
        frame = brain.Update(l, r, 0.2f);
        Check(frame.Z == 1 && !frame.ConfirmDown, "Both forward buttons must not double movement speed");
        l.Forward = false; l.Backward = true; r.Forward = false;
        frame = brain.Update(l, r, 0.2f);
        Check(frame.Z == -1 && !frame.ConfirmDown, "Left X moves backward without clicking");
        l = new HandSample { Valid = true, X = 0.1f, Y = 0.1f };
        r = new HandSample { Valid = true };
        frame = brain.Update(l, r, 0.2f);
        Check(frame.X == 0 && frame.Y == 0, "Stick noise inside deadzone cannot move the camera");
        l.X = 1;
        frame = brain.Update(l, r, 0.2f);
        Check(frame.Hand == 0 && frame.X > 0.9f, "Left stick drives full-speed lateral movement");
        l = new HandSample { Valid = true };
        r = new HandSample { Valid = true, Grip = true };
        frame = brain.Update(l, r, 0.2f);
        Check(frame.Hand == 1 && frame.GripStarted, "Right grip captures a fresh rotation origin");
        frame = brain.Update(l, r, 0.2f);
        Check(frame.Gripping && !frame.GripStarted, "Holding grip must not recapture every frame");
        r.Valid = false; l.Grip = true;
        frame = brain.Update(l, r, 0.2f);
        Check(frame.Hand == 0 && frame.GripStarted, "Changing grip hand requires a new origin; no jump");
        float[] topLeft = ViewMath.PointerPixels(-0.5f, -0.5f, 2560, 1440, true);
        Check(topLeft[0] == 0 && topLeft[1] == 0, "Mirrored UI top-left maps to Windows client top-left");
        Check(ViewMath.PointerPixels(0.6f, 0, 2560, 1440, true) == null, "Aiming outside the menu cannot click desktop controls");
        for (int side = 0; side < 2; side++)
        {
            ControllerBrain single = new ControllerBrain();
            HandSample one = new HandSample { Valid = true, Trigger = true, Forward = true, X = -1, Y = 1 };
            ControllerFrame f = single.Update(side == 0 ? one : new HandSample(), side == 1 ? one : new HandSample(), 0.2f);
            Check(f.Hand == side && f.ConfirmDown && f.ConfirmHeld && f.X < 0 && f.Y > 0 && f.Z == 1,
                "Either hand alone must provide identical trigger and 3-axis movement");
        }
        Check(ViewMath.GripPitchDelta(0, -20, true) == 20 && ViewMath.GripPitchDelta(0, -20, false) == -20,
            "Grip vertical inversion must change direction without changing magnitude");
        Check(ViewMath.GripPitchDelta(179, -179, true) == -2, "Grip pitch wrap must not cause a full-turn jump");
        Check(ActorMath.IsHead("Armature:Head") && ActorMath.IsHead("Bip01 Head") &&
            ActorMath.IsHead("Root|頭") && !ActorMath.IsHead("headtip") && !ActorMath.IsHead("headphone"),
            "Recognize supported head bones without binding accessories or tips");
        Check(ActorMath.IsLeftEye("left_eye") && ActorMath.IsLeftEye("左目") &&
            ActorMath.IsRightEye("right.eye") && ActorMath.IsRightEye("migimemodo") &&
            !ActorMath.IsRightEye("righteyetip"), "Eye points must use actual left/right bones");
        Check(ActorMath.IsHead("11.joint_Head") && ActorMath.IsHead("283.!joint_mHead") &&
            ActorMath.IsHead("291.joint_M1Head") && ActorMath.IsHead("7.joint_H2Head") &&
            ActorMath.IsHead("525.joint_M2HHead") && ActorMath.IsRightEye("121.!joint_RightEye") &&
            ActorMath.IsLeftEye("133.!joint_LeftEye") && ActorMath.BoneKey("281.joint_mTorso2") == "torso2" &&
            !ActorMath.IsHead("292.!joint_M1HeadTip") && !ActorMath.IsHead("183.!IK_SkL Head IK"),
            "Shipped numbered skeleton names must match without binding head tips or IK controls");
        int[] original = { 0, 1, 2, 2, 3, 4, 3, 4, 5 };
        int[] retained = ActorMath.ClipHeadTriangles(original, new[] { true, true, true, false, false, false });
        Check(retained.Length == 6 && retained[0] == 2 && retained[3] == 3 && original.Length == 9 &&
            original[0] == 0 && original[3] == 2,
            "Clip pure head triangles, preserve neck/body triangles and original mesh indices");
        Check(ActorMath.ClipHeadTriangles(original, new bool[6]).Length == 9 &&
            ActorMath.ClipHeadTriangles(new int[0], new bool[0]).Length == 0,
            "A mesh without head weights must retain all geometry");
        bool invalidTriangles = false;
        try { ActorMath.ClipHeadTriangles(new[] { 0, 1 }, new[] { true, true }); }
        catch (ArgumentException) { invalidTriangles = true; }
        Check(invalidTriangles, "Malformed topology must fail before changing a mesh");
        Check(ActorMath.ClampOffset(0.3f) == 0.25f && ActorMath.ClampOffset(-0.3f) == -0.25f &&
            ActorMath.ClampOffset(0.05f) == 0.05f, "Eye adjustment must respect each axis limit");
        ControllerBrain panelBrain = new ControllerBrain();
        l = new HandSample { Valid = true, Panel = true };
        r = new HandSample { Valid = true };
        Check(panelBrain.Update(l, r, 0.2f).PanelDown, "Left stick click opens the actor panel");
        r.Panel = true;
        Check(!panelBrain.Update(l, r, 0.2f).PanelDown, "Two held stick buttons cannot toggle the panel twice");
        l.Panel = r.Panel = false; panelBrain.Update(l, r, 0.2f);
        r.Panel = true;
        frame = panelBrain.Update(l, r, 0.2f);
        Check(frame.PanelDown && frame.Hand == 1, "Right stick click independently opens the actor panel");
        Check(!panelBrain.Update(l, r, 0.2f).PanelDown, "Holding stick click must not reopen every frame");
        ActorCalibrationStore calibration = new ActorCalibrationStore();
        calibration.Items.Add(new ActorCalibration { Key = "17:角色/头骨#1", X = 0.05f, Y = -0.03f, Z = 0.12f });
        calibration.Items.Add(new ActorCalibration { Key = "27:quoted \"actor\"\\bone", X = -0.15f, Yaw = 90 });
        string json = CalibrationJson.Write(calibration);
        ActorCalibrationStore loaded = CalibrationJson.Read<ActorCalibrationStore>(json);
        Check(json.Contains("Items") && loaded.Items.Count == 2 && loaded.Items[0].Key == calibration.Items[0].Key &&
            loaded.Items[0].X == 0.05f && loaded.Items[0].Y == -0.03f && loaded.Items[0].Z == 0.12f &&
            loaded.Items[1].Key == calibration.Items[1].Key && loaded.Items[1].Yaw == 90,
            "Real calibration DTO must serialize complete lists, Unicode keys and all offsets");
        Check(CalibrationJson.Read<ActorCalibrationStore>("{}").Items == null,
            "Empty calibration from an older build must be recognized as missing data");
        Check(ActorMath.IsHead("210.joint_Husatama") && ActorMath.IsHead("434.joint_2Husatama") &&
            ActorMath.IsHead("418.joint_M2 Husatama") && ActorMath.IsHead("213.!joint_SkL atama") &&
            ActorMath.IsHead("Coll.162.15.joint_Head") && ActorMath.IsHead("18.joint__M8Head") &&
            ActorMath.BoneKey("209.joint_Huskubi") == "neck" &&
            ActorMath.BoneKey("208.joint_Husjouhanshin2") == "torso2" &&
            !ActorMath.IsHead("211.!joint_Husatamasaki") && !ActorMath.IsHead("12.joint_kagekakukameatama") &&
            ActorMath.BoneKey("1.joint_Husmigitekubi") != "neck",
            "Real male/monster romanized bones must match without binding tips, wrists or helper heads");
        Check(ActorMath.RoleLabel("210.joint_Husatama") == "Man / Male" &&
            ActorMath.RoleLabel("213.joint_SkLatama") == "Monster / Skull Lord" &&
            ActorMath.RolePriority("325.joint_mHead") < ActorMath.RolePriority("15.joint_Head") &&
            ActorMath.RoleLabel("300.joint_Lukaatama") == "Female / luka",
            "Male/monster identities use bone branch names, never the mixed animation object name");
        PointerLatch pointer = new PointerLatch();
        pointer.Move(100, 200, true, true, 36);
        float[] heldPoint = pointer.Move(120, 218, false, true, 36);
        Check(heldPoint[0] == 100 && heldPoint[1] == 200, "Trigger hold jitter must not become a ScrollRect drag");
        heldPoint = pointer.Move(150, 200, false, true, 36);
        Check(heldPoint[0] == 150, "Deliberate controller drag must remain available");
        heldPoint = pointer.Move(105, 200, false, true, 36);
        Check(heldPoint[0] == 105, "Drag must not snap back to the click anchor");
        pointer.Reset();
        heldPoint = pointer.Move(300, 400, true, true, 36);
        Check(heldPoint[0] == 300 && heldPoint[1] == 400, "A new click must use a fresh anchor");
        Console.WriteLine("PASS: Esc frame deduplication, modal capture, focus release, UI resizing/aspect, unlimited yaw, pitch.");
        Console.WriteLine("PASS: symmetric triggers, disconnect release, 3-axis movement, grip inversion/handover, UI pointer bounds.");
        Console.WriteLine("PASS: character bone aliases, conservative head clipping, original indices retained, eye limits, symmetric panel toggle.");
        Console.WriteLine("PASS: complete calibration JSON list round trip, Unicode/escaped keys, nonzero offsets and legacy empty data.");
        Console.WriteLine("PASS: male/monster romanized skeletons, conservative role labels, click jitter suppression and intentional drag.");
    }
}
