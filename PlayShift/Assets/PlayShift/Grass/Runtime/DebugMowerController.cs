using UnityEngine;

namespace PlayShift.Grass
{
    /// <summary>
    /// Minimal WASD/arrow-key mower for testing the grass system in an empty scene. Your real mower only
    /// needs a <see cref="GrassCutter"/>; this component is optional.
    /// </summary>
    public sealed class DebugMowerController : MonoBehaviour
    {
        [SerializeField] private float speed = 7f;
        [SerializeField] private float turnSpeed = 140f;
        [Tooltip("Hold Shift to go this many times faster.")]
        [SerializeField] private float boost = 3f;
        [SerializeField] private Transform followCamera;
        [SerializeField] private Vector3 cameraOffset = new Vector3(0f, 6f, -8f);

        private void Update()
        {
            ReadInput(out float forward, out float turn, out bool fast);
            float dt = Time.deltaTime;
            transform.Rotate(0f, turn * turnSpeed * dt, 0f, Space.World);
            transform.position += transform.forward * (forward * speed * (fast ? boost : 1f) * dt);
        }

        private void LateUpdate()
        {
            if (followCamera == null) return;
            Vector3 target = transform.position + transform.rotation * cameraOffset;
            followCamera.position = Vector3.Lerp(followCamera.position, target, 1f - Mathf.Exp(-6f * Time.deltaTime));
            followCamera.LookAt(transform.position + Vector3.up * 0.5f);
        }

        private static void ReadInput(out float forward, out float turn, out bool fast)
        {
            forward = 0f; turn = 0f; fast = false;
#if ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) forward += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) forward -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) turn += 1f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) turn -= 1f;
            fast = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
#elif ENABLE_LEGACY_INPUT_MANAGER
            forward = Input.GetAxisRaw("Vertical");
            turn = Input.GetAxisRaw("Horizontal");
            fast = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
#endif
        }
    }
}
