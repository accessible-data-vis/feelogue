using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public static class TouchDebugger
{
    public enum TapResult
    {
        None,        // not valid, ignored
        SingleTap,   // valid single tap
        DoubleTap,   // valid double tap
        Swipe
    }

    public struct TapDebugInfo
    {
        public string FingerName;
        public HashSet<Vector2Int> Coords;
        public float Duration;
        public int TapCount;
        public float TimeSinceLast;
        public bool DoubleTapMode;
        public TapResult Result;
        public object LastMotion;

        // Thresholds (optional — leave at 0 to omit from log)
        public float MinDuration;
        public float MaxDuration;
        public float Cooldown;
        public float DoubleTapWindow;

        // Spatial inference (optional)
        public Vector2Int? InterpretedTapPoint;
        public List<NodeComponent> MatchingNodes;
        public List<float> Probabilities;
        public Vector2? MostLikelyPin;
        public float MostLikelyProbability;

        // Double-tap breakdown (optional)
        public bool? ValidDuration;
        public bool? WithinWindow;
        public bool? SameLocation;
        public bool? SameNodes;
        public float? FirstTapDuration;
        public float? SecondTapDuration;
    }

    public static void LogReport(TapDebugInfo info)
    {
        var log = new System.Text.StringBuilder();

        log.AppendLine($" [TAP DEBUG REPORT] → RESULT: {info.Result}");
        log.AppendLine($"- Finger: {info.FingerName}");
        log.AppendLine($"- Raw coords: {(info.Coords != null && info.Coords.Count > 0 ? string.Join(", ", info.Coords) : "none")}");
        log.AppendLine($"- Held time: {info.Duration:F3}");
        log.AppendLine($"- Tap count: {info.TapCount}");
        log.AppendLine($"- Since last tap: {info.TimeSinceLast:F3}s");
        log.AppendLine($"- Modes: DoubleTap={info.DoubleTapMode}");

        if (info.MinDuration > 0 || info.MaxDuration > 0)
        {
            log.AppendLine($"- Thresholds: min={info.MinDuration:F3}, " +
                           $"max={info.MaxDuration:F3}, " +
                           $"cooldown={info.Cooldown:F3}, " +
                           $"doubleTapWindow={info.DoubleTapWindow:F3}");
        }

        log.AppendLine($"- Motion classification: {info.LastMotion}");

        if (info.InterpretedTapPoint.HasValue)
            log.AppendLine($"- InterpretedTapPoint: {info.InterpretedTapPoint.Value}");

        if (info.MatchingNodes != null)
            log.AppendLine($"- Matching nodes: {info.MatchingNodes.Count} → {string.Join(", ", info.MatchingNodes.Select(n => n.id))}");

        if (info.Probabilities != null && info.Probabilities.Count > 0)
        {
            log.AppendLine($"- Probabilities: [{string.Join(", ", info.Probabilities.Select(p => p.ToString("F4")))}]");
            log.AppendLine($"- Most likely pin: {info.MostLikelyPin} (p={info.MostLikelyProbability:F4})");
        }

        if (info.Result == TapResult.DoubleTap)
        {
            log.AppendLine(" [Double Tap Breakdown]");
            log.AppendLine($"  - First tap duration: {info.FirstTapDuration:F3}s");
            log.AppendLine($"  - Second tap duration: {info.SecondTapDuration:F3}s");
            log.AppendLine($"  - Valid duration: {info.ValidDuration}");
            log.AppendLine($"  - Within window: {info.WithinWindow}");
            log.AppendLine($"  - Same location: {info.SameLocation}");
            log.AppendLine($"  - Same nodes: {info.SameNodes}");
        }

        UnityEngine.Debug.Log(log.ToString());
    }

    public static void LogTouchCoordinates(HashSet<Vector2Int> coords, string context = "")
    {
        if (coords == null || coords.Count == 0)
        {
            Debug.Log($"[TouchDebug{(string.IsNullOrEmpty(context) ? "" : $" - {context}")}] No coordinates");
            return;
        }

        Debug.Log($"[TouchDebug{(string.IsNullOrEmpty(context) ? "" : $" - {context}")}] " +
                  $"Coords ({coords.Count}): {string.Join(", ", coords)}");
    }

    public static void LogGestureClassification(object motionType, float velocityY, float velocityXZ, string fingerName)
    {
        Debug.Log($"[GestureDebug - {fingerName}] Motion: {motionType}, vY: {velocityY:F3}, vXZ: {velocityXZ:F3}");
    }

    public static void LogTiming(string fingerName, float duration, float minDuration, float maxDuration, bool isValid)
    {
        Debug.Log($"[TimingDebug - {fingerName}] Duration: {duration:F3}s " +
                  $"(min: {minDuration:F3}, max: {maxDuration:F3}) → {(isValid ? "VALID" : "INVALID")}");
    }
}
