using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using Leap;
using Leap.Unity;


public class PositionReport : MonoBehaviour
{
    // interface classes
    [SerializeField] private MonoBehaviour textToSpeechService;
    private InterfaceTextToSpeech _textToSpeech;
    [SerializeField] private MonoBehaviour buttonGUIService;
    private InterfaceButtonGUI _buttonGUI;
    [SerializeField] private MonoBehaviour rtdUpdaterService;
    private InterfaceRTDUpdater _rtdUpdater;
    [SerializeField] private FingerSnapper fingerSnapper;
    private TouchProcessor touchProcessor;
    [SerializeField] private SpeechSettings speechSettings;
    private CapsuleHandEdit _capsuleHand;

    // Touch validation and gesture classification
    private TouchHandValidator _handValidator;
    private TouchGestureClassifier _gestureClassifier;
    private TouchCollisionTracker _collisionTracker;
    private TouchSpatialProcessor _spatialProcessor;
    private TouchResponseOrchestrator _responseOrchestrator;

    // visuals & collisions
    [SerializeField] private GraphVisualizer visualizer;

    //  Calibration support - event fired when touch is processed
    public event System.Action<Vector2Int, HashSet<Vector2Int>, List<NodeComponent>> OnTouchProcessed;
    public bool bypassValidationForCalibration = false; // Set by calibration system

    private Vector3 _lastTipPos;
    private float _lastTipTime;

    // Chirality validation configuration
    [SerializeField] private bool _isLeftComponent = false;
    [SerializeField, Range(0f, 1f)] private float _acceptConfidence = 0.50f;
    [SerializeField, Range(0f, 1f)] private float _releaseConfidence = 0.35f;
    [SerializeField, Range(1, 60)]  private int   _framesToSwitch   = 12;

    void Awake()
    {
        _textToSpeech = textToSpeechService as InterfaceTextToSpeech ?? throw new InvalidOperationException("textToSpeechService not assigned!");
        _buttonGUI = buttonGUIService as InterfaceButtonGUI ?? throw new InvalidOperationException("buttonGUIService not assigned!");
        _rtdUpdater = rtdUpdaterService as InterfaceRTDUpdater ?? throw new InvalidOperationException("rtdUpdaterService not assigned!");
        touchProcessor = GetComponent<TouchProcessor>();
        _isLeftComponent = name.IndexOf("Left", System.StringComparison.OrdinalIgnoreCase) >= 0;

        // Initialize hand validator
        _handValidator = new TouchHandValidator(_isLeftComponent, _acceptConfidence, _releaseConfidence, _framesToSwitch);

        // Initialize gesture classifier
        _gestureClassifier = new TouchGestureClassifier(
            _buttonGUI.GetTapMinDuration(),
            _buttonGUI.GetTapMaxDuration(),
            _buttonGUI.GetDoubleTapTimeWindow(),
            _buttonGUI.GetTapCoolDown()
        );

        // Initialize collision tracker
        _collisionTracker = new TouchCollisionTracker();

        // Initialize spatial processor
        _spatialProcessor = new TouchSpatialProcessor(touchProcessor, visualizer);
        _spatialProcessor.OnTouchProcessed += (point, coords, nodes) => OnTouchProcessed?.Invoke(point, coords, nodes);

        // Initialize response orchestrator
        _responseOrchestrator = new TouchResponseOrchestrator(_rtdUpdater, _textToSpeech, _buttonGUI, speechSettings);
    }

    void Start()
    {
        if (visualizer == null)
            visualizer = FindObjectOfType<GraphVisualizer>();
        _capsuleHand = GetComponentInParent<CapsuleHandEdit>();

        // The CapsuleHand's parent or grandparent should be the tracking root
        var leapServiceProvider = FindObjectOfType<Leap.LeapServiceProvider>();

        if (leapServiceProvider == null)
            UnityEngine.Debug.LogError($"{name} could not find LeapServiceProvider!");

        UnityEngine.Debug.Log($"{gameObject.name} PositionReportNew is active!");
    }

