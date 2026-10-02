using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Makes a world-space panel draggable with the right grip, anywhere on it (the trigger keeps clicking its
/// buttons). Same one-grab-at-a-time rule as the other panels (GrabLock). 'Moved' tells the owner the user has
/// placed it themselves, so it shouldn't be re-positioned automatically any more.
/// </summary>
public class PanelDrag : MonoBehaviour
{
    public bool Moved { get; private set; }

    Transform hand;
    bool grabbed;
    Vector3 off;
    Quaternion rotOff;

    /// <summary>Adds a grab collider covering the canvas rect (canvas units) and the drag behaviour.</summary>
    public static PanelDrag Attach(Canvas canvas, float width, float height)
    {
        var go = canvas.gameObject;
        var col = go.AddComponent<BoxCollider>();
        // Canvas rect is top-left anchored content inside a centred RectTransform of this size.
        col.center = Vector3.zero;
        col.size = new Vector3(width, height, 10f);
        go.AddComponent<XRSimpleInteractable>(); // ray turns green over it, like the other draggable panels
        return go.AddComponent<PanelDrag>();
    }

    void Update()
    {
        if (!hand)
        {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            hand = rig ? rig.rightHandAnchor : null;
            if (!hand) return;
        }
        bool grip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
        if (!grabbed)
        {
            if (GrabLock.GripPressed && Physics.Raycast(hand.position, hand.forward, out var hit) && hit.collider.gameObject == gameObject && GrabLock.TryTake(this))
            {
                grabbed = true;
                Moved = true;
                off = hand.InverseTransformPoint(transform.position);
                rotOff = Quaternion.Inverse(hand.rotation) * transform.rotation;
            }
        }
        else
        {
            if (!grip) { grabbed = false; GrabLock.Release(this); return; }
            transform.SetPositionAndRotation(hand.TransformPoint(off), hand.rotation * rotOff);
        }
    }
}
