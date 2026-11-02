using System;
using System.Text.RegularExpressions;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace NorthwoodVrFixes
{
    [DataContract] public sealed class ActorCalibration
    {
        [DataMember] public string Key;
        [DataMember] public float X, Y, Z, Yaw;
    }
    [DataContract] public sealed class ActorCalibrationStore
    {
        [DataMember] public List<ActorCalibration> Items = new List<ActorCalibration>();
    }
    public static class CalibrationJson
    {
        public static T Read<T>(string json)
        {
            using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
        public static string Write<T>(T value)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    public struct HandSample
    {
        public bool Valid, Backward, Forward, Trigger, Grip, Activity, Panel;
        public float X, Y;
    }

    public struct ControllerFrame
    {
        public int Hand;
        public bool ConfirmDown, ConfirmUp, ConfirmHeld, Gripping, GripStarted, PanelDown;
        public float X, Y, Z;
    }

    public sealed class ControllerBrain
    {
        private bool primary, grip, panel;
        private int owner = -1;
        public ControllerFrame Update(HandSample left, HandSample right, float deadzone)
        {
            if (!left.Valid) left = new HandSample();
            if (!right.Valid) right = new HandSample();
            bool lInput = left.Backward || left.Forward || left.Trigger || left.Grip || left.Panel;
            bool rInput = right.Backward || right.Forward || right.Trigger || right.Grip || right.Panel;
            int old = owner;
            if (owner == 0 && left.Valid && left.Grip) { }
            else if (owner == 1 && right.Valid && right.Grip) { }
            else if (left.Grip && !right.Grip) owner = 0;
            else if (right.Grip && !left.Grip) owner = 1;
            else if (lInput && !rInput) owner = 0;
            else if (rInput && !lInput) owner = 1;
            else
            {
                float lm = left.X * left.X + left.Y * left.Y;
                float rm = right.X * right.X + right.Y * right.Y;
                float threshold = deadzone * deadzone;
                if (lm > threshold && lm > rm + 0.0025f) owner = 0;
                else if (rm > threshold && rm > lm + 0.0025f) owner = 1;
                else if (left.Activity && !right.Activity) owner = 0;
                else if (right.Activity && !left.Activity) owner = 1;
            }
            if (owner == 0 && !left.Valid) owner = right.Valid ? 1 : -1;
            if (owner == 1 && !right.Valid) owner = left.Valid ? 0 : -1;
            if (owner < 0) owner = right.Valid ? 1 : left.Valid ? 0 : -1;
            HandSample current = owner == 0 ? left : right;
            bool nextPrimary = left.Trigger || right.Trigger;
            bool nextPanel = left.Panel || right.Panel;
            bool nextGrip = owner >= 0 && current.Grip;
            ControllerFrame result = new ControllerFrame {
                Hand = owner, ConfirmDown = nextPrimary && !primary, ConfirmUp = !nextPrimary && primary,
                ConfirmHeld = nextPrimary, Gripping = nextGrip,
                PanelDown = nextPanel && !panel,
                GripStarted = nextGrip && (!grip || owner != old),
                Z = owner < 0 ? 0 : (current.Forward ? 1f : 0f) - (current.Backward ? 1f : 0f)
            };
            float magnitude = (float)Math.Sqrt(current.X * current.X + current.Y * current.Y);
            if (magnitude > deadzone && owner >= 0)
            {
                float response = Math.Min(1f, (magnitude - deadzone) / Math.Max(0.01f, 1f - deadzone));
                result.X = current.X / magnitude * response;
                result.Y = current.Y / magnitude * response;
            }
            primary = nextPrimary; grip = nextGrip; panel = nextPanel;
            return result;
        }
    }

    public static class ActorMath
    {
        public static string JointToken(string name)
        {
            string[] parts = (name ?? "").Split(':', '|');
            string last = parts[parts.Length - 1].ToLowerInvariant();
            last = Regex.Replace(last, @"^(?:coll\.\d+\.)?\d+\.", "");
            last = Regex.Replace(last, @"^!?joint_", "");
            return Regex.Replace(last, @"[\s_\-\.]", "");
        }
        public static string BoneKey(string name)
        {
            string[] parts = (name ?? "").Split(':', '|');
            string last = parts[parts.Length - 1].ToLowerInvariant();
            // Verified names in the shipped scene: 11.joint_Head, 283.joint_mHead,
            // 291.joint_M1Head, 7.joint_H2Head and 121.!joint_RightEye.
            Match joint = Regex.Match(JointToken(name),
                @"^(?:m\d*h?|h\d*|hus|2hus|m\d*hus|skl|miku|luka|lka|haku|ia|mko|teto|tianyi|l|s)?(head|atama|neck|kubi|torso[234]?|jouhanshin2?|lefteye(?:tip)?|righteye(?:tip)?|hidarimemodo|migimemodo|hidarime|migime)$");
            if (joint.Success)
            {
                string key = joint.Groups[1].Value;
                if (key == "atama") return "head";
                if (key == "kubi") return "neck";
                if (key == "jouhanshin2") return "torso2";
                if (key == "jouhanshin") return "torso";
                if (key == "hidarime" || key == "hidarimemodo") return "lefteye";
                if (key == "migime" || key == "migimemodo") return "righteye";
                return key;
            }
            return Regex.Replace(last, @"[\s_\-\.]", "");
        }
        public static string RoleLabel(string head)
        {
            string token = JointToken(head);
            if (Regex.IsMatch(token, @"^skl(?:head|atama)$")) return "Monster / Skull Lord";
            if (Regex.IsMatch(token, @"^(?:hus|2hus|m\d*hus)(?:head|atama)$")) return "Man / Male";
            if (Regex.IsMatch(token, @"^(?:m\d*h?|h\d+|h)(?:head|atama)$"))
                return "Man / Player / Henchman (" + Regex.Replace(token, "(head|atama)$", "").ToUpperInvariant() + ")";
            Match female = Regex.Match(token, @"^(miku|luka|lka|haku|ia|mko|teto|tianyi|l)(?:head|atama)$");
            if (female.Success)
            {
                string role = female.Groups[1].Value;
                if (role == "l" || role == "lka") role = "luka";
                if (role == "mko") role = "meiko";
                return "Female / " + role;
            }
            return "身份待确认 (" + head + ")";
        }
        public static int RolePriority(string head)
        {
            string role = RoleLabel(head);
            return role.StartsWith("Man") || role.StartsWith("Monster") ? 0 : role.StartsWith("Female") ? 2 : 1;
        }
        public static bool IsHead(string name)
        {
            string key = BoneKey(name);
            return key == "head" || key == "頭" || key == "頭部" || key == "head01" ||
                key == "jbipchead" || key == "cfjhead" || Regex.IsMatch(key, @"^bip\d+head$");
        }
        public static bool IsLeftEye(string name)
        {
            string key = BoneKey(name);
            return key == "lefteye" || key == "eyel" || key == "leye" || key == "左目" ||
                key == "左眼" || key == "hidarimemodo" || key == "jbipclefteye";
        }
        public static bool IsRightEye(string name)
        {
            string key = BoneKey(name);
            return key == "righteye" || key == "eyer" || key == "reye" || key == "右目" ||
                key == "右眼" || key == "migimemodo" || key == "jbipcrighteye";
        }
        public static int[] ClipHeadTriangles(int[] source, bool[] headVertices)
        {
            if (source.Length % 3 != 0) throw new ArgumentException("Triangle indices must be a multiple of three.");
            System.Collections.Generic.List<int> result = new System.Collections.Generic.List<int>(source.Length);
            for (int i = 0; i < source.Length; i += 3)
            {
                int a = source[i], b = source[i + 1], c = source[i + 2];
                if (headVertices[a] && headVertices[b] && headVertices[c]) continue;
                result.Add(a); result.Add(b); result.Add(c);
            }
            return result.ToArray();
        }
        public static float ClampOffset(float value) { return Math.Max(-0.25f, Math.Min(0.25f, value)); }
    }

    public sealed class PointerLatch
    {
        private float x, y;
        private bool held, dragging;
        public float[] Move(float nextX, float nextY, bool down, bool isHeld, float threshold)
        {
            if (down) { x = nextX; y = nextY; held = true; dragging = false; }
            if (held && isHeld && !dragging)
            {
                float dx = nextX - x, dy = nextY - y;
                if (dx * dx + dy * dy > threshold * threshold) dragging = true;
                else return new[] { x, y };
            }
            held = isHeld;
            return new[] { nextX, nextY };
        }
        public void Reset() { held = dragging = false; }
    }

    public sealed class Interaction
    {
        public bool Looking { get; private set; }
        public bool ConfirmOpen { get; private set; }
        private int lastEscapeFrame = -1;

        public void Escape(int frame)
        {
            if (lastEscapeFrame == frame) return;
            lastEscapeFrame = frame;
            Looking = false;
            ConfirmOpen = !ConfirmOpen;
        }

        public void ToggleLook()
        {
            if (!ConfirmOpen) Looking = !Looking;
        }

        public void Release() { Looking = false; }
        public void Cancel() { Looking = false; ConfirmOpen = false; }
        public bool InterceptQuit(bool escapeDown) { return ConfirmOpen || escapeDown; }
    }

    public static class ViewMath
    {
        public static bool IsUiCameraName(string name)
        {
            return Regex.IsMatch(name ?? "", @"(?:^|[\s_])(?:ui|gui|canvas|overlay)(?:[\s_]|cam|$)", RegexOptions.IgnoreCase);
        }

        public static bool IsFirstPersonCameraName(string name)
        {
            return (name ?? "").IndexOf("POV", StringComparison.OrdinalIgnoreCase) >= 0 ||
                Regex.IsMatch(name ?? "", @"first[\s_]*person", RegexOptions.IgnoreCase);
        }

        public static float WrapYaw(float value)
        {
            return (float)(value - Math.Floor((value + 180.0) / 360.0) * 360.0);
        }

        public static float ClampPitch(float value, float limit)
        {
            return Math.Max(-limit, Math.Min(limit, value));
        }

        public static float GripPitchDelta(float previous, float current, bool invert)
        {
            return WrapYaw(current - previous) * (invert ? -1f : 1f);
        }

        public static float[] Fit(int width, int height, float maxWidth, float maxHeight)
        {
            if (width <= 0 || height <= 0 || maxWidth <= 0 || maxHeight <= 0)
                throw new ArgumentOutOfRangeException();
            float scale = Math.Min(maxWidth / width, maxHeight / height);
            return new[] { width * scale, height * scale };
        }

        public static float[] PointerPixels(float localX, float localY, int width, int height, bool mirror)
        {
            if (Math.Abs(localX) > 0.5f || Math.Abs(localY) > 0.5f || width <= 0 || height <= 0) return null;
            float x = (localX + 0.5f) * width;
            float y = (mirror ? localY + 0.5f : 0.5f - localY) * height;
            return new[] { Math.Max(0, Math.Min(width - 1, x)), Math.Max(0, Math.Min(height - 1, y)) };
        }
    }
}