    void Update()
    {
        // Sync classifier config in case tap timing settings changed at runtime
        _gestureClassifier.UpdateConfig(
            _buttonGUI.GetTapMinDuration(),
            _buttonGUI.GetTapMaxDuration(),
            _buttonGUI.GetDoubleTapTimeWindow(),
            _buttonGUI.GetTapCoolDown()
        );

        if (_capsuleHand != null)
        {
            Hand leapHand = _capsuleHand.GetLeapHand();
            if (leapHand == null) return;

            // HandConfidenceController already handles very low confidence
            // But add chirality validation for remaining cases
            bool isExpectedChirality = _handValidator.ValidateChirality(leapHand);
            if (!isExpectedChirality)
            {
                
                if (Time.frameCount % 120 == 0) // Log every 2 seconds
                {
                    UnityEngine.Debug.LogWarning($"[{name}] Wrong chirality: Expected {(name.Contains("Left") ? "LEFT" : "RIGHT")}, " +
                                $"got {(leapHand.IsLeft ? "LEFT" : "RIGHT")} (confidence: {leapHand.Confidence:F2})");
                }
                
                return; // Don't process wrong-handed data
            }
        }

        // --- fingertip velocity tracking (only while in contact) ---
        if (_collisionTracker.IsInContact && _capsuleHand != null)
        {
            Hand leapHand = _capsuleHand.GetLeapHand();
            if (leapHand != null)
            {
                Finger index = leapHand.fingers[(int)Finger.FingerType.INDEX];
                Vector3 unityTip = index.TipPosition;
                float t = Time.time;

                // Update gesture classifier with velocity data
                _gestureClassifier.UpdateVelocity(unityTip, t, _lastTipTime, _lastTipPos);

                _lastTipPos = unityTip;
                _lastTipTime = t;
            }
        }
    }

    public void SetTrainingMode(bool on)
    {
        _gestureClassifier.SetTrainingMode(on);
    }

    void OnTriggerEnter(Collider other)
    {
        if (!_buttonGUI.GetTouchSenseState()) return;

        // Delegate to collision tracker
        bool isValidPin = _collisionTracker.HandleEnter(other);
        if (!isValidPin)
            return;

        // Reset gesture tracking on first contact
        _gestureClassifier.ResetVelocityTracking();
        fingerSnapper?.OnTouchStart();
        _lastTipPos = Vector3.zero;
        _lastTipTime = 0f;
    }

