using UnityEngine;

/// <summary>
/// A character controller script for the player that avoids the naming conflict 
/// with the built-in UnityEngine.CharacterController.
/// </summary>
public class PlayerController : MonoBehaviour
{
    [Header("Movement Settings")]
    public float moveSpeed = 2.0f;
    public float gravity = 9.81f;

    private CharacterController m_Controller; // This refers to the built-in component
    private Transform mainCameraTransform;
    private Vector3 velocity;

    void Start()
    {
        m_Controller = GetComponent<CharacterController>();
        if (Camera.main != null)
        {
            mainCameraTransform = Camera.main.transform;
        }

        if (m_Controller == null)
        {
            Debug.LogError("No CharacterController component found on this GameObject. Adding one...");
            m_Controller = gameObject.AddComponent<CharacterController>();
        }
    }

    void Update()
    {
        if (m_Controller == null) return;

        // Sync character controller height and center with the VR headset height if camera is found
        if (mainCameraTransform != null)
        {
            UpdateCharacterControllerHeight();
        }

        // Basic movement logic can go here (e.g., following hand controllers)
        ApplyGravity();
    }

    private void UpdateCharacterControllerHeight()
    {
        // Adjust the height of the CharacterController to match the headset's height (y position relative to rig)
        // Clamp between 1m and 2.2m
        float height = Mathf.Clamp(mainCameraTransform.localPosition.y, 1.0f, 2.2f);
        m_Controller.height = height;
        
        // Center should be half of the height
        m_Controller.center = new Vector3(0, height / 2.0f + m_Controller.skinWidth, 0);
    }

    private void ApplyGravity()
    {
        if (m_Controller.isGrounded)
        {
            velocity.y = -0.1f;
        }
        else
        {
            velocity.y -= gravity * Time.deltaTime;
        }

        m_Controller.Move(velocity * Time.deltaTime);
    }
}
