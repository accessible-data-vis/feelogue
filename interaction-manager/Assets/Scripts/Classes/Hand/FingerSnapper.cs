using Leap;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

[RequireComponent(typeof(Collider), typeof(Rigidbody))]
public class FingerSnapper : MonoBehaviour
{
    [Header("References")]
    public Transform RTDAnchor;
    public Leap.LeapServiceProvider leapProvider;

    [Header("Snap Tuning")]
    [Tooltip("Max distance above surface to engage snapping (m).")]
    public float hoverMargin = 0.010f;         // 10 mm
    [Tooltip("Keep at least this clearance along surface normal (m).")]
    public float surfacePad = -0.001f;       //0.0010f;        // 1.0 mm
    [Range(0f, 1f)] public float lerp = 0.85f;

    [Header("Hit Filtering")]
    [Tooltip("Reject hits whose normal deviates more than this from bed normal (deg).")]
    [Range(0f, 89f)] public float maxNormalAngle = 25f;
    [Tooltip("Reject hits whose lateral (in-plane) distance from the tip exceeds this (m).")]
    public float maxLateralFromTip = 0.010f;              // 10 mm

    [Header("Raycast")]
    [Tooltip("Start ray/spherecast this far above the tip along bed normal (m).")]
    public float rayOffset = 0.02f;
    [Tooltip("Maximum cast length (m).")]
    public float maxRayDistance = 0.12f;
    [Tooltip("Use SphereCast with tip radius (better against side hits).")]
    public bool useSphereCast = true;

    [Header("Auto tip collider")]
    public bool setTipSphereRadius = true;
    public float tipSphereRadius = 0.0075f;  // ≈7.5 mm

    [Header("Hand Settings")]
    public Leap.Chirality handedness = Leap.Chirality.Right;
    public int fingerIndex = 1;

    // ───────── internal ─────────
    private Rigidbody _rb;
    private int _pinsMask, _selfMask;
    private Vector3 _planeN, _planeP0;
    private bool isTouchingPin = false;
    private Vector3 frozenPosition;

    static class GizmoStyles
    {
        // cache to avoid per-frame allocations
        public static readonly GUIStyle Label = new GUIStyle(EditorStyles.whiteLabel)
        {
            fontSize = 13,
            fontStyle = FontStyle.Normal,
            alignment = TextAnchor.UpperLeft
        };
    }


    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.isKinematic = true;
        _rb.useGravity = false;
        _rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
        _rb.interpolation = RigidbodyInterpolation.Interpolate;

        _pinsMask = LayerMask.GetMask("Pins");
        _selfMask = LayerMask.GetMask("Fingers");

