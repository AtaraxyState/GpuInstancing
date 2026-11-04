using UnityEngine;

/// <summary>
/// Caméra libre pour explorer une scène en Play mode (ancien Input Manager).
///  - clic droit maintenu + souris : regarder (curseur verrouillé seulement pendant le clic) ;
///  - ZQSD (AZERTY) ou WASD (QWERTY) : avancer, reculer, aller à gauche ou à droite ;
///  - A / E (AZERTY) ou Q / E (QWERTY) : descendre / monter ;
///  - flèches et Page préc. / Page suiv. : mêmes déplacements, quel que soit le clavier ;
///  - Maj : accélérer ; molette : régler la vitesse.
/// </summary>
[AddComponentMenu("Nyx/Free Camera Controller")]
[DisallowMultipleComponent]
public sealed class FreeCameraController : MonoBehaviour
{
    public enum KeyboardLayout
    {
        Azerty,
        Qwerty
    }

    [SerializeField] KeyboardLayout keyboardLayout = KeyboardLayout.Azerty;
    [Tooltip("Vitesse de déplacement (m/s), réglable à la molette.")]
    [SerializeField, Min(0.01f)] float moveSpeed = 5f;
    [SerializeField] Vector2 speedRange = new Vector2(0.2f, 100f);
    [Tooltip("Facteur appliqué à la vitesse par cran de molette.")]
    [SerializeField, Range(1.01f, 2f)] float scrollSpeedStep = 1.2f;
    [Tooltip("Multiplicateur de vitesse avec Maj.")]
    [SerializeField, Min(1f)] float boostMultiplier = 4f;
    [Tooltip("Degrés par unité de déplacement de la souris.")]
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

    void OnDisable() => StopLooking();

    void Update()
    {
        UpdateLook();
        UpdateSpeed();
        UpdateMove();
    }

    void UpdateLook()
    {
        if (Input.GetMouseButtonDown(1))
        {
            _looking = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        if (Input.GetMouseButtonUp(1))
            StopLooking();
        if (!_looking)
            return;

        _yaw += Input.GetAxisRaw("Mouse X") * lookSensitivity;
        _pitch = Mathf.Clamp(_pitch - Input.GetAxisRaw("Mouse Y") * lookSensitivity, -89f, 89f);
        transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
    }

    void UpdateSpeed()
    {
        float scroll = Input.mouseScrollDelta.y;
        if (scroll != 0f)
            moveSpeed = Mathf.Clamp(moveSpeed * Mathf.Pow(scrollSpeedStep, scroll), speedRange.x, speedRange.y);
    }

    void UpdateMove()
    {
        bool azerty = keyboardLayout == KeyboardLayout.Azerty;
        var input = Vector3.zero;
        if (Input.GetKey(azerty ? KeyCode.Z : KeyCode.W) || Input.GetKey(KeyCode.UpArrow))
            input.z += 1f;
        if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))
            input.z -= 1f;
        if (Input.GetKey(azerty ? KeyCode.Q : KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))
            input.x -= 1f;
        if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow))
            input.x += 1f;
        if (Input.GetKey(KeyCode.E) || Input.GetKey(KeyCode.PageUp))
            input.y += 1f;
        if (Input.GetKey(azerty ? KeyCode.A : KeyCode.Q) || Input.GetKey(KeyCode.PageDown))
            input.y -= 1f;
        if (input == Vector3.zero)
            return;

        bool boost = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        float speed = moveSpeed * (boost ? boostMultiplier : 1f);
        // Avant / côtés selon le regard, montée / descente verticales
        Vector3 direction = transform.right * input.x + transform.forward * input.z + Vector3.up * input.y;
        transform.position += Vector3.ClampMagnitude(direction, 1f) * (speed * Time.unscaledDeltaTime);
    }

    void StopLooking()
    {
        _looking = false;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }
}
