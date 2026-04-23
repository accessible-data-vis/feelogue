using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Stores and restores gesture highlights across chart regeneration (zoom/pan).
/// Also tracks most recent touch positions per hand for re-highlighting.
/// </summary>
public class RTDGestureHighlightPersistence
{
    private readonly InterfaceButtonGUI _buttonGUI;
    private readonly InterfaceGraphVisualizer _graphVisualizer;

    // Store data values for all active gesture highlights (left/right hands)
    private Dictionary<string, List<(object xValue, object yValue)>> _storedGestureValues
        = new Dictionary<string, List<(object, object)>>();

    // Track most recent touch center points per hand
    private List<Vector2Int> _lastLeftCenterPoints = new List<Vector2Int>();
    private List<Vector2Int> _lastRightCenterPoints = new List<Vector2Int>();

    public RTDGestureHighlightPersistence(InterfaceButtonGUI buttonGUI, InterfaceGraphVisualizer graphVisualizer)
    {
        _buttonGUI = buttonGUI;
        _graphVisualizer = graphVisualizer;
    }

    /// <summary>
    /// Record center points for a hand's touch.
    /// </summary>
    public void RecordTouchCenterPoints(string hand, List<Vector2Int> coords)
    {
        if (hand == "left")
            _lastLeftCenterPoints = new List<Vector2Int>(coords);
        else if (hand == "right")
            _lastRightCenterPoints = new List<Vector2Int>(coords);
    }

    /// <summary>
    /// Re-highlight most recent touch positions for all hands.
    /// Returns a message describing what was restored, or null if nothing to restore.
    /// </summary>
    public string GetMostRecentTouchInfo(out List<(List<Vector2Int> coords, HighlightShape shape, string hand)> toRestore)
    {
        toRestore = new List<(List<Vector2Int>, HighlightShape, string)>();
        bool hasLeft = _lastLeftCenterPoints.Count > 0;
        bool hasRight = _lastRightCenterPoints.Count > 0;

        if (!hasLeft && !hasRight)
            return null;

        string message = "";

        if (hasLeft)
        {
            Debug.Log($"Re-highlighting {_lastLeftCenterPoints.Count} left touch center points");
            toRestore.Add((_lastLeftCenterPoints, HighlightShape.Box, "left"));
            message += "Left hand restored. ";
        }

        if (hasRight)
        {
            Debug.Log($"Re-highlighting {_lastRightCenterPoints.Count} right touch center points");
            toRestore.Add((_lastRightCenterPoints, HighlightShape.Box, "right"));
            message += "Right hand restored. ";
        }

        return message.Trim();
    }

    /// <summary>
    /// Store data values for all currently active gesture highlights before chart regeneration.
    /// </summary>
    public void StoreAllGestureHighlights(
        Dictionary<string, List<Vector2Int>> activeGestures,
        Func<Vector2Int, (object xValue, object yValue)?> getNodeValues)
    {
        _storedGestureValues.Clear();

        foreach (var handEntry in activeGestures)
        {
            string hand = handEntry.Key;
            var coords = handEntry.Value;
            var values = new List<(object, object)>();

            foreach (var coord in coords)
            {
                var nodeValues = getNodeValues(coord);
                if (nodeValues.HasValue)
                {
                    values.Add(nodeValues.Value);
                    Debug.Log($"Stored {hand} gesture highlight at ({coord.x},{coord.y}): X={nodeValues.Value.xValue}, Y={nodeValues.Value.yValue}");
                }
            }

            if (values.Count > 0)
                _storedGestureValues[hand] = values;
        }

        Debug.Log($"Stored {_storedGestureValues.Sum(kv => kv.Value.Count)} total gesture highlights across {_storedGestureValues.Count} hands");
    }

    /// <summary>
    /// Restore all gesture highlights after chart regeneration.
    /// Returns list of (coords, shape, hand) tuples to highlight.
    /// </summary>
    public List<(List<Vector2Int> coords, HighlightShape shape, string hand)> GetRestoredGestureHighlights()
    {
        var results = new List<(List<Vector2Int>, HighlightShape, string)>();

        if (_storedGestureValues.Count == 0)
        {
            Debug.Log("No stored gesture highlights to restore");
            return results;
        }

        // Shape is now read from the gesture config inside ShowTouchHighlights; pass Box as sentinel.
        HighlightShape shape = HighlightShape.Box;
        int restoredCount = 0;

        foreach (var handEntry in _storedGestureValues)
        {
            string hand = handEntry.Key;
            var valuesList = handEntry.Value;
            var coordsToHighlight = new List<Vector2Int>();

            foreach (var (xValue, yValue) in valuesList)
            {
                List<float> searchValues = new List<float>();

                if (float.TryParse(xValue.ToString(), out float xFloat))
                    searchValues.Add(xFloat);
                if (float.TryParse(yValue.ToString(), out float yFloat))
                    searchValues.Add(yFloat);

                if (searchValues.Count == 0)
                {
                    Debug.LogWarning($"Cannot restore {hand} gesture highlight - values not numeric: X={xValue}, Y={yValue}");
                    continue;
                }

                var matchingNodes = _graphVisualizer.GetMatchingNodesBasedOnValue(searchValues, requireAll: true);

                if (matchingNodes.Count > 0)
                {
                    var node = matchingNodes[0];
                    if (node.xy != null && node.xy.Length >= 2)
                    {
                        Vector2Int coord = new Vector2Int(node.xy[0], node.xy[1]);
                        coordsToHighlight.Add(coord);
                        restoredCount++;
                        Debug.Log($"Found {hand} gesture point at ({coord.x}, {coord.y}) for values X={xValue}, Y={yValue}");
                    }
                }
                else
                {
                    Debug.LogWarning($"Could not find node with X={xValue}, Y={yValue} after zoom");
                }
            }

            if (coordsToHighlight.Count > 0)
            {
                Debug.Log($"Restoring {coordsToHighlight.Count} {hand} gesture highlights");
                results.Add((coordsToHighlight, shape, hand));
            }
        }

        Debug.Log($"Restored {restoredCount} total gesture highlights");
        return results;
    }

    /// <summary>
    /// Clear stored gesture highlight values.
    /// </summary>
    public void ClearStoredGestureValues()
    {
        _storedGestureValues.Clear();
    }

}