        if (setTipSphereRadius && TryGetComponent<SphereCollider>(out var sc))
            sc.radius = tipSphereRadius;
    }

    void Start()
    {
        // Build plane after RTDAligner has positioned the anchor
        RebuildBedPlane();
    }

    void OnEnable()
    {
        // Delay rebuild to ensure RTDAligner has run
        if (Application.isPlaying)
            Invoke(nameof(RebuildBedPlane), 0.1f);
    }

    void OnTransformParentChanged()
    {
        RebuildBedPlane(); 
    }

    void RebuildBedPlane()
    {
        if (!RTDAnchor)
        {
            Debug.LogWarning($"[{name}] RTDAnchor not assigned!");
            return;
        }

        // Simplified: use anchor's up vector directly (no flipping needed with correct setup)
        //_planeN = RTDAnchor.up;

        // Force bed normal to world up (pins are flat, so plane should be horizontal)
        _planeN = Vector3.up;

        // Use anchor position directly (no child lookup needed)
        //_planeP0 = RTDAnchor.position;    // 16th oct - change
        float pinHeight = 0.007f;  // 7mm - matches pin CapsuleCollider height
        _planeP0 = RTDAnchor.position + Vector3.up * pinHeight;

        //Debug.Log($"RTDAnchor World Rotation: {RTDAnchor.rotation.eulerAngles}");

        Debug.Log($"[{name}] Bed plane rebuilt: normal={_planeN}, origin={_planeP0}, world rotation={RTDAnchor.rotation.eulerAngles}");
    }

    void FixedUpdate()
    {
        if (!RTDAnchor || !leapProvider) return;

        // Get tracked finger position from Leap
        Leap.Frame frame = leapProvider.CurrentFixedFrame;
        Leap.Hand hand = frame.GetHand(handedness);
        if (hand == null) return;

        Leap.Finger finger = hand.fingers[(int)Finger.FingerType.INDEX];
        Vector3 raw = finger.TipPosition;  // Already in Unity world space

        Vector3 target = raw;

        // Cast along bed normal
        Vector3 ro = raw + _planeN * rayOffset;
        Vector3 rd = -_planeN;
        int mask = (_selfMask == 0) ? _pinsMask : (_pinsMask & ~_selfMask);

        bool haveHit;
        RaycastHit hit;

        if (useSphereCast)
            haveHit = Physics.SphereCast(ro, tipSphereRadius, rd, out hit, maxRayDistance, mask, QueryTriggerInteraction.Collide);
        else
            haveHit = Physics.Raycast(ro, rd, out hit, maxRayDistance, mask, QueryTriggerInteraction.Collide);

        Vector3 surfPoint = haveHit ? hit.point : raw - Vector3.Dot(raw - _planeP0, _planeN) * _planeN;
        Vector3 surfNormal = haveHit ? hit.normal : _planeN;

        // FILTER: reject side hits (check normal alignment)
        if (haveHit)
        {
            float cosThresh = Mathf.Cos(maxNormalAngle * Mathf.Deg2Rad);
            float ndot = Vector3.Dot(surfNormal.normalized, _planeN);
            if (ndot < cosThresh)
            {
                haveHit = false;
                //Debug.Log($"[{name}] Rejected side hit: ndot={ndot:F3}");
            }
        }

        // FILTER: reject hits too far laterally from finger tip
        if (haveHit)
        {
            Vector3 toHit = surfPoint - raw;
            float lateral = Vector3.Magnitude(toHit - Vector3.Dot(toHit, _planeN) * _planeN);
            if (lateral > maxLateralFromTip)
            {
                haveHit = false;
                //Debug.Log($"[{name}] Rejected lateral hit: {lateral * 1000f:F2}mm");
            }
        }

        // Apply snapping logic
        if (haveHit)
        {
            float gap = Vector3.Dot(raw - surfPoint, surfNormal);
            if (gap <= hoverMargin)
                target = surfPoint + surfNormal * surfacePad;
        }
        else
        {
            // No pin hit - clamp to bed plane with padding
            float signedH = Vector3.Dot(raw - _planeP0, _planeN);
            if (signedH < surfacePad)
                target = raw - signedH * _planeN + _planeN * surfacePad;
        }

        Vector3 newPos = Vector3.Lerp(raw, target, lerp);
        _rb.MovePosition(newPos);
    }

    // Called by PositionReportNew when touch starts
    public void OnTouchStart()
    {
        isTouchingPin = true;
        frozenPosition = _rb.position;
        //Debug.Log($"[{name}] OnTouchStart called - isTouchingPin = TRUE");
    }

    // Called by PositionReportNew when touch ends
    public void OnTouchEnd()
    {
        isTouchingPin = false;
        //Debug.Log($"[{name}] OnTouchEnd called - isTouchingPin = FALSE");
    }

    #if UNITY_EDITOR
    // Cache gizmo data
    private float _lastGizmoCalcTime = 0f;
    private const float GIZMO_CALC_INTERVAL = 0.4f;

    // Cached raycast results
    private bool _cachedHitSomething = false;
    private RaycastHit _cachedHit;
    private Vector3 _cachedRo;
    private Vector3 _cachedRd;
    private Vector3 _cachedRaw;

    void OnDrawGizmosSelected()
    {
        if (!RTDAnchor) return;
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(transform.position, transform.position + _planeN * 0.06f);
        Gizmos.DrawWireSphere(_planeP0, 0.005f);
    }

    void OnDrawGizmos()
    {
        if (!RTDAnchor) return;

        // Update cached data occasionally
        if (Time.time - _lastGizmoCalcTime >= GIZMO_CALC_INTERVAL)
        {
            _lastGizmoCalcTime = Time.time;

            _cachedRaw = Application.isPlaying && _rb != null ? _rb.position : transform.position;
            _cachedRo = _cachedRaw + _planeN * rayOffset;
            _cachedRd = -_planeN;
            int mask = (_selfMask == 0) ? _pinsMask : (_pinsMask & ~_selfMask);

            // Perform raycast (only occasionally)
            if (useSphereCast)
                _cachedHitSomething = Physics.SphereCast(_cachedRo, tipSphereRadius, _cachedRd, out _cachedHit, maxRayDistance, mask, QueryTriggerInteraction.Collide);
            else
                _cachedHitSomething = Physics.Raycast(_cachedRo, _cachedRd, out _cachedHit, maxRayDistance, mask, QueryTriggerInteraction.Collide);
        }

        // Draw using cached data (every frame - smooth!)

        // Draw finger collision sphere
        Gizmos.color = isTouchingPin ? Color.red : Color.green;
        Gizmos.DrawWireSphere(_rb != null ? _rb.position : _cachedRaw, tipSphereRadius);

        // Draw ray origin
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(_cachedRo, 0.002f);

        if (_cachedHitSomething)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawLine(_cachedRo, _cachedHit.point);

            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(_cachedHit.point, 0.003f);

            float trackedGap = Vector3.Dot(_cachedRaw - _cachedHit.point, _cachedHit.normal);
            float collisionGap = _rb != null ? Vector3.Dot(_rb.position - _cachedHit.point, _cachedHit.normal) : trackedGap;

            string label = $"GameObject: {gameObject.name}\n" +
                        $"Ray Distance: {_cachedHit.distance * 100f:F2}cm\n" +
                        $"Tracked Gap: {trackedGap * 1000f:F2}mm\n" +
                        $"Collision Gap: {collisionGap * 1000f:F2}mm\n" +
                        $"Touching: {isTouchingPin}";

            var style = new GUIStyle(EditorStyles.boldLabel)
            {
                normal = { textColor = Color.black }
            };

            Handles.Label(_cachedHit.point + Vector3.up * 0.01f, label, style);
        }
        else
        {
            Gizmos.color = Color.red;
            Gizmos.DrawLine(_cachedRo, _cachedRo + _cachedRd * maxRayDistance);

            UnityEditor.Handles.Label(_cachedRo + _cachedRd * (maxRayDistance * 0.5f), $"{gameObject.name}: No hit");
        }

        // Draw bed plane reference
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(transform.position, transform.position + _planeN * 0.06f);
        Gizmos.DrawWireSphere(_planeP0, 0.005f);

        for (int i = -5; i <= 5; i++)
        {
            Vector3 start = _planeP0 + Vector3.right * i * 0.05f - Vector3.forward * 0.25f;
            Vector3 end = _planeP0 + Vector3.right * i * 0.05f + Vector3.forward * 0.25f;
            Gizmos.DrawLine(start, end);
        }
        for (int i = -5; i <= 5; i++)
        {
            Vector3 start = _planeP0 + Vector3.forward * i * 0.05f - Vector3.right * 0.25f;
            Vector3 end = _planeP0 + Vector3.forward * i * 0.05f + Vector3.right * 0.25f;
            Gizmos.DrawLine(start, end);
        }
    }
    #endif
}
