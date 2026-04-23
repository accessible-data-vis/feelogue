using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Classifies touch gestures (tap vs swipe) and detects double-taps.
/// Handles velocity-based motion analysis and tap timing validation.
/// </summary>
public class TouchGestureClassifier
{
    // Motion classification
    public enum MotionType { Unknown, Tap, Swipe }

    private MotionType _lastMotion = MotionType.Unknown;
    private bool _tapDownDetected = false;
    private Queue<Vector3> _velocityBuffer = new Queue<Vector3>();
    private int _velocityBufferSize = 5;

    // Displacement tracking for fallback classification
    private Vector3? _gestureStartPosition = null;
    private Vector3? _gestureEndPosition = null;

    // Double-tap detection
    private float _lastProcessedTime = -1f;
    private List<string> _lastNodeIDs = new List<string>();
    private Vector2Int? _lastTapPoint = null;
    private int _tapCount = 0;
    private float _firstTapDuration = -1f;
    private float _secondTapDuration = -1f;

    // Training mode
    private bool _trainingMode = false;
    private int _tapsRecorded = 0;
    private List<float> _recordedDurations = new List<float>();
    private const int TRAINING_TAP_TARGET = 10;

    // Configuration
    private float _tapMinDuration;
    private float _tapMaxDuration;
    private float _doubleTapTimeWindow;
    private float _tapCoolDown;

    public TouchGestureClassifier(
        float tapMinDuration = 0.3f,
        float tapMaxDuration = 0.6f,
        float doubleTapTimeWindow = 1.5f,
        float tapCoolDown = 0.2f)
    {
        _tapMinDuration = tapMinDuration;
        _tapMaxDuration = tapMaxDuration;
        _doubleTapTimeWindow = doubleTapTimeWindow;
        _tapCoolDown = tapCoolDown;
    }

    /// <summary>
    /// Updates velocity tracking and detects tap-down/tap-up gestures.
    /// Call this every frame when finger is in contact.
    /// </summary>
    public void UpdateVelocity(Vector3 tipPosition, float currentTime, float lastTipTime, Vector3 lastTipPosition)
    {
        if (lastTipTime <= 0) return;

        float dtTip = currentTime - lastTipTime;
        if (dtTip <= 0f) return;

        // Track displacement
        if (!_gestureStartPosition.HasValue)
            _gestureStartPosition = tipPosition;
        _gestureEndPosition = tipPosition;

        // Calculate raw velocity
        Vector3 rawVel = (tipPosition - lastTipPosition) / dtTip;

        // Smooth velocity with rolling average
        Vector3 smoothedVel = GetSmoothedVelocity(rawVel);

        float vY = smoothedVel.y;
        float vXZ = new Vector2(smoothedVel.x, smoothedVel.z).magnitude;

        // Tap detection (downward spike followed by upward release)
        if (!_tapDownDetected && vY < -0.02f)
        {
            _tapDownDetected = true;
            _lastMotion = MotionType.Unknown;
        }
        else if (_tapDownDetected && vY > 0.02f)
        {
            _tapDownDetected = false;
            if (_lastMotion != MotionType.Swipe)  // don't overwrite confirmed swipe
                _lastMotion = MotionType.Tap;
        }
        // Swipe detection (lateral motion dominates)
        else if (vXZ > 0.1f && vXZ > Mathf.Abs(vY) * 3f)
        {
            _lastMotion = MotionType.Swipe;
        }
    }

    /// <summary>
    /// Resets velocity tracking state (call when contact ends).
    /// </summary>
    public void ResetVelocityTracking()
    {
        ClassifyByDisplacement();  // always run — can promote Tap→Swipe or Unknown→Swipe
        if (_lastMotion == MotionType.Unknown)
            _lastMotion = MotionType.Tap;  // final fallback

        _tapDownDetected = false;
        _velocityBuffer.Clear();
        _gestureStartPosition = null;
        _gestureEndPosition = null;
    }

    /// <summary>
    /// Classifies the current motion type.
    /// </summary>
    public MotionType GetMotionType() => _lastMotion;

    /// <summary>
    /// Clears the motion type (call after processing).
    /// </summary>
    public void ClearMotion()
    {
        _lastMotion = MotionType.Unknown;
    }

