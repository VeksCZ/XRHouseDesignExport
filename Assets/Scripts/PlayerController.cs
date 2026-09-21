using UnityEngine;

/// <summary>
/// Leftover from an early prototype and still attached to the OVRCameraRig in the scene. It applied gravity
/// to a CharacterController on the rig, but MRUK's world lock owns the tracking space, so the rig fell without
/// end (hundreds of metres) while MRUK logged a warning every frame - the frame rate collapsed to ~1 fps.
/// Nothing in this app needs walking physics, so it removes the CharacterController and switches itself off;
/// the component only stays so the existing scene keeps loading without a missing-script warning.
/// </summary>
public class PlayerController : MonoBehaviour
{
    void Awake()
    {
        if (TryGetComponent<CharacterController>(out var controller)) Destroy(controller);
        enabled = false;
    }
}
