using UnityEngine;

[ExecuteInEditMode]
public class RTDAligner : MonoBehaviour
{
    [Header("Calibration")]
    [Tooltip("The calibrated local-space offset for this anchor")]
    public Vector3 anchorLocalOffset = new Vector3(-0.0773f, -0.2163f, -0.0109f);

    [Header("Runtime Updates")]
    [Tooltip("Apply offset changes immediately during Play mode (useful for calibration)")]
    public bool applyOffsetRealtime = false;

    [Header("Rotation")]
    [Tooltip("Fixed rotation to apply after detaching")]
    public Vector3 fixedRotation = new Vector3(-180f, 0f, 0f);
    
    void Awake()
    {
        // In Editor mode, OnValidate handles updates
        // In Play mode, apply calibration immediately
        if (Application.isPlaying)
        {
            ApplyCalibration();
        }
    }
    
    void Start()
    {
        if (Application.isPlaying)
        {
            // Detach this object from its parent
            transform.SetParent(null, false);

            // Apply the calibrated position
            transform.position = anchorLocalOffset;

            // Fix rotation
            transform.rotation = Quaternion.Euler(fixedRotation);

            Debug.Log($"{name} detached and positioned at {transform.position}");
        }
    }

    void Update()
    {
        // Real-time offset updates during Play mode (for calibration)
        if (Application.isPlaying && applyOffsetRealtime)
        {
            transform.position = anchorLocalOffset;
        }
    }
    
    void ApplyCalibration()
    {
        transform.localPosition = anchorLocalOffset;
    }
    
    // Editor helper: capture position changes in the Inspector
    void OnValidate()
    {
        if (!Application.isPlaying)
        {
            anchorLocalOffset = transform.localPosition;
        }
    }
}
