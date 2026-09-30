using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Tracks the most recent touch positions per hand for re-highlighting.
/// </summary>
public class RTDGestureHighlightPersistence
{
    private readonly InterfaceButtonGUI _buttonGUI;
    private readonly InterfaceGraphVisualizer _graphVisualizer;

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
            AppLog.Detail(LogArea.Render, $"Re-highlighting {_lastLeftCenterPoints.Count} left touch center points");
            toRestore.Add((_lastLeftCenterPoints, HighlightShape.Box, "left"));
            message += "Left hand restored. ";
        }

        if (hasRight)
        {
            AppLog.Detail(LogArea.Render, $"Re-highlighting {_lastRightCenterPoints.Count} right touch center points");
            toRestore.Add((_lastRightCenterPoints, HighlightShape.Box, "right"));
            message += "Right hand restored. ";
        }

        return message.Trim();
    }
}
