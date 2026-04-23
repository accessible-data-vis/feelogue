using UnityEngine;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Handles spatial inference and probabilistic node matching for touch events.
/// Integrates with TouchProcessor for probability calculations and manages alignment drift detection.
/// </summary>
public class TouchSpatialProcessor
{
    private TouchProcessor _touchProcessor;
    private GraphVisualizer _visualizer;

    // Alignment drift detection
    private int _consecutiveMisalignments = 0;
    private const int MISALIGNMENT_THRESHOLD = 30; // ~0.5s at 60fps

    // Events
    public event System.Action<Vector2Int, HashSet<Vector2Int>, List<NodeComponent>> OnTouchProcessed;

    public TouchSpatialProcessor(TouchProcessor touchProcessor, GraphVisualizer visualizer)
    {
        _touchProcessor = touchProcessor;
        _visualizer = visualizer;
    }

    /// <summary>
    /// Processes touch coordinates using spatial inference.
    /// </summary>
    /// <param name="coords">All touched pin coordinates (raised + lowered)</param>
    /// <param name="fingerName">Name of the finger for logging</param>
    /// <param name="calibrationMode">Whether in calibration mode</param>
    /// <param name="matchingNodes">Out parameter: nodes that match the touch</param>
    /// <param name="interpretedPoint">Out parameter: the interpreted tap point</param>
    /// <returns>True if processing succeeded, false if no nodes found</returns>
    public bool ProcessTouch(
        HashSet<Vector2Int> coords,
        string fingerName,
        bool calibrationMode,
        out List<NodeComponent> matchingNodes,
        out Vector2Int interpretedPoint,
        out Dictionary<string, object> nodesDict)
    {
        matchingNodes = null;
        interpretedPoint = Vector2Int.zero;
        nodesDict = null;

        if (coords == null || coords.Count == 0)
            return false;

        // CALIBRATION MODE: Just compute centroid, no node matching needed
        if (calibrationMode)
        {
            interpretedPoint = new Vector2Int(
                coords.Sum(c => c.x) / coords.Count,
                coords.Sum(c => c.y) / coords.Count
            );

            OnTouchProcessed?.Invoke(interpretedPoint, coords, null);
            UnityEngine.Debug.Log($"[CALIBRATION] Touch at centroid {interpretedPoint}, raw coords: {string.Join(", ", coords)}");
            return true;
        }

        // Filter to only pins with data AFTER collecting full spatial footprint
        matchingNodes = _visualizer.GetMatchingNodes(coords);
        if (matchingNodes == null || matchingNodes.Count == 0)
        {
            UnityEngine.Debug.LogWarning($"[{fingerName}] Touch detected at {coords.Count} pin(s), but no raised pins with data in region: {string.Join(", ", coords)}");
            return false;
        }

        // Pass FULL coords (including lowered pins) for accurate spatial calculation
        _touchProcessor.ProcessTouch(coords, matchingNodes);

        interpretedPoint = _touchProcessor.interpretedTapPoint;

        // Fire event for calibration system (if listening)
        OnTouchProcessed?.Invoke(interpretedPoint, coords, matchingNodes);

        // Check for tracking misalignment
        CheckForMisalignment(coords, interpretedPoint, fingerName);

        // Log distribution data
        UnityEngine.Debug.Log("Distribution Calculation: " +
                  $"Most Likely Pin: {_touchProcessor.mostLikelyPin} (p={_touchProcessor.mostLikelyProbability:F4}), " +
                  $"closestPoint: {_touchProcessor.closestPoint}, " +
                  $"Pins: {string.Join(", ", _touchProcessor.nodePositions.Select(p => $"({p.x},{p.y})"))}"
        );

        // Build nodes dictionary with probabilities
        nodesDict = new Dictionary<string, object>();
        for (int i = 0; i < matchingNodes.Count; i++)
        {
            var node = matchingNodes[i];
            nodesDict[node.id] = new
            {
                node_xy = node.xy,
                node_values = node.values ?? new Dictionary<string, object>(),
                probability = i < _touchProcessor.probabilities.Count ? _touchProcessor.probabilities[i] : 0f
            };
        }

        return true;
    }

    /// <summary>
    /// Gets high-confidence node positions based on probability threshold.
    /// </summary>
    public List<Vector2Int> GetHighConfidencePositions(float threshold = 0.2f)
    {
        return _touchProcessor.GetHighConfidencePositions(threshold);
    }

    /// <summary>
    /// Gets the most likely pin from the last touch processing.
    /// </summary>
    public Vector2 GetMostLikelyPin() => _touchProcessor.mostLikelyPin;

    /// <summary>
    /// Gets the most likely probability from the last touch processing.
    /// </summary>
    public float GetMostLikelyProbability() => _touchProcessor.mostLikelyProbability;

    /// <summary>
    /// Gets the interpreted tap point from the last touch processing.
    /// </summary>
    public Vector2Int GetInterpretedTapPoint() => _touchProcessor.interpretedTapPoint;

    /// <summary>
    /// Gets the node positions from the last touch processing.
    /// </summary>
    public HashSet<Vector2Int> GetNodePositions() => _touchProcessor.nodePositions;

    /// <summary>
    /// Gets the probabilities from the last touch processing.
    /// </summary>
    public List<float> GetProbabilities() => _touchProcessor.probabilities;

    /// <summary>
    /// Resets alignment drift counter.
    /// </summary>
    public void ResetMisalignmentCounter()
    {
        _consecutiveMisalignments = 0;
    }

    // Private helpers

    private void CheckForMisalignment(HashSet<Vector2Int> coords, Vector2Int interpretedPoint, string fingerName)
    {
        if (coords == null || coords.Count == 0) return;

        // Check if the interpreted point is far from all collision points
        float minDist = float.MaxValue;
        foreach (var coord in coords)
        {
            float dist = Vector2Int.Distance(coord, interpretedPoint);
            if (dist < minDist) minDist = dist;
        }

        // If interpreted point is more than 3 pins away from any collision
        if (minDist > 3f)
        {
            _consecutiveMisalignments++;

            if (_consecutiveMisalignments >= MISALIGNMENT_THRESHOLD)
            {
                UnityEngine.Debug.LogWarning(
                    $"[{fingerName}] TRACKING MISALIGNMENT DETECTED!\n" +
                    $"Interpreted point {interpretedPoint} is {minDist:F1} pins away from collision.\n" +
                    $"Collision coords: {string.Join(", ", coords)}\n" +
                    $"This may indicate Leap Motion tracking is drifting from physical hand position."
                );
                _consecutiveMisalignments = 0; // Reset after warning
            }
        }
        else
        {
            // Reset counter when alignment is good
            _consecutiveMisalignments = 0;
        }
    }
}
