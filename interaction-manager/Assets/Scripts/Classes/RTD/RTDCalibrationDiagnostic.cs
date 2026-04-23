using UnityEngine;
using Leap;
using Leap.Unity;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Diagnostic tool to visualize and verify RTD-to-Leap alignment.
/// Helps identify coordinate space issues and calibration drift.
/// </summary>
public class RTDCalibrationDiagnostic : MonoBehaviour
{
    [Header("References")]
    public Transform RTDAnchor;
    public LeapServiceProvider leapProvider;
    [Tooltip("PositionReport component on left hand (e.g., LeftIndexTip)")]
    public PositionReport leftPositionReport;
    [Tooltip("PositionReport component on right hand (e.g., RightIndexTip)")]
    public PositionReport rightPositionReport;
    [SerializeField] private MonoBehaviour rtdUpdaterService;
    private InterfaceRTDUpdater _rtdUpdater;

    [Header("Calibration Mode")]
    [Tooltip("Enable to raise calibration pins and guide through process")]
    public bool calibrationMode = false;
    [Tooltip("Use Gaussian probability interpretation (recommended) vs raw finger position")]
    public bool useGaussianMethod = true;
    private bool _wasInCalibrationMode = false;

    [Header("Visualization")]
    [Tooltip("Show gizmos for finger positions and known test points")]
    public bool showGizmos = true;
    [Tooltip("Log alignment error to console")]
    public bool logErrors = true;
    [Range(0.001f, 0.05f)]
    public float errorThreshold = 0.005f; // 5mm error threshold

    [Header("Calibration Pins (NW, NE, SW, SE, Center)")]
    [Tooltip("The 5 pins used for calibration - corners + center")]
    public List<Vector2Int> calibrationPins = new List<Vector2Int>
    {
        new Vector2Int(0, 0),      // NW (top-left)
        new Vector2Int(59, 0),     // NE (top-right)
        new Vector2Int(0, 39),     // SW (bottom-left)
        new Vector2Int(59, 39),    // SE (bottom-right)
        new Vector2Int(30, 20)     // Center
    };

    private int _currentCalibrationPinIndex = 0;

    [Header("Calibration Adjustment")]
    [Tooltip("Suggested offset correction based on observed errors")]
    public Vector3 suggestedOffsetCorrection = Vector3.zero;

    [Header("Runtime Stats")]
    public float currentAlignmentError = 0f;
    public Vector3 lastMeasuredOffset = Vector3.zero;
    public int samplesCollected = 0;

    private const float PIN_SPACING = 0.0025f; // 2.5mm
    private const float HAND_PROXIMITY_THRESHOLD = 0.02f; // 20mm
    private const int CALIBRATION_TOUCH_RADIUS = 5; // pins — ignore touches >5 pins from target
    private const float GIZMO_SPHERE_SIZE = 0.002f;
    private const float GIZMO_PIN_SIZE = 0.003f;
    private List<AlignmentSample> _samples = new List<AlignmentSample>();
    private const int MAX_SAMPLES = 25;  // 5 samples × 5 pins
    private const int SAMPLES_PER_PIN = 5;

    private struct AlignmentSample
    {
        public Vector3 leapFingerWorld;
        public Vector3 expectedPinWorld;
        public Vector3 error;
        public float timestamp;
        public Vector2Int pinCoord;
    }

    void Start()
    {
        _rtdUpdater = rtdUpdaterService as InterfaceRTDUpdater;
        if (_rtdUpdater == null)
        {
            Debug.LogError("RTDUpdater service not assigned!");
        }

        // Auto-find PositionReport components if not assigned
        if (leftPositionReport == null)
        {
            var allReports = FindObjectsOfType<PositionReport>();
            leftPositionReport = allReports.FirstOrDefault(pr => pr.name.Contains("Left"));
        }
        if (rightPositionReport == null)
        {
            var allReports = FindObjectsOfType<PositionReport>();
            rightPositionReport = allReports.FirstOrDefault(pr => pr.name.Contains("Right"));
        }
    }