    void OnTriggerExit(Collider other)
    {
        if (!_buttonGUI.GetTouchSenseState()) return;

        // Delegate to collision tracker
        bool touchComplete = _collisionTracker.HandleExit(other, out float enterTime, out float holdDuration);
        if (!touchComplete)
            return; // Still touching other pins

        // Stop gesture tracking
        _gestureClassifier.ResetVelocityTracking();
        fingerSnapper?.OnTouchEnd();

        var coords = _collisionTracker.GetCoordinates();

        try
        {
            // Validate minimum hold time
            if (holdDuration < _buttonGUI.GetTapMinDuration())
            {
                if (holdDuration < 0.1f)
                    return;
                _gestureClassifier.ResetTapCount();
                UnityEngine.Debug.LogWarning($"Ignored tap: held {holdDuration:F3}s < min {_buttonGUI.GetTapMinDuration()}s");
                return;
            }

            // Veto swipes
            if (_gestureClassifier.GetMotionType() == TouchGestureClassifier.MotionType.Swipe)
            {
                TouchDebugger.LogReport(new TouchDebugger.TapDebugInfo
                {
                    FingerName = this.name,
                    Coords = coords,
                    Duration = holdDuration,
                    TapCount = _gestureClassifier.GetTapCount(),
                    TimeSinceLast = Time.time,
                    DoubleTapMode = _buttonGUI.GetDoubleTapState(),

                    Result = TouchDebugger.TapResult.Swipe,
                    LastMotion = _gestureClassifier.GetMotionType(),
                    MinDuration = _buttonGUI.GetTapMinDuration(),
                    MaxDuration = _buttonGUI.GetTapMaxDuration(),
                    Cooldown = _buttonGUI.GetTapCoolDown(),
                    DoubleTapWindow = _buttonGUI.GetDoubleTapTimeWindow()
                });
                _gestureClassifier.ClearMotion();
                _collisionTracker.ClearCoordinates();
                return;
            }

            // Debug: Log palm velocity
            if (_capsuleHand != null)
            {
                Hand leapHand = _capsuleHand.GetLeapHand();
                if (leapHand != null)
                {
                    float downSpeed = -leapHand.PalmVelocity.y / 1000f;
                    UnityEngine.Debug.Log($"[TapVelocity] {this.name}, Palm downSpeed={downSpeed:F3} m/s, PalmVelocity={leapHand.PalmVelocity}");
                }
            }

            if (coords.Count == 0)
                return;

            float touchStartTime = _collisionTracker.GetTouchStartTime();
            ProcessTouchIfNeeded(coords, touchStartTime > 0f ? touchStartTime : enterTime);
            _gestureClassifier.ClearMotion();
            _collisionTracker.ClearCoordinates();
        }
        finally
        {
            _collisionTracker.Reset();
        }
    }

    private void ProcessTouchIfNeeded(HashSet<Vector2Int> coords, float enterTime)
    {
        // choose left or right finger data
        var fingerName = this.name;

        float now = Time.time;
        float duration = now - enterTime;

        //  CALIBRATION MODE: Bypass all validation
        if (bypassValidationForCalibration)
        {
            if (coords.Count > 0)
            {
                ProcessTouchData(coords, fingerName, duration);
            }
            return;
        }

        if (!_buttonGUI.GetDoubleTapState())
        {
            _gestureClassifier.ResetTapCount();
            return;
        }

        // Validate tap timing
        if (!_gestureClassifier.ValidateTapTiming(duration, now, out string failureReason))
        {
            if (failureReason != null)
            {
                TouchDebugger.LogReport(new TouchDebugger.TapDebugInfo
                {
                    FingerName = fingerName,
                    Coords = coords,
                    Duration = duration,
                    TapCount = _gestureClassifier.GetTapCount(),
                    TimeSinceLast = now,
                    DoubleTapMode = _buttonGUI.GetDoubleTapState(),

                    Result = TouchDebugger.TapResult.None,
                    LastMotion = _gestureClassifier.GetMotionType(),
                    MinDuration = _buttonGUI.GetTapMinDuration(),
                    MaxDuration = _buttonGUI.GetTapMaxDuration(),
                    Cooldown = _buttonGUI.GetTapCoolDown(),
                    DoubleTapWindow = _buttonGUI.GetDoubleTapTimeWindow()
                });
                UnityEngine.Debug.LogWarning($"Ignored tap: {failureReason}");
            }
            return;
        }

        // Double-tap mode
        if (_buttonGUI.GetDoubleTapState())
        {
            // Note: We need to process touch data first to get interpretedTapPoint and currentNodeIDs
            // So we'll just process the tap and log it
            TouchDebugger.LogReport(new TouchDebugger.TapDebugInfo
            {
                FingerName = fingerName,
                Coords = coords,
                Duration = duration,
                TapCount = _gestureClassifier.GetTapCount(),
                TimeSinceLast = now,
                DoubleTapMode = _buttonGUI.GetDoubleTapState(),

                Result = TouchDebugger.TapResult.SingleTap,
                LastMotion = _gestureClassifier.GetMotionType(),
                MinDuration = _buttonGUI.GetTapMinDuration(),
                MaxDuration = _buttonGUI.GetTapMaxDuration(),
                Cooldown = _buttonGUI.GetTapCoolDown(),
                DoubleTapWindow = _buttonGUI.GetDoubleTapTimeWindow()
            });

            ProcessTouchData(coords, fingerName, duration);
        }
    }

