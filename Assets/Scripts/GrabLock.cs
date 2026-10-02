using UnityEngine;

/// <summary>
/// One grab at a time for the right-hand grip drags (menu, Dollhouse, minimap, floor plans). A drag may only start
/// on the frame the grip is pressed, on whatever the ray hits first at that moment, and holds the lock until the
/// grip is released - so sweeping the ray over another panel mid-drag can never pick that one up too.
/// </summary>
public static class GrabLock
{
    static Object owner;
    static int ownerFrame = -1;

    /// <summary>True only on the frame the right grip goes down.</summary>
    public static bool GripPressed => OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);

    /// <summary>Only ever called on a grip press. A new press means any earlier drag has ended (the grip was
    /// released in between), so an older owner never blocks - even one that missed its own release (closed or
    /// hidden mid-drag); only a second taker in the very same frame is refused.</summary>
    public static bool TryTake(Object who)
    {
        if (owner && ownerFrame == Time.frameCount && owner != who) return false;
        owner = who;
        ownerFrame = Time.frameCount;
        return true;
    }

    public static void Release(Object who)
    {
        if (owner == who) owner = null;
    }
}