    void Update()
    {
        if (!RTDAnchor) return;

        // Handle calibration mode transitions
        if (calibrationMode != _wasInCalibrationMode)
        {
            if (calibrationMode)
                EnterCalibrationMode();
            else
                ExitCalibrationMode();
            _wasInCalibrationMode = calibrationMode;
        }

        // Update diagnostics
        if (_samples.Count > 0)
        {
            UpdateAlignmentStatistics();
        }

        // Check if current pin has enough samples
        if (calibrationMode && _currentCalibrationPinIndex < calibrationPins.Count)
        {
            Vector2Int targetPin = calibrationPins[_currentCalibrationPinIndex];
            int samplesForThisPin = _samples.Count(s => s.pinCoord == targetPin);

            if (samplesForThisPin >= SAMPLES_PER_PIN)
            {
                // Move to next pin
                _currentCalibrationPinIndex++;

                if (_currentCalibrationPinIndex < calibrationPins.Count)
                {
                    Debug.Log($"Pin {_currentCalibrationPinIndex}/{calibrationPins.Count} complete. " +
                              $"Move to next pin: {calibrationPins[_currentCalibrationPinIndex]}");
                    PulseCurrentTargetPin();
                }
                else
                {
                    Debug.Log(" ALL PINS COMPLETE! Click 'Apply Suggested Correction'");
                    _rtdUpdater?.StopPulsePins();
                }
            }
        }
    }

    private void EnterCalibrationMode()
    {
        if (_rtdUpdater == null) return;

        //  Enable real-time offset updates on RTDAligner
        var aligner = RTDAnchor.GetComponent<RTDAligner>();
        if (aligner != null)
        {
            aligner.applyOffsetRealtime = true;
            Debug.Log(" Enabled real-time offset updates on RTDAligner");
        }

        string methodName = useGaussianMethod ? "Gaussian probability" : "raw finger position";
        Debug.Log($"CALIBRATION MODE STARTED ({methodName})\n" +
                  "Touch each pulsing pin in sequence:\n" +
                  "1. NW corner (0,0)\n" +
                  "2. NE corner (59,0)\n" +
                  "3. SW corner (0,39)\n" +
                  "4. SE corner (59,39)\n" +
                  "5. Center (30,20)");

        //  Enable bypass in PositionReport to skip tap validation
        if (leftPositionReport != null)
        {
            leftPositionReport.bypassValidationForCalibration = true;
            leftPositionReport.OnTouchProcessed += HandleTouchProcessed;
        }
        if (rightPositionReport != null)
        {
            rightPositionReport.bypassValidationForCalibration = true;
            rightPositionReport.OnTouchProcessed += HandleTouchProcessed;
        }

        // Clear screen and raise only calibration pins
        _rtdUpdater.ClearScreen();

        foreach (var pin in calibrationPins)
        {
            _rtdUpdater.SetPixel(pin.y, pin.x, true);
        }

        // Start with first pin
        _currentCalibrationPinIndex = 0;
        _samples.Clear();
        samplesCollected = 0;

        // Pulse the first target pin
        PulseCurrentTargetPin();
    }

    private void ExitCalibrationMode()
    {
        Debug.Log(" CALIBRATION MODE ENDED");

        //  Disable bypass - restore normal validation
        if (leftPositionReport != null)
        {
            leftPositionReport.bypassValidationForCalibration = false;
            leftPositionReport.OnTouchProcessed -= HandleTouchProcessed;
        }
        if (rightPositionReport != null)
        {
            rightPositionReport.bypassValidationForCalibration = false;
            rightPositionReport.OnTouchProcessed -= HandleTouchProcessed;
        }

        // Stop pulsing
        _rtdUpdater?.StopPulsePins();

        // Refresh to original chart
        _rtdUpdater?.RefreshScreen();
    }

