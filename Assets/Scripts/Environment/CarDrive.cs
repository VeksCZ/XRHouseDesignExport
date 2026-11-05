using UnityEngine;
using UnityEngine.InputSystem;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// The drivable car ("Trace routes"-style self-contained tool, needs no scene wiring - it bootstraps itself into
/// any scene that has a WalkThroughMode). A simple low-poly VW Passat B8 Variant silhouette, kinematic: it never
/// uses a Rigidbody/WheelCollider, because the walk-through moves the MODEL around the stationary camera rig
/// (WalkThroughMode's own doc comment explains why) - a physically simulated car can't live in a world that
/// teleports under it every frame. Instead, while driving, this script positions the model root itself every
/// frame exactly the way WalkThroughMode.Place() does for walking, from the car's own position/heading in model
/// space (carPos/carYaw) - so it is a second mover sharing the same trick, never two systems fighting over root.
///
/// Controls: desktop W/S = throttle/brake-reverse, A/D = steer, C near the car = get in/out (E is already
/// "export wall drawings" in the desktop app). VR: right trigger = gas, left trigger = brake/reverse, left stick
/// X = steer, left controller X button near the car = get in/out.
/// </summary>
public class CarDrive : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindFirstObjectByType<CarDrive>() || !FindFirstObjectByType<WalkThroughMode>()) return;
        new GameObject("CarDrive").AddComponent<CarDrive>();
    }

    // Real VW Passat B8 Variant proportions (metres).
    const float CarLength = 4.77f, CarWidth = 1.83f, CarHeight = 1.51f, Wheelbase = 2.79f, Track = 1.57f;
    const float WheelRadius = 0.33f, WheelThickness = 0.22f;
    const float MaxSpeed = 8.3f;        // ~30 km/h
    const float MaxReverse = 3.0f;
    const float Accel = 4.0f, BrakeDecel = 7.0f, Coast = 2.5f;
    const float MaxSteerDeg = 30f;
    const float TurnRateDegPerSec = 42f;
    const float InteractRange = 1.6f;

    const int WalkLayer = 2;            // built-in "Ignore Raycast" - same meaning as WalkThroughMode.WalkLayer
    const int WalkMask = 1 << WalkLayer;

    Transform car, body, cabin;
    Transform[] wheels = new Transform[4];   // FL, FR, RL, RR
    Transform[] wheelPivots = new Transform[4]; // steer pivot (front wheels use this; rear == wheel)
    float[] wheelSpinDeg = new float[4];

    Vector3 carPos;      // model space (child of the walk root), feet level
    float carYaw;        // degrees, model space
    float carSpeed;       // m/s, +forward
    float carPitch, carRoll;
    bool driving;
    bool built;
    bool noCarStart;    // this scan's spec has no "site.car_start" - never place the car at the model origin (could be inside the house)
    WalkThroughMode walk;
    DesktopWalkRig desktop;
    float lastEnterPressTime = -10f;

    // Seat / door points in the car's own local space (X = right, Z = forward, Y = up). Left-hand drive.
    static readonly Vector3 DriverEye = new Vector3(-0.42f, 1.22f, 0.25f);
    static readonly Vector3 DriverDoorGround = new Vector3(-1.05f, 0f, 0.3f);

    void Update()
    {
        walk = WalkThroughMode.Active;
        if (walk == null || !walk.IsOn) { if (built) DestroyCar(); driving = false; return; }
        if (!walk.TryGetModelFrame(out var root, out _, out _) || root == null) return;
        if (noCarStart) return;
        if (!built) BuildCar(root);
        if (car.parent != root) car.SetParent(root, false);

        if (!desktop) desktop = FindFirstObjectByType<DesktopWalkRig>();
        bool vr = !desktop;

        bool interact = vr
            ? OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.LTouch)
            : Keyboard.current != null && Keyboard.current.cKey.wasPressedThisFrame;

        if (driving)
        {
            Drive(Time.deltaTime, root, vr);
            if (interact) Exit(root);
        }
        else
        {
            car.gameObject.SetActive(true);
            if (interact && Time.time - lastEnterPressTime > 0.3f)
            {
                lastEnterPressTime = Time.time;
                TryEnter(root);
            }
        }
    }

    void TryEnter(Transform root)
    {
        Vector3 doorWorld = root.TransformPoint(carPos + Quaternion.Euler(0, carYaw, 0) * DriverDoorGround);
        var head = walk.HeadTransform;
        if (head == null || Vector3.Distance(head.position, doorWorld) > InteractRange)
        {
            walk.uiLog?.AddLog("Car: step up to the driver's door to get in");
            return;
        }
        driving = true;
        walk.DrivingCar = true;
        carSpeed = 0f;
        walk.uiLog?.AddLog(desktop
            ? "Car: driving - W/S gas+brake, A/D steer, C to get out"
            : "Car: driving - R trigger gas, L trigger brake, L stick steer, X to get out");
    }

    void Exit(Transform root)
    {
        driving = false;
        walk.DrivingCar = false;
        carSpeed = 0f;
        // Put the player's feet at the driver door, the model kept exactly where driving left it.
        Vector3 doorModel = carPos + Quaternion.Euler(0, carYaw, 0) * DriverDoorGround;
        var head = walk.HeadTransform;
        Vector3 feet = new Vector3(head.position.x, head.position.y, head.position.z);
        Vector3 wantFeet = root.TransformPoint(doorModel);
        root.position += wantFeet - new Vector3(feet.x, wantFeet.y, feet.z);
        Physics.SyncTransforms();
        walk.uiLog?.AddLog("Car: parked - stepped out");
    }

    void Drive(float dt, Transform root, bool vr)
    {
        float throttleIn, steerIn;
        if (vr)
        {
            float gas = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
            float brake = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.LTouch);
            throttleIn = Mathf.Clamp(gas - brake, -1f, 1f);
            steerIn = Mathf.Clamp(OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch).x, -1f, 1f);
        }
        else
        {
            throttleIn = desktop ? Mathf.Clamp(desktop.Move.y, -1f, 1f) : 0f;
            steerIn = desktop ? Mathf.Clamp(desktop.Move.x, -1f, 1f) : 0f;
        }

        // Throttle/brake: braking (input opposes current motion) decelerates harder than easing off does.
        float target = throttleIn >= 0 ? throttleIn * MaxSpeed : throttleIn * MaxReverse;
        float rate = Mathf.Sign(target - carSpeed) == Mathf.Sign(carSpeed) || carSpeed == 0f ? Accel : BrakeDecel;
        if (Mathf.Abs(throttleIn) < 0.02f) { target = 0f; rate = Coast; }
        carSpeed = Mathf.MoveTowards(carSpeed, target, rate * dt);

        // Steering only turns while rolling (a stationary car can't pivot) - and scales down near max speed.
        float speedFactor = Mathf.Clamp01(Mathf.Abs(carSpeed) / 1.2f) * Mathf.Lerp(1f, 0.55f, Mathf.Abs(carSpeed) / MaxSpeed);
        float steerSign = carSpeed >= 0f ? 1f : -1f;
        carYaw += steerIn * TurnRateDegPerSec * speedFactor * steerSign * dt;

        Quaternion rot = Quaternion.Euler(0, carYaw, 0);
        Vector3 wantDelta = rot * Vector3.forward * (carSpeed * dt);

        // A short forward "bumper" probe against the walk model's own colliders - stop at walls instead of
        // driving through them (the driveway/paving itself is the same collidable model, so it's never blocked).
        if (wantDelta.sqrMagnitude > 1e-10f)
        {
            Vector3 fromWorld = root.TransformPoint(carPos + rot * new Vector3(0, 0.6f, Mathf.Sign(carSpeed) * CarLength * 0.5f));
            Vector3 dirWorld = root.TransformVector(wantDelta.normalized);
            float dist = wantDelta.magnitude + 0.25f;
            if (Physics.SphereCast(fromWorld, 0.35f, dirWorld, out var hit, dist, WalkMask, QueryTriggerInteraction.Ignore))
            {
                float allowed = Mathf.Max(0f, hit.distance - 0.25f);
                wantDelta = wantDelta.normalized * Mathf.Min(wantDelta.magnitude, allowed);
                if (allowed < 0.02f) carSpeed = 0f;
            }
        }
        carPos += wantDelta;

        // Follow the terrain/driveway under the 4 wheels (same collidable model walking uses).
        Vector3 fwd = rot * Vector3.forward, right = rot * Vector3.right;
        bool okF = ProbeY(root, carPos + fwd * (Wheelbase * 0.5f), out float yF);
        bool okR = ProbeY(root, carPos - fwd * (Wheelbase * 0.5f), out float yR);
        bool okL = ProbeY(root, carPos + right * (Track * 0.5f), out float yRi);
        bool okRi = ProbeY(root, carPos - right * (Track * 0.5f), out float yLe);
        if (okF && okR && okL && okRi)
        {
            carPos.y = (yF + yR + yRi + yLe) * 0.25f;
            carPitch = Mathf.Clamp(Mathf.Atan2(yF - yR, Wheelbase) * Mathf.Rad2Deg, -20f, 20f);
            carRoll = Mathf.Clamp(Mathf.Atan2(yLe - yRi, Track) * Mathf.Rad2Deg, -20f, 20f);
        } // else: drove off the edge of the model - keep the last good height rather than falling

        // Place the car visual, and re-center the world on the driver's seat (exactly WalkThroughMode.Place()'s trick).
        car.localPosition = carPos;
        car.localRotation = rot;
        body.localRotation = Quaternion.Euler(carPitch, 0, -carRoll);
        SpinWheels(dt, steerIn * MaxSteerDeg);

        var head = walk.HeadTransform;
        Vector3 eyeModel = carPos + rot * DriverEye;
        root.position += head.position - root.TransformPoint(eyeModel);
        Physics.SyncTransforms();
    }

    static bool ProbeY(Transform root, Vector3 modelXZ, out float y)
    {
        y = 0f;
        Vector3 origin = root.TransformPoint(new Vector3(modelXZ.x, modelXZ.y + 1.5f, modelXZ.z));
        Vector3 dir = root.TransformVector(Vector3.down);
        if (!Physics.Raycast(origin, dir, out var hit, 4f, WalkMask, QueryTriggerInteraction.Ignore)) return false;
        y = root.InverseTransformPoint(hit.point).y;
        return true;
    }

    void SpinWheels(float dt, float steerDeg)
    {
        float spin = (carSpeed * dt / WheelRadius) * Mathf.Rad2Deg;
        for (int i = 0; i < 4; i++)
        {
            wheelSpinDeg[i] += spin;
            bool front = i < 2;
            if (front) wheelPivots[i].localRotation = Quaternion.Euler(0, steerDeg, 0);
            wheels[i].localRotation = Quaternion.Euler(0, 0, wheelSpinDeg[i]);
        }
    }

    // ---------- building the simple low-poly car (primitives only - no model assets needed) ----------

    void BuildCar(Transform root)
    {
        built = true;
        var go = new GameObject("Car");
        car = go.transform;
        car.SetParent(root, false);

        var site = Furniture.Load(HouseEditsStore.CurrentScan)?["site"] as Newtonsoft.Json.Linq.JObject;
        var cs = site?["car_start"] as Newtonsoft.Json.Linq.JArray;
        if (cs != null && cs.Count >= 2)
        {
            // GLB axes (x, z[, yawDeg]) -> model space, same mirror SiteModel/Furniture use elsewhere.
            carPos = new Vector3((float)cs[0], 0f, -(float)cs[1]);
            carYaw = cs.Count >= 3 ? -(float)cs[2] : 0f;
        }
        else
        {
            // No "site.car_start" in this scan's spec - the model origin is inside the house (near the entrance
            // room), never a safe fallback spot for the car. Skip it rather than spawning it indoors.
            built = false;
            noCarStart = true;
            Destroy(go);
            return;
        }

        var bodyGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
        bodyGo.name = "CarBody";
        body = bodyGo.transform;
        body.SetParent(car, false);
        Destroy(bodyGo.GetComponent<Collider>());
        var bodyMat = CarMaterial(new Color(0.12f, 0.15f, 0.20f));
        bodyGo.GetComponent<MeshRenderer>().sharedMaterial = bodyMat;
        body.localScale = new Vector3(CarWidth * 0.94f, CarHeight * 0.52f, CarLength);
        body.localPosition = new Vector3(0, WheelRadius + CarHeight * 0.26f, 0);

        var cabinGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cabinGo.name = "CarCabin";
        cabin = cabinGo.transform;
        cabin.SetParent(body, false);
        Destroy(cabinGo.GetComponent<Collider>());
        cabinGo.GetComponent<MeshRenderer>().sharedMaterial = CarMaterial(new Color(0.07f, 0.08f, 0.10f, 0.85f));
        // Child of the (non-uniformly scaled) body - divide by its scale to get metres again.
        cabin.localScale = new Vector3(1.45f / body.localScale.x, 0.55f / body.localScale.y, 2.5f / body.localScale.z);
        cabin.localPosition = new Vector3(0, 0.72f, -0.05f);

        var wheelMat = CarMaterial(new Color(0.03f, 0.03f, 0.03f));
        float[] sx = { -1f, 1f, -1f, 1f }, sz = { 1f, 1f, -1f, -1f };
        for (int i = 0; i < 4; i++)
        {
            var pivotGo = new GameObject(i < 2 ? "WheelSteer" : "WheelPivot");
            var pivot = pivotGo.transform;
            pivot.SetParent(car, false);
            pivot.localPosition = new Vector3(sx[i] * Track * 0.5f, WheelRadius, sz[i] * Wheelbase * 0.5f);
            wheelPivots[i] = pivot;

            var wGo = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            wGo.name = "Wheel";
            Destroy(wGo.GetComponent<Collider>());
            wGo.GetComponent<MeshRenderer>().sharedMaterial = wheelMat;
            var w = wGo.transform;
            w.SetParent(pivot, false);
            w.localRotation = Quaternion.Euler(0, 0, 90f);   // cylinder axis -> local X (the axle)
            w.localScale = new Vector3(WheelThickness, WheelRadius, WheelRadius);
            wheels[i] = w;
        }

        // Every part here is purely visual (no colliders): it must never block the walk-through's own raycasts/
        // fade check, and WalkLayer colliders are for the house model, not for things that move every frame.
        foreach (var mf in car.GetComponentsInChildren<MeshFilter>()) mf.gameObject.layer = 0;

        car.localPosition = carPos;
        car.localRotation = Quaternion.Euler(0, carYaw, 0);
    }

    static Material CarMaterial(Color c)
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit")
                  ?? Shader.Find("Universal Render Pipeline/Unlit")
                  ?? Shader.Find("Standard")
                  ?? Shader.Find("Unlit/Color");
        return new Material(shader) { color = c };
    }

    void DestroyCar()
    {
        if (car) Destroy(car.gameObject);
        car = null; body = null; cabin = null;
        wheels = new Transform[4]; wheelPivots = new Transform[4]; wheelSpinDeg = new float[4];
        built = false;
    }

    void OnDestroy() => DestroyCar();
}