    /// <summary>
    /// Validates if a tap meets timing requirements (min/max duration, cooldown).
    /// </summary>
    public bool ValidateTapTiming(float duration, float currentTime, out string failureReason)
    {
        failureReason = null;

        // Check minimum duration
        if (duration < _tapMinDuration)
        {
            failureReason = $"held {duration:F3}s < min {_tapMinDuration}s";
            _tapCount = 0;
            return false;
        }

        // Check maximum duration
        if (duration > _tapMaxDuration)
        {
            _tapCount = 0;
            if (duration < 2.0f) // Only warn if < 2s (extreme holds are probably unintentional)
            {
                failureReason = $"held {duration:F3}s > max {_tapMaxDuration}s";
            }
            return false;
        }

        // Check cooldown
        if (currentTime - _lastProcessedTime < _tapCoolDown)
        {
            _tapCount = 0;
            failureReason = $"cooldown not elapsed ({currentTime - _lastProcessedTime:F3}s < {_tapCoolDown}s)";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if a double-tap occurred and updates state accordingly.
    /// Returns true if this completes a double-tap sequence.
    /// </summary>
    public bool CheckForDoubleTap(
        float duration,
        float currentTime,
        Vector2Int interpretedTapPoint,
        List<string> currentNodeIDs,
        out bool isFirstTap)
    {
        isFirstTap = false;

        // Training mode: just record taps
        if (_trainingMode)
        {
            _recordedDurations.Add(duration);
            _tapsRecorded++;
            UnityEngine.Debug.Log($"[TRAIN] Tap #{_tapsRecorded}: {duration:F3}s");

            if (_tapsRecorded >= TRAINING_TAP_TARGET)
            {
                UnityEngine.Debug.Log($"[TRAIN COMPLETE] min={_recordedDurations.Min():F3}s  max={_recordedDurations.Max():F3}s");
                _trainingMode = false;
            }
            return false; // Don't process as real tap during training
        }

        bool validDuration = duration >= _tapMinDuration && duration <= _tapMaxDuration;
        bool withinWindow = currentTime - _lastProcessedTime <= _doubleTapTimeWindow;
        bool sameLocation = _lastTapPoint.HasValue && Vector2Int.Distance(_lastTapPoint.Value, interpretedTapPoint) <= 1;
        bool sameNodes = AreNodeListsSimilar(currentNodeIDs, _lastNodeIDs);

        bool isDoubleTap = validDuration && withinWindow && sameLocation && sameNodes;

        // Update state (after computing withinWindow above so windowExpired check below is valid)
        _lastNodeIDs = currentNodeIDs;
        _lastTapPoint = interpretedTapPoint;

        if (isDoubleTap)
        {
            // Double-tap confirmed
            _firstTapDuration = -1f;
            _secondTapDuration = -1f;
            _tapCount = 0;
            _lastProcessedTime = -1f; // Reset to allow next sequence
            return true;
        }
        else
        {
            _lastProcessedTime = currentTime;

            // Start a fresh sequence if window expired or no tap has been recorded yet
            bool windowExpired = !withinWindow;

            if (windowExpired || _tapCount == 0)
            {
                // Starting fresh sequence
                _tapCount = 1;
                _firstTapDuration = duration;
                _secondTapDuration = -1f;
                isFirstTap = true;
            }
            else if (_tapCount == 1)
            {
                // Second tap (but didn't match double-tap criteria)
                _tapCount = 2;
                _secondTapDuration = duration;
            }

            return false;
        }
    }

    /// <summary>
    /// Gets tap durations for debugging (first and second tap).
    /// </summary>
    public (float firstTap, float secondTap) GetTapDurations() => (_firstTapDuration, _secondTapDuration);

    /// <summary>
    /// Gets the current tap count.
    /// </summary>
    public int GetTapCount() => _tapCount;

    /// <summary>
    /// Resets tap count (call when tap sequence is broken).
    /// </summary>
    public void ResetTapCount()
    {
        _tapCount = 0;
    }

    /// <summary>
    /// Enables/disables training mode for calibrating tap durations.
    /// </summary>
    public void SetTrainingMode(bool enabled)
    {
        _trainingMode = enabled;
        _tapsRecorded = 0;
        _recordedDurations.Clear();
    }

    /// <summary>
    /// Updates configuration values.
    /// </summary>
    public void UpdateConfig(float tapMinDuration, float tapMaxDuration, float doubleTapTimeWindow, float tapCoolDown)
    {
        _tapMinDuration = tapMinDuration;
        _tapMaxDuration = tapMaxDuration;
        _doubleTapTimeWindow = doubleTapTimeWindow;
        _tapCoolDown = tapCoolDown;
    }

    // Private helpers

    /// <summary>
    /// Fallback classification based on total displacement when velocity-based detection fails.
    /// </summary>
    private void ClassifyByDisplacement()
    {
        if (!_gestureStartPosition.HasValue || !_gestureEndPosition.HasValue) return;
        if (_lastMotion == MotionType.Swipe) return;  // already confirmed swipe, don't downgrade

        Vector3 disp = _gestureEndPosition.Value - _gestureStartPosition.Value;
        float lateralDisp = new Vector2(disp.x, disp.z).magnitude;

        const float SWIPE_THRESHOLD = 0.015f;

        if (lateralDisp >= SWIPE_THRESHOLD)
        {
            _lastMotion = MotionType.Swipe;
            UnityEngine.Debug.Log($"[Displacement] Promoted to Swipe (lateral: {lateralDisp:F4}m)");
        }
        else if (_lastMotion == MotionType.Unknown)
        {
            _lastMotion = MotionType.Tap;
            UnityEngine.Debug.Log($"[Displacement] Classified as Tap (lateral: {lateralDisp:F4}m)");
        }
        // else: keep existing classification (Tap stays Tap for small-displacement taps)
    }

    private Vector3 GetSmoothedVelocity(Vector3 newVel)
    {
        _velocityBuffer.Enqueue(newVel);
        if (_velocityBuffer.Count > _velocityBufferSize)
            _velocityBuffer.Dequeue();

        Vector3 sum = Vector3.zero;
        foreach (var v in _velocityBuffer)
            sum += v;

        return sum / _velocityBuffer.Count;
    }

    private bool AreNodeListsSimilar(List<string> list1, List<string> list2)
    {
        if (list1 == null || list2 == null || list1.Count == 0 || list2.Count == 0)
            return false;

        var set1 = new HashSet<string>(list1);
        int matches = 0;
        foreach (var item in list2)
        {
            if (set1.Contains(item))
                matches++;
        }

        int minCount = Mathf.Min(list1.Count, list2.Count);
        return matches >= Mathf.CeilToInt(0.7f * minCount);
    }
}