    /// <summary>
    /// Event handler for touch processing - uses Gaussian-interpreted position
    /// </summary>
    private void HandleTouchProcessed(Vector2Int interpretedPoint, HashSet<Vector2Int> rawCoords, List<NodeComponent> matchingNodes)
    {
        if (!calibrationMode || _currentCalibrationPinIndex >= calibrationPins.Count)
            return;

        Vector2Int targetPin = calibrationPins[_currentCalibrationPinIndex];

        // Only accept touches near the target pin — ignore accidental touches elsewhere
        int dx = Mathf.Abs(interpretedPoint.x - targetPin.x);
        int dy = Mathf.Abs(interpretedPoint.y - targetPin.y);
        if (dx > CALIBRATION_TOUCH_RADIUS || dy > CALIBRATION_TOUCH_RADIUS)
        {
            Debug.Log($"Touch at {interpretedPoint} ignored — too far from target {targetPin} (dx={dx}, dy={dy})");
            return;
        }

        // Calculate error: interpreted position vs actual target
        Vector3 interpretedWorld = PinCoordToWorldPosition(interpretedPoint);
        Vector3 targetWorld = PinCoordToWorldPosition(targetPin);
        Vector3 error = interpretedWorld - targetWorld;

        AlignmentSample sample = new AlignmentSample
        {
            leapFingerWorld = interpretedWorld, // Using interpreted position
            expectedPinWorld = targetWorld,
            error = error,
            timestamp = Time.time,
            pinCoord = targetPin
        };

        _samples.Add(sample);
        if (_samples.Count > MAX_SAMPLES)
            _samples.RemoveAt(0);

        samplesCollected = _samples.Count;
        currentAlignmentError = error.magnitude;

        if (logErrors)
        {
            Debug.Log($"Sample {samplesCollected}: Interpreted={interpretedPoint}, Target={targetPin}, " +
                      $"Error={error.magnitude * 1000f:F2}mm ({error.magnitude * 1000f / 2.5f:F1} pins), " +
                      $"RawPins={rawCoords.Count}");
        }
    }

    private void PulseCurrentTargetPin()
    {
        if (_rtdUpdater == null || _currentCalibrationPinIndex >= calibrationPins.Count)
            return;

        var targetPin = calibrationPins[_currentCalibrationPinIndex];

        // Stop any previous pulse
        _rtdUpdater.StopPulsePins();

        // Pulse the current target
        _rtdUpdater.PulsePins(
            new List<Vector2Int> { targetPin },
            interval: 0.5f,
            duration: -1f, // Indefinite
            hand: "calibration"
        );

        Debug.Log($"Touch pin {_currentCalibrationPinIndex + 1}/{calibrationPins.Count}: {targetPin}");
    }

    private void UpdateAlignmentStatistics()
    {
        // Calculate average error over recent samples
        Vector3 avgError = Vector3.zero;
        foreach (var sample in _samples)
            avgError += sample.error;
        avgError /= _samples.Count;

        lastMeasuredOffset = avgError;
        currentAlignmentError = avgError.magnitude;

        // Suggest correction
        suggestedOffsetCorrection = -avgError; // Negate to get correction
    }

    /// <summary>
    /// Convert pin coordinates (0-59, 0-39) to Unity world position
    /// </summary>
    private Vector3 PinCoordToWorldPosition(Vector2Int pinCoord)
    {
        if (!RTDAnchor) return Vector3.zero;

        // RTDBuild places pins with:
        // - gridX = 60, gridY = 40
        // - spacing = 0.0025m
        // - rows go -Z from RTD anchor
        // - cols go +X from RTD anchor

        // Local position relative to RTD anchor
        float localX = pinCoord.x * PIN_SPACING;
        float localZ = -pinCoord.y * PIN_SPACING;
        Vector3 pinLocal = new Vector3(localX, 0f, localZ);

        // Transform to world space
        return RTDAnchor.TransformPoint(pinLocal);
    }

    public void ApplySuggestedCorrection()
    {
        var aligner = RTDAnchor.GetComponent<RTDAligner>();
        if (!aligner)
        {
            Debug.LogError("No RTDAligner found on RTDAnchor!");
            return;
        }

        Debug.Log($"Applying correction: {suggestedOffsetCorrection}\n" +
                  $"   Old offset: {aligner.anchorLocalOffset}\n" +
                  $"   New offset: {aligner.anchorLocalOffset + suggestedOffsetCorrection}\n" +
                  $"   (RTDAnchor will move immediately if 'Apply Offset Realtime' is enabled)");

        aligner.anchorLocalOffset += suggestedOffsetCorrection;

        // RTDAnchor position updates automatically if applyOffsetRealtime is enabled

        // Clear samples after applying correction
        _samples.Clear();
        samplesCollected = 0;
    }

