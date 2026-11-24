using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace PlayShift.FirstPerson.Editor
{
    /// <summary>
    /// Builds the reusable PlayShift first-person player prefab:
    ///
    ///   PlayShiftFirstPersonPlayer   CharacterController, PlayShiftFirstPersonRig, Easy Peasy InputManager + controller
    ///     CameraParent (eye height; the controller's subtle head bob moves this)
    ///       PlayerCamera (MainCamera, the only AudioListener)
    ///         ToolAnchor (camera-relative grip point for a held tool, bottom-right)
    ///     GroundCheck (feet)
    ///
    /// No body, hands or arms. Games add their own tool/interaction components to an instance (Cut the Grass adds the
    /// trimmer). The Easy Peasy asset is optional and found by type name (it is not in git: Asset Store licence):
    /// without it the prefab still works and the game's built-in movement drives it.
    ///
    /// Controller tuning (spec): walk 3.5 m/s, sprint 5.5 m/s (unlimited), eye height 1.7 m, vertical FOV 65,
    /// extremely subtle head bob, no strafe tilt / sprint zoom / jump / slide / crouch / ledge grab / swimming.
    /// </summary>
    public static class PlayShiftFirstPersonBuilder
    {
        public const string PrefabPath = "Assets/PlayShift/FirstPerson/PlayShiftFirstPersonPlayer.prefab";
        public const float EyeHeight = 1.7f;
        public const float WalkSpeed = 3.5f, SprintSpeed = 5.5f, DefaultVerticalFov = 65f;
        private const string InputTypeName = "EasyPeasyFirstPersonController.InputManager";
        private const int IgnoreRaycastLayer = 2;

        /// <summary>A component type by full name (the asset is not referenced at compile time).</summary>
        public static Type FindType(string fullName) =>
            TypeCache.GetTypesDerivedFrom<Component>().FirstOrDefault(t => t.FullName == fullName);

        public static bool EasyPeasyAvailable =>
            FindType(PlayShiftFirstPersonRig.ControllerTypeName) != null && FindType(InputTypeName) != null;

        [MenuItem("PlayShift/First Person/Rebuild Player Prefab", priority = 1)]
        public static void RebuildFromMenu()
        {
            var p = BuildPrefab();
            if (p != null) { Selection.activeObject = p; EditorGUIUtility.PingObject(p); }
        }

        /// <summary>The prefab, (re)built if missing or if Easy Peasy was imported after it was made.</summary>
        public static GameObject EnsurePrefab()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab != null && (!EasyPeasyAvailable || HasEasyPeasy(prefab))) return prefab;
            return BuildPrefab();
        }

        public static bool HasEasyPeasy(GameObject go)
        {
            var t = FindType(PlayShiftFirstPersonRig.ControllerTypeName);
            return t != null && go.GetComponent(t) != null;
        }

        public static GameObject BuildPrefab()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrefabPath));
            var root = new GameObject("PlayShiftFirstPersonPlayer");
            try
            {
                // The controller's ground check overlaps every layer in its mask; the player sits on Ignore Raycast so
                // it never detects itself.
                root.layer = IgnoreRaycastLayer;
                var cc = root.AddComponent<CharacterController>();
                cc.height = 1.8f; cc.radius = 0.3f; cc.center = new Vector3(0f, 0.9f, 0f);
                cc.stepOffset = 0.35f; cc.slopeLimit = 45f; cc.skinWidth = 0.04f;

                var cameraParent = new GameObject("CameraParent").transform;
                cameraParent.SetParent(root.transform, false);
                cameraParent.localPosition = new Vector3(0f, EyeHeight, 0f);
                var camGo = new GameObject("PlayerCamera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
                camGo.transform.SetParent(cameraParent, false);
                var cam = camGo.GetComponent<Camera>();
                cam.nearClipPlane = 0.03f;
                cam.farClipPlane = 1000f;
                cam.fieldOfView = DefaultVerticalFov;
                var toolAnchor = new GameObject("ToolAnchor").transform;
                toolAnchor.SetParent(camGo.transform, false);
                toolAnchor.localPosition = new Vector3(0.42f, -0.5f, 0.55f); // bottom-right grip, in front of the near plane
                var groundCheck = new GameObject("GroundCheck").transform;
                groundCheck.SetParent(root.transform, false);

                var rig = root.AddComponent<PlayShiftFirstPersonRig>();
                var so = new SerializedObject(rig);
                so.FindProperty("playerCamera").objectReferenceValue = cam;
                so.FindProperty("toolAnchor").objectReferenceValue = toolAnchor;
                so.ApplyModifiedPropertiesWithoutUndo();

                AttachEasyPeasy(root, cam.transform, cameraParent, groundCheck);

                var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                Debug.Log($"[PlayShift] First-person player prefab saved: {PrefabPath} (Easy Peasy controller: {(EasyPeasyAvailable ? "yes" : "not in project, built-in movement")}).");
                return prefab;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        /// <summary>Adds and configures the Easy Peasy input + controller. False when the asset is not in the project.</summary>
        public static bool AttachEasyPeasy(GameObject root, Transform camera, Transform cameraParent, Transform groundCheck)
        {
            var controllerType = FindType(PlayShiftFirstPersonRig.ControllerTypeName);
            var inputType = FindType(InputTypeName);
            if (controllerType == null || inputType == null) return false;

            root.AddComponent(inputType);
            var controller = root.AddComponent(controllerType);
            var so = new SerializedObject(controller);
            void Obj(string n, UnityEngine.Object v) { var p = so.FindProperty(n); if (p != null) p.objectReferenceValue = v; }
            void Num(string n, float v) { var p = so.FindProperty(n); if (p != null) p.floatValue = v; }
            void Flag(string n, bool v) { var p = so.FindProperty(n); if (p != null) p.boolValue = v; }
            void Mask(string n, int v) { var p = so.FindProperty(n); if (p != null) p.intValue = v; }

            Obj("playerCamera", camera);
            Obj("cameraParent", cameraParent);
            Obj("groundCheck", groundCheck);
            Mask("groundMask", ~(1 << IgnoreRaycastLayer));
            Mask("ledgeLayer", 0);   // no ledge grabbing
            Mask("waterMask", 0);    // no swimming

            // Movement: smooth but responsive (about 0.2 s to full speed), unlimited sprint (the controller has no stamina).
            Num("walkSpeed", WalkSpeed);
            Num("sprintSpeed", SprintSpeed);
            Num("groundAcceleration", 18f);
            Num("groundDeceleration", 24f);
            // Unused mechanics neutralised: no jump height, crouch/slide do nothing visible and keep walking speed.
            Num("jumpSpeed", 0f);
            Num("crouchSpeed", WalkSpeed);
            Num("slideSpeed", SprintSpeed);
            Num("slideDuration", 0.01f);
            Num("slideFovBoost", 0f);
            Num("crouchingCameraHeight", EyeHeight);
            Num("crouchingCharacterControllerHeight", 1.8f);
            Flag("enableSlopeSliding", false);

            // Camera: eye height, FOV 65 (the game overrides it from its settings), extremely subtle bob, no sway/tilt/zoom.
            Num("standingCameraHeight", EyeHeight);
            Num("normalFov", DefaultVerticalFov);
            Num("sprintFov", DefaultVerticalFov);
            Num("mouseSensitivity", 2f);
            Num("strafeTiltAmount", 0f);
            Num("bobAmount", 0.006f);
            Num("bobSpeed", 10f);
            Flag("useHeadBob", false);
            Flag("useCameraTilt", false);
            Flag("useFovKick", false);
            Flag("useClimbTilt", false);
            Flag("currentStateDebug", false);
            so.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }
    }
}
