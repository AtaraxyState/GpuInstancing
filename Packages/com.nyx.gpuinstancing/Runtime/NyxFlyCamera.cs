using UnityEngine;

namespace Nyx.GpuInstancing
{
    /// <summary>
    /// Caméra libre de la scène de démo (ancien Input Manager) :
    ///  - clic droit maintenu + souris : regarder ;
    ///  - ZQSD (AZERTY) ou WASD (QWERTY), ou flèches : se déplacer ;
    ///  - A / E (AZERTY) ou Q / E (QWERTY), ou Page préc. / Page suiv. : descendre / monter ;
    ///  - Maj : accélérer ; molette : régler la vitesse.
    /// </summary>
    [AddComponentMenu("Nyx/GPU Instancing/Fly Camera (demo)")]
    [DisallowMultipleComponent]
    public sealed class NyxFlyCamera : MonoBehaviour
    {
        public enum KeyboardLayout
        {
            Azerty,
            Qwerty
        }

        [SerializeField] KeyboardLayout keyboardLayout = KeyboardLayout.Azerty;
        [SerializeField, Min(0.01f)] float moveSpeed = 5f;
        [SerializeField, Min(1f)] float boostMultiplier = 4f;
        [SerializeField, Min(0.01f)] float lookSensitivity = 2f;

        float _yaw;
        float _pitch;
        bool _looking;

        void OnEnable()
        {
            Vector3 euler = transform.eulerAngles;
            _yaw = euler.y;
            _pitch = euler.x > 180f ? euler.x - 360f : euler.x;
        }

        void OnDisable() => SetLooking(false);

#if ENABLE_LEGACY_INPUT_MANAGER
        void Update()
        {
            if (Input.GetMouseButtonDown(1))
                SetLooking(true);
            if (Input.GetMouseButtonUp(1))
                SetLooking(false);
            if (_looking)
            {
                _yaw += Input.GetAxisRaw("Mouse X") * lookSensitivity;
                _pitch = Mathf.Clamp(_pitch - Input.GetAxisRaw("Mouse Y") * lookSensitivity, -89f, 89f);
                transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            }

            float scroll = Input.mouseScrollDelta.y;
            if (scroll != 0f)
                moveSpeed = Mathf.Clamp(moveSpeed * Mathf.Pow(1.2f, scroll), 0.2f, 100f);

            bool azerty = keyboardLayout == KeyboardLayout.Azerty;
            var input = Vector3.zero;
            if (Input.GetKey(azerty ? KeyCode.Z : KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) input.z += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) input.z -= 1f;
            if (Input.GetKey(azerty ? KeyCode.Q : KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) input.x -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) input.x += 1f;
            if (Input.GetKey(KeyCode.E) || Input.GetKey(KeyCode.PageUp)) input.y += 1f;
            if (Input.GetKey(azerty ? KeyCode.A : KeyCode.Q) || Input.GetKey(KeyCode.PageDown)) input.y -= 1f;
            if (input == Vector3.zero)
                return;
            bool boost = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            Vector3 direction = transform.right * input.x + transform.forward * input.z + Vector3.up * input.y;
            transform.position += Vector3.ClampMagnitude(direction, 1f) * (moveSpeed * (boost ? boostMultiplier : 1f) * Time.unscaledDeltaTime);
        }
#else
        void Start() => Debug.LogWarning("NyxFlyCamera : nécessite l'ancien Input Manager (Project Settings > Player > Active Input Handling).", this);
#endif

        void SetLooking(bool looking)
        {
            _looking = looking;
            Cursor.lockState = looking ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !looking;
        }
    }
}