    void OnDrawGizmos()
    {
        if (!showGizmos || !RTDAnchor) return;

        // Draw RTD anchor
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(RTDAnchor.position, 0.01f);

        // Draw test pins
        Gizmos.color = Color.yellow;
        foreach (var pinCoord in calibrationPins)
        {
            Vector3 pinWorld = PinCoordToWorldPosition(pinCoord);
            Gizmos.DrawWireSphere(pinWorld, GIZMO_PIN_SIZE);

            // Label
            #if UNITY_EDITOR
            UnityEditor.Handles.Label(pinWorld + Vector3.up * 0.01f,
                                     $"Pin ({pinCoord.x},{pinCoord.y})",
                                     new GUIStyle(UnityEditor.EditorStyles.whiteLabel) { fontSize = 10 });
            #endif
        }

        // Draw recent alignment errors
        Gizmos.color = Color.red;
        foreach (var sample in _samples)
        {
            Gizmos.DrawLine(sample.expectedPinWorld, sample.leapFingerWorld);
            Gizmos.DrawWireSphere(sample.leapFingerWorld, GIZMO_SPHERE_SIZE);
        }

        // Draw suggested correction vector
        if (suggestedOffsetCorrection.magnitude > 0.001f)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawLine(RTDAnchor.position, RTDAnchor.position + suggestedOffsetCorrection * 10f);
        }
    }

    #if UNITY_EDITOR
    [UnityEditor.CustomEditor(typeof(RTDCalibrationDiagnostic))]
    class CalibrationDiagnosticEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            RTDCalibrationDiagnostic diagnostic = (RTDCalibrationDiagnostic)target;

            UnityEditor.EditorGUILayout.Space();
            UnityEditor.EditorGUILayout.LabelField("Calibration Instructions", UnityEditor.EditorStyles.boldLabel);
            UnityEditor.EditorGUILayout.HelpBox(
                "1. Enable 'Calibration Mode' checkbox above\n" +
                "2. Touch each pulsing pin (5 times per pin)\n" +
                "3. System auto-advances through all 5 pins\n" +
                "4. Click 'Apply Suggested Correction' button below\n" +
                "5. If RTDAligner has 'Apply Offset Realtime' enabled, you'll see the correction immediately!",
                UnityEditor.MessageType.Info);

            UnityEditor.EditorGUILayout.Space();
            if (GUILayout.Button("Apply Suggested Correction"))
            {
                if (diagnostic.samplesCollected < 10)
                {
                    UnityEditor.EditorUtility.DisplayDialog("Insufficient Data",
                        $"Only {diagnostic.samplesCollected} samples collected. Please collect at least 10 samples from multiple test pins.",
                        "OK");
                }
                else
                {
                    diagnostic.ApplySuggestedCorrection();
                }
            }

            if (GUILayout.Button("Clear Samples"))
            {
                diagnostic._samples.Clear();
                diagnostic.samplesCollected = 0;
            }

            UnityEditor.EditorGUILayout.Space();
            UnityEditor.EditorGUILayout.LabelField("Diagnostics", UnityEditor.EditorStyles.boldLabel);
            UnityEditor.EditorGUILayout.LabelField($"Current Error: {diagnostic.currentAlignmentError * 1000f:F2}mm");
            UnityEditor.EditorGUILayout.LabelField($"Samples: {diagnostic.samplesCollected}/{MAX_SAMPLES}");
            UnityEditor.EditorGUILayout.LabelField($"Avg Offset: ({diagnostic.lastMeasuredOffset.x * 1000f:F2}, {diagnostic.lastMeasuredOffset.y * 1000f:F2}, {diagnostic.lastMeasuredOffset.z * 1000f:F2})mm");
        }
    }
    #endif
}
