using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Keyboard + mouse "headset" for the Windows desktop app. WalkThroughMode treats it like the Quest rig: this
/// transform is the tracking space (floor level, never moved by walking - the model moves instead, exactly as on
/// the Quest), the child camera is the head at eyeHeight. The mouse turns the rig (yaw) and tilts the camera
/// (pitch); WASD/arrows give the move vector WalkThroughMode reads instead of the left stick, Shift runs.
/// </summary>
[DefaultExecutionOrder(-50)] // look/move input is read before WalkThroughMode.Update uses it
public class DesktopWalkRig : MonoBehaviour
{
    public Camera cam;
    public float eyeHeight = 1.65f;
    [Tooltip("Degrees per mouse count.")]
    public float mouseSensitivity = 0.12f;
    public float runMultiplier = 2.2f;
    public float slowMultiplier = 0.4f;

    /// <summary>Off while a menu is open (cursor free): no looking, no moving.</summary>
    public bool InputEnabled { get; set; } = true;
    public Transform Head => cam ? cam.transform : null;
    /// <summary>Left-stick equivalent: x = strafe, y = forward, each -1..1.</summary>
    public Vector2 Move { get; private set; }
    public float SpeedMultiplier { get; private set; } = 1f;

    float yaw, pitch;

    /// <summary>Tests: a move vector applied instead of the keyboard while set (InputEnabled not required).</summary>
    public Vector2? ForcedMove { get; set; }

    /// <summary>Looks in the given direction (degrees; yaw around up, pitch positive = down).</summary>
    public void SetView(float yawDeg, float pitchDeg)
    {
        yaw = yawDeg; pitch = Mathf.Clamp(pitchDeg, -89f, 89f);
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        if (cam) cam.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    void Awake()
    {
        if (!cam) cam = GetComponentInChildren<Camera>();
        if (cam)
        {
            cam.transform.localPosition = new Vector3(0f, eyeHeight, 0f);
            cam.transform.localRotation = Quaternion.identity;
        }
        yaw = transform.eulerAngles.y;
    }

    void Update()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        Move = Vector2.zero;
        SpeedMultiplier = 1f;
        if (ForcedMove.HasValue) { Move = ForcedMove.Value; return; }
        if (!InputEnabled || kb == null || mouse == null) return;

        Vector2 d = mouse.delta.ReadValue() * mouseSensitivity;
        yaw += d.x;
        pitch = Mathf.Clamp(pitch - d.y, -89f, 89f);
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        if (cam) cam.transform.localRotation = Quaternion.Euler(pitch, 0f, 0f);

        Vector2 m = Vector2.zero;
        if (kb.wKey.isPressed || kb.upArrowKey.isPressed) m.y += 1f;
        if (kb.sKey.isPressed || kb.downArrowKey.isPressed) m.y -= 1f;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) m.x += 1f;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) m.x -= 1f;
        Move = Vector2.ClampMagnitude(m, 1f);
        if (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed) SpeedMultiplier = runMultiplier;
        else if (kb.leftCtrlKey.isPressed) SpeedMultiplier = slowMultiplier;
    }

    /// <summary>Looks level in the given direction (after a scan load).</summary>
    public void ResetView(float yawDeg = 0f)
    {
        yaw = yawDeg; pitch = 0f;
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        if (cam) cam.transform.localRotation = Quaternion.identity;
    }
}
