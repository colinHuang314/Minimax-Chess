using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

/// <summary>
/// Simple first-person walk-around controller: WASD to move, mouse to look,
/// space to jump, hold left-shift to sprint. Attach to a GameObject with a
/// CharacterController (the script will add one if missing) and put your
/// main Camera as a child at eye height.
///
/// Uses the new Input System package (UnityEngine.InputSystem) directly via
/// Keyboard.current / Mouse.current - no .inputactions asset required. Your
/// project's Active Input Handling (Player Settings) must be set to "Input
/// System Package (New)" or "Both". Requires the Input System package
/// (Window > Package Manager > Input System) and an assembly reference to
/// "Unity.InputSystem" if your scripts are in an asmdef.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PlayerMovement : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Camera used for looking around. If left empty, will try Camera.main.")]
    public Camera playerCamera;

    [Header("Movement")]
    public float walkSpeed = 4f;
    public float sprintSpeed = 7.5f;
    public float jumpHeight = 1.2f;
    public float gravity = -19.6f;

    [Header("Mouse Look")]
    public float mouseSensitivity = 2f;
    public float minLookAngle = -85f;
    public float maxLookAngle = 85f;
    [Tooltip("If true, cursor starts locked/hidden. Set false if you have a menu/UI to click through first.")]
    public bool lockCursorOnStart = false;

    [Header("Ground Check")]
    public LayerMask groundMask = ~0;

    private CharacterController _controller;
    private Vector3 _velocity;
    private float _pitch;
    private bool _cursorLocked;

    void Awake()
    {
        _controller = GetComponent<CharacterController>();
        if (playerCamera == null) playerCamera = Camera.main;

        if (playerCamera != null)
        {
            // Preserve the initial camera pitch so locking the cursor doesn't snap it to zero.
            float initialPitch = playerCamera.transform.localEulerAngles.x;
            if (initialPitch > 180f) initialPitch -= 360f;
            _pitch = Mathf.Clamp(initialPitch, minLookAngle, maxLookAngle);
        }
    }

    void OnEnable()
    {
        SetCursorLock(lockCursorOnStart);
    }

    void Update()
    {
        HandleCursorToggle();
        HandleLook();
        HandleMove();
    }

    private void HandleCursorToggle()
    {
        var keyboard = Keyboard.current;
        var mouse = Mouse.current;

        // Press Escape to free the cursor, click the game view (not UI) to re-lock.
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
        {
            SetCursorLock(false);
        }
        else if (!_cursorLocked && mouse != null && mouse.leftButton.wasPressedThisFrame)
        {
            if (IsPointerOverUI()) return; // let the UI have the click, don't grab the cursor
            SetCursorLock(true);
        }
    }

    private bool IsPointerOverUI()
    {
        var es = EventSystem.current;
        if (es == null) return false;

        // Works for both mouse-driven UI and the new Input System's pointer.
        if (Mouse.current != null)
        {
            // EventSystem.IsPointerOverGameObject() without an id checks the
            // last known pointer position, which works fine for mouse input.
            return es.IsPointerOverGameObject();
        }
        return false;
    }

    private void SetCursorLock(bool locked)
    {
        _cursorLocked = locked;
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    private void HandleLook()
    {
        if (!_cursorLocked || playerCamera == null) return;

        var mouse = Mouse.current;
        if (mouse == null) return;

        // delta is in pixels/frame; scale down so mouseSensitivity feels similar
        // to the old Input.GetAxis("Mouse X") range.
        Vector2 delta = mouse.delta.ReadValue() * (mouseSensitivity * 0.05f);
        float mouseX = delta.x;
        float mouseY = delta.y;

        // Yaw rotates the whole body (so movement direction follows look direction).
        transform.Rotate(Vector3.up * mouseX);

        // Pitch only rotates the camera, clamped so you can't flip over.
        _pitch -= mouseY;
        _pitch = Mathf.Clamp(_pitch, minLookAngle, maxLookAngle);
        playerCamera.transform.localEulerAngles = new Vector3(_pitch, 0f, 0f);
    }

    private void HandleMove()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        bool grounded = _controller.isGrounded;
        if (grounded && _velocity.y < 0f)
            _velocity.y = -2f; // small downward force to keep grounded flag stable

        float h = 0f, v = 0f;
        if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) h -= 1f;
        if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) h += 1f;
        if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) v -= 1f;
        if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) v += 1f;

        Vector3 move = (transform.right * h + transform.forward * v);
        if (move.sqrMagnitude > 1f) move.Normalize();

        float speed = keyboard.leftShiftKey.isPressed ? sprintSpeed : walkSpeed;
        _controller.Move(move * speed * Time.deltaTime);

        if (keyboard.spaceKey.wasPressedThisFrame)
        {
            // if (grounded)
            // v = sqrt(h * -2 * g)
            _velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }

        _velocity.y += gravity * Time.deltaTime;
        _controller.Move(_velocity * Time.deltaTime);
    }
}