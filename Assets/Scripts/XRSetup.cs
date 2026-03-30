using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using Unity.XR.CoreUtils;

public class XRSetup : MonoBehaviour
{
    public GameObject leftControllerPrefab;
    public GameObject rightControllerPrefab;
    public GameObject xrOriginPrefab;
    public GameObject xrMenuPrefab;

    void Awake()
    {
        // Instantiate XR Origin (Rig)
        if (xrOriginPrefab != null && Object.FindFirstObjectByType<XROrigin>() == null)
        {
            Instantiate(xrOriginPrefab);
        }

        // Instantiate Controllers if not already present
        if (leftControllerPrefab != null && GameObject.Find("LeftHand Controller") == null)
        {
            Instantiate(leftControllerPrefab);
        }
        if (rightControllerPrefab != null && GameObject.Find("RightHand Controller") == null)
        {
            Instantiate(rightControllerPrefab);
        }

        // Instantiate XR Menu
        if (xrMenuPrefab != null && GameObject.Find("XRMenuCanvas") == null)
        {
            Instantiate(xrMenuPrefab);
        }
    }
}