    private void ProcessTouchData(HashSet<Vector2Int> coords, string fingerName, float duration)
    {
        // 1) guard rails
        if (!_buttonGUI.GetTouchSenseState()) return;
        if (coords == null || coords.Count == 0) return;

        string hand = fingerName.Contains("Left") ? "left" : "right";

        // Process touch using spatial processor
        bool success = _spatialProcessor.ProcessTouch(
            coords,
            fingerName,
            bypassValidationForCalibration,
            out List<NodeComponent> matchingNodes,
            out Vector2Int interpretedPoint,
            out Dictionary<string, object> nodesDict
        );

        if (!success)
            return; // No nodes found or calibration mode handled

        // Handle pin blink for single taps
        _responseOrchestrator.HandlePinBlink(_spatialProcessor.GetNodePositions());

        // Serialize touch data
        string serialized = _responseOrchestrator.SerializeTouchData(matchingNodes, nodesDict);

        if (_buttonGUI.GetDoubleTapState())
        {
            List<string> currentIDs = matchingNodes?.Select(n => n.id).ToList() ?? new List<string>();
            float currentTime = Time.time;

            // Check for double-tap using gesture classifier
            bool isDoubleTap = _gestureClassifier.CheckForDoubleTap(
                duration,
                currentTime,
                interpretedPoint,
                currentIDs,
                out bool isFirstTap
            );

            if (isDoubleTap)
            {
                var (firstDuration, secondDuration) = _gestureClassifier.GetTapDurations();
                TouchDebugger.LogReport(new TouchDebugger.TapDebugInfo
                {
                    FingerName = fingerName,
                    Coords = coords,
                    Duration = duration,
                    TapCount = _gestureClassifier.GetTapCount(),
                    TimeSinceLast = currentTime,
                    DoubleTapMode = _buttonGUI.GetDoubleTapState(),

                    Result = TouchDebugger.TapResult.DoubleTap,
                    LastMotion = _gestureClassifier.GetMotionType(),
                    MinDuration = _buttonGUI.GetTapMinDuration(),
                    MaxDuration = _buttonGUI.GetTapMaxDuration(),
                    Cooldown = _buttonGUI.GetTapCoolDown(),
                    DoubleTapWindow = _buttonGUI.GetDoubleTapTimeWindow(),
                    InterpretedTapPoint = interpretedPoint,
                    MatchingNodes = matchingNodes,
                    Probabilities = _spatialProcessor.GetProbabilities(),
                    MostLikelyPin = _spatialProcessor.GetMostLikelyPin(),
                    MostLikelyProbability = _spatialProcessor.GetMostLikelyProbability(),
                    ValidDuration = true,
                    WithinWindow = true,
                    SameLocation = true,
                    SameNodes = true,
                    FirstTapDuration = firstDuration,
                    SecondTapDuration = secondDuration
                });

                var highConfidencePositions = _spatialProcessor.GetHighConfidencePositions(0.2f);
                _responseOrchestrator.HandleDoubleTapResponse(
                    matchingNodes,
                    _spatialProcessor.GetProbabilities(),
                    _spatialProcessor.GetMostLikelyPin(),
                    highConfidencePositions,
                    hand,
                    serialized,
                    fingerName
                );
            }
            else if (isFirstTap)
            {
                UnityEngine.Debug.Log($"First tap detected, waiting for second tap...");
            }
        }
    }

    public void ResetLastTouchData()
    {
        _responseOrchestrator.ResetLastTouchData();
    }

    public string GetLastTouchData()
    {
        return _responseOrchestrator.GetLastTouchData();
    }

}
