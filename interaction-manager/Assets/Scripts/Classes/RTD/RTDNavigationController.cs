using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Manages navigation through chart nodes (x-axis, y-axis, data marks).
/// Tracks current navigation context and indices.
/// </summary>
public class RTDNavigationController
{
    // ===== Enums =====
    public enum NavContext { XAxis, YAxis, DataMark }

    // ===== Dependencies =====
    private readonly InterfaceGraphVisualizer _graphVisualizer;
    private Func<int, bool> _isDataPointVisibleCallback;

    // ===== State =====
    private List<NodeComponent> _xAxisNodes = new List<NodeComponent>();
    private List<NodeComponent> _yAxisNodes = new List<NodeComponent>();
    private List<NodeComponent> _dataPointNodes = new List<NodeComponent>();

    private NavContext _currentNavContext = NavContext.DataMark;
    private int _currentXAxisIndex = -1;
    private int _currentYAxisIndex = -1;
    private int _currentDataMarkIndex = 0;

    private Vector2Int? _currentlyHighlightedPoint = null;
    private bool _highlightFromNavigation = false;

    // Controls navigation sort order. Effect depends on chart type:
    //   Scatter unchecked: left-to-right by X pixel column (then Y row).
    //   Scatter checked:   bottom-to-top by Y pixel row descending (then X column).
    //   Line/bar unchecked: global-ID (data-table) order.
    //   Line/bar checked:   series-first order (all of series A in X order, then series B, …).
    private bool _interleavedNavigation = false;

    // Current chart type — only scatter ("point") uses interleaved navigation.
    private string _chartType = "";

    // ===== Events =====
    public event Action<NodeComponent, List<NodeComponent>, List<float>> NavigationChanged = delegate { };
    public event Action<int> AutoPanToDataPoint = delegate { };

    // ===== Properties =====
    public NavContext CurrentContext => _currentNavContext;
    public Vector2Int? CurrentlyHighlightedPoint => _currentlyHighlightedPoint;
    public bool IsHighlightFromNavigation => _highlightFromNavigation;
    public int CurrentXAxisIndex => _currentXAxisIndex;
    public int CurrentYAxisIndex => _currentYAxisIndex;
    public int CurrentDataMarkIndex => _currentDataMarkIndex;
    public int? CurrentDataPointIndex => _currentDataMarkIndex >= 0 ? _currentDataMarkIndex : (int?)null;

    // ===== Constructor =====
    public RTDNavigationController(InterfaceGraphVisualizer graphVisualizer)
    {
        _graphVisualizer = graphVisualizer;
    }

    // ===== Public Methods =====

    public void SetVisibilityCallback(Func<int, bool> callback)
    {
        _isDataPointVisibleCallback = callback;
    }

    /// <summary>
    /// Controls navigation sort order (see _interleavedNavigation field for full table).
    /// Re-sorts immediately if nodes are already extracted.
    /// </summary>
    public void SetInterleavedNavigation(bool value)
    {
        _interleavedNavigation = value;
        if (_dataPointNodes.Count > 0)
            ApplyNavigationSort();
    }

    /// <summary>
    /// Inform the navigation controller of the current chart type.
    /// Scatter plots ("point") use spatial sort (X col, Y row) in the default mode
    /// instead of the renderer's global-ID order.
    /// </summary>
    public void SetChartType(string chartType)
    {
        _chartType = chartType ?? "";
        if (_dataPointNodes.Count > 0)
            ApplyNavigationSort();
    }

    public void EnableDataPointNavigation(bool enable, bool navigateToFirst = true)
    {
        if (!enable)
        {
            ClearNavigation();
            return;
        }

        ExtractDataPointNodes();

        if (_dataPointNodes.Count > 0)
        {
            _currentDataMarkIndex = navigateToFirst ? 0 : -1;
            _currentNavContext = NavContext.DataMark;

            if (navigateToFirst)
                NavigateToCurrentDataPoint();
        }
        else
        {
            Debug.LogWarning("No data point nodes found to navigate.");
            _currentDataMarkIndex = -1;
        }
    }

    public void ResetNavigationToStart()
    {
        ExtractDataPointNodes();

        if (_dataPointNodes.Count == 0)
        {
            Debug.LogWarning("ResetNavigationToStart: No data points found.");
            return;
        }

        _currentDataMarkIndex = 0;
        NavigateToCurrentDataPoint();
        Debug.Log("Navigation reset to start.");
    }

    /// <summary>
    /// Set navigation index without triggering highlight/TTS.
    /// Used by gestures to set starting point for navigation.
    /// </summary>
    public void SetNavigationIndexOnly(Vector2Int coord)
    {
        EnsureNodesExtracted();

        var (context, index) = FindNodeAtCoord(coord);
        if (context.HasValue)
        {
            SetContextAndIndex(context.Value, index);
            Debug.Log($"Set navigation index to {context.Value} {index + 1} at ({coord.x},{coord.y})");
        }
        else
        {
            LogNodeSearchFailure(coord);
        }
    }

    /// <summary>
    /// Navigate to a touched coordinate and trigger highlight/TTS.
    /// </summary>
    public void SetNavigationToTouchedPoint(Vector2Int coord, List<NodeComponent> matchingNodes = null, List<float> probabilities = null)
    {
        EnsureNodesExtracted();

        _currentlyHighlightedPoint = coord;

        var (context, index) = FindNodeAtCoord(coord);
        if (context.HasValue)
        {
            SetContextAndIndex(context.Value, index);
            Debug.Log($"Snapped to {context.Value} {index + 1} at ({coord.x},{coord.y})");
            NavigateToCurrentDataPoint(matchingNodes, probabilities);
        }
        else
        {
            Debug.LogWarning($"No node found at ({coord.x},{coord.y})");
        }
    }

    public void NavigateNextDataPoint()
    {
        AdvanceIndex(+1);

        if (CheckAndAutoPanToVisibleNode())
            return;

        NavigateToCurrentDataPoint();
    }

    public void NavigatePrevDataPoint()
    {
        AdvanceIndex(-1);

        if (CheckAndAutoPanToVisibleNode())
            return;

        NavigateToCurrentDataPoint();
    }

    public void NavigateToDataPointByIndex(int index)
    {
        ExtractDataPointNodes();

        if (_dataPointNodes.Count == 0)
        {
            Debug.LogWarning($"Cannot navigate to index {index}: No data points found.");
            return;
        }

        if (index < 0 || index >= _dataPointNodes.Count)
        {
            Debug.LogWarning($"Cannot navigate to index {index}: Out of range (0-{_dataPointNodes.Count - 1}).");
            return;
        }

        _currentNavContext = NavContext.DataMark;
        _currentDataMarkIndex = index;
        NavigateToCurrentDataPoint();
        Debug.Log($"Navigated to data point {index + 1}/{_dataPointNodes.Count}");
    }

    public void NavigateToDataPointByValue(string xField, object xValue, string yField, object yValue)
    {
        ExtractDataPointNodes();

        if (_dataPointNodes.Count == 0)
        {
            Debug.LogWarning("Cannot navigate: No data points found.");
            return;
        }

        int index = _dataPointNodes.FindIndex(node =>
        {
            if (node.values == null) return false;

            bool xMatch = node.values.TryGetValue(xField, out var nodeX) &&
                          nodeX.ToString() == xValue.ToString();

            if (!xMatch) return false;

            if (!node.values.TryGetValue(yField, out var nodeY)) return false;
            if (!float.TryParse(nodeY.ToString(), out float nodeYFloat)) return false;
            if (!float.TryParse(yValue.ToString(), out float yValueFloat)) return false;

            return Math.Abs(nodeYFloat - yValueFloat) < 0.01f;
        });

        if (index >= 0)
        {
            _currentNavContext = NavContext.DataMark;
            _currentDataMarkIndex = index;
            NavigateToCurrentDataPoint();
            Debug.Log($"Found and navigated to point with {xField}={xValue}, {yField}={yValue} at viewport index {index}");
        }
        else
        {
            Debug.LogWarning($"Could not find data point with {xField}={xValue}, {yField}={yValue}");
        }
    }

    public void ClearNavigation()
    {
        _currentDataMarkIndex = -1;
        _currentXAxisIndex = -1;
        _currentYAxisIndex = -1;
        // Reset to DataMark as the default context for the next navigation session.
        _currentNavContext = NavContext.DataMark;
        _currentlyHighlightedPoint = null;
        _highlightFromNavigation = false;
    }

    // ===== Internal Methods =====

    private void EnsureNodesExtracted()
    {
        if (_dataPointNodes.Count == 0)
            ExtractDataPointNodes();
    }

    /// <summary>
    /// Find which context (x-axis, y-axis, data-mark) a coordinate belongs to.
    /// Returns the context and index, or (null, -1) if not found.
    /// </summary>
    private (NavContext? context, int index) FindNodeAtCoord(Vector2Int coord)
    {
        // Try x-axis
        int xIdx = FindNodeIndex(_xAxisNodes, coord);
        if (xIdx >= 0)
            return (NavContext.XAxis, xIdx);

        // Try y-axis
        int yIdx = FindNodeIndex(_yAxisNodes, coord);
        if (yIdx >= 0)
            return (NavContext.YAxis, yIdx);

        // Try data-mark (check both xy and barCoordinates)
        int dIdx = _dataPointNodes.FindIndex(n =>
        {
            if (n.xy != null && n.xy.Length == 2 && n.xy[0] == coord.x && n.xy[1] == coord.y)
                return true;
            if (n.barCoordinates != null && n.barCoordinates.Count > 0)
                return n.barCoordinates.Any(bc => bc.x == coord.x && bc.y == coord.y);
            return false;
        });

        if (dIdx >= 0)
            return (NavContext.DataMark, dIdx);

        return (null, -1);
    }

    private static int FindNodeIndex(List<NodeComponent> nodes, Vector2Int coord)
    {
        return nodes.FindIndex(n => n.xy != null && n.xy.Length == 2 && n.xy[0] == coord.x && n.xy[1] == coord.y);
    }

    private void SetContextAndIndex(NavContext context, int index)
    {
        _currentNavContext = context;
        switch (context)
        {
            case NavContext.XAxis: _currentXAxisIndex = index; break;
            case NavContext.YAxis: _currentYAxisIndex = index; break;
            case NavContext.DataMark: _currentDataMarkIndex = index; break;
        }
    }

    private void AdvanceIndex(int direction)
    {
        switch (_currentNavContext)
        {
            case NavContext.XAxis:
                if (_xAxisNodes.Count > 0)
                    _currentXAxisIndex = Mathf.Clamp(_currentXAxisIndex + direction, 0, _xAxisNodes.Count - 1);
                break;
            case NavContext.YAxis:
                if (_yAxisNodes.Count > 0)
                    _currentYAxisIndex = Mathf.Clamp(_currentYAxisIndex + direction, 0, _yAxisNodes.Count - 1);
                break;
            case NavContext.DataMark:
                if (_dataPointNodes.Count > 0)
                    _currentDataMarkIndex = Mathf.Clamp(_currentDataMarkIndex + direction, 0, _dataPointNodes.Count - 1);
                break;
        }
    }

    private void LogNodeSearchFailure(Vector2Int coord)
    {
        Debug.LogWarning($"No node found at ({coord.x},{coord.y})");
        Debug.Log($"Total data point nodes: {_dataPointNodes.Count}");

        if (_dataPointNodes.Count > 0)
        {
            int nodesAtColumn = 0;
            foreach (var n in _dataPointNodes)
            {
                if (n.xy != null && n.xy.Length >= 2 && n.xy[0] == coord.x)
                {
                    nodesAtColumn++;
                    Debug.Log($"Node {n.id} at col {coord.x}: xy=({n.xy[0]},{n.xy[1]}), barCoords.Count={n.barCoordinates?.Count ?? 0}");
                }
            }
            Debug.Log($"Found {nodesAtColumn} nodes at column {coord.x}");
        }
    }

    private void ExtractDataPointNodes()
    {
        _xAxisNodes.Clear();
        _yAxisNodes.Clear();
        _dataPointNodes.Clear();

        var nodes = _graphVisualizer?.GetNodes();
        if (nodes == null)
        {
            Debug.LogWarning("GraphVisualizer or nodes not available.");
            return;
        }

        foreach (var nodeObj in nodes.Values)
        {
            var node = nodeObj.GetComponent<NodeComponent>();
            if (node == null)
                continue;

            if (node.type.Contains("x-axis"))
                _xAxisNodes.Add(node);
            else if (node.type.Contains("y-axis"))
                _yAxisNodes.Add(node);
            else if (node.type.Contains("data-mark") || node.type.Contains("data-point"))
                _dataPointNodes.Add(node);
        }

        // Sort axis nodes by position
        _xAxisNodes = _xAxisNodes
            .OrderBy(n => n.xy != null && n.xy.Length >= 2 ? n.xy[0] : int.MaxValue)
            .ThenBy(n => n.xy != null && n.xy.Length >= 2 ? n.xy[1] : int.MaxValue)
            .ToList();

        _yAxisNodes = _yAxisNodes
            .OrderBy(n => n.xy != null && n.xy.Length >= 2 ? n.xy[0] : int.MaxValue)
            .ThenBy(n => n.xy != null && n.xy.Length >= 2 ? n.xy[1] : int.MaxValue)
            .ToList();

        ApplyNavigationSort();

        int visibleCount = _dataPointNodes.Count(n => n.visibility);
        int hiddenCount = _dataPointNodes.Count(n => !n.visibility);
        Debug.Log($"Extracted: {_xAxisNodes.Count} x-axis, {_yAxisNodes.Count} y-axis, {_dataPointNodes.Count} data-points ({visibleCount} visible, {hiddenCount} hidden)");
    }

    /// <summary>
    /// Sorts _dataPointNodes based on _interleavedNavigation and _chartType.
    ///
    /// Scatter, unchecked: visible left-to-right (xPixelCol asc, yPixelRow asc); hidden in global-ID order.
    /// Scatter, checked:   visible bottom-to-top (yPixelRow desc, xPixelCol asc); hidden in global-ID order.
    ///
    /// Line/bar, unchecked: global renderer ID order (data-table order).
    /// Line/bar, checked:   series-first — all nodes in series A (by xPixelCol asc), then series B, etc.
    ///                       Series order determined by first appearance in global-ID order.
    /// </summary>
    private void ApplyNavigationSort()
    {
        bool isScatter = _chartType == "point";

        if (isScatter)
        {
            var visible = _dataPointNodes
                .Where(n => n.visibility && n.xy != null && n.xy.Length >= 2)
                .ToList();

            var hidden = _dataPointNodes
                .Where(n => !n.visibility || n.xy == null || n.xy.Length < 2)
                .OrderBy(n => ParseGlobalIndex(n.id) ?? int.MaxValue)
                .ToList();

            if (!_interleavedNavigation)
            {
                // Unchecked scatter: left-to-right by X, then ascending Y row
                visible = visible
                    .OrderBy(n => n.xy[0])
                    .ThenBy(n => n.xy[1])
                    .ToList();
            }
            else
            {
                // Checked scatter: bottom-to-top (descending row = ascending Y value), then X
                visible = visible
                    .OrderByDescending(n => n.xy[1])
                    .ThenBy(n => n.xy[0])
                    .ToList();
            }

            _dataPointNodes = visible.Concat(hidden).ToList();
            return;
        }

        // Line / bar charts
        if (!_interleavedNavigation)
        {
            // Unchecked: data-table order via global renderer ID
            _dataPointNodes = _dataPointNodes
                .OrderBy(n => ParseGlobalIndex(n.id) ?? int.MaxValue)
                .ToList();
            return;
        }

        // Checked line/bar: series-first order
        // Build seriesOrder: first-appearance index per series string (in global-ID order)
        var seriesOrder = new Dictionary<string, int>();
        int nextSeriesIdx = 0;
        foreach (var n in _dataPointNodes.OrderBy(n => ParseGlobalIndex(n.id) ?? int.MaxValue))
        {
            string s = n.series ?? "";
            if (!seriesOrder.ContainsKey(s))
                seriesOrder[s] = nextSeriesIdx++;
        }

        _dataPointNodes = _dataPointNodes
            .OrderBy(n => seriesOrder.TryGetValue(n.series ?? "", out int si) ? si : int.MaxValue)
            .ThenBy(n => n.xy != null && n.xy.Length >= 2 ? n.xy[0] : int.MaxValue)
            .ThenBy(n => ParseGlobalIndex(n.id) ?? int.MaxValue)
            .ToList();
    }

    private void NavigateToCurrentDataPoint(List<NodeComponent> nodes = null, List<float> probabilities = null)
    {
        NodeComponent node = null;

        if (nodes == null || nodes.Count == 0)
        {
            node = GetCurrentNode();

            if (node != null)
            {
                nodes = new List<NodeComponent> { node };
                probabilities = new List<float> { 1.0f };
            }
        }

        if (nodes == null || nodes.Count == 0 || nodes[0] == null)
        {
            Debug.LogWarning("No valid node to navigate to");
            return;
        }

        node = nodes[0];

        if (node.xy == null || node.xy.Length < 2)
        {
            Debug.LogError($"Node {node.id} has null or invalid xy coordinates! visibility={node.visibility}");
            return;
        }

        int x = node.xy[0];
        int y = node.xy[1];
        _currentlyHighlightedPoint = new Vector2Int(x, y);
        _highlightFromNavigation = true;

        Debug.Log($"Navigating to {_currentNavContext}: {node.id} at ({x},{y}), visibility={node.visibility}");
        NavigationChanged?.Invoke(node, nodes, probabilities);
    }

    private NodeComponent GetCurrentNode()
    {
        switch (_currentNavContext)
        {
            case NavContext.XAxis:
                if (_currentXAxisIndex >= 0 && _currentXAxisIndex < _xAxisNodes.Count)
                    return _xAxisNodes[_currentXAxisIndex];
                break;
            case NavContext.YAxis:
                if (_currentYAxisIndex >= 0 && _currentYAxisIndex < _yAxisNodes.Count)
                    return _yAxisNodes[_currentYAxisIndex];
                break;
            case NavContext.DataMark:
                if (_currentDataMarkIndex >= 0 && _currentDataMarkIndex < _dataPointNodes.Count)
                    return _dataPointNodes[_currentDataMarkIndex];
                break;
        }
        return null;
    }

    private bool CheckAndAutoPanToVisibleNode()
    {
        if (_currentNavContext != NavContext.DataMark)
            return false;

        if (_currentDataMarkIndex < 0 || _currentDataMarkIndex >= _dataPointNodes.Count)
            return false;

        NodeComponent currentNode = _dataPointNodes[_currentDataMarkIndex];

        int? globalIndex = ParseGlobalIndex(currentNode.id);
        if (!globalIndex.HasValue)
        {
            Debug.LogWarning($"Could not extract global index from node ID: {currentNode.id}");
            return false;
        }

        Debug.Log($"Checking node at list index {_currentDataMarkIndex}: id={currentNode.id}, globalIndex={globalIndex.Value}, visibility={currentNode.visibility}");

        // Check if node has invalid coordinates (stale node from previous render)
        bool hasValidCoordinates = currentNode.xy != null && currentNode.xy.Length >= 2 && !(currentNode.xy[0] == 0 && currentNode.xy[1] == 0);

        if (!hasValidCoordinates)
        {
            Debug.Log($"Node {currentNode.id} has invalid coordinates - triggering auto-pan (globalIndex={globalIndex.Value})");
            AutoPanToDataPoint?.Invoke(globalIndex.Value);
            return true;
        }

        // Check if data point is visible using callback
        if (_isDataPointVisibleCallback != null)
        {
            if (!_isDataPointVisibleCallback(globalIndex.Value))
            {
                Debug.Log($"Auto-panning to hidden data point {globalIndex.Value} (list index {_currentDataMarkIndex})");
                AutoPanToDataPoint?.Invoke(globalIndex.Value);
                return true;
            }
        }
        else if (!currentNode.visibility)
        {
            Debug.Log($"Auto-panning to hidden data point {globalIndex.Value} (using node.visibility fallback)");
            AutoPanToDataPoint?.Invoke(globalIndex.Value);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parse global data point index from a node ID like "data-point-14".
    /// </summary>
    private static int? ParseGlobalIndex(string nodeId)
    {
        var parts = nodeId.Split('-');
        if (parts.Length >= 3 && int.TryParse(parts[2], out int index))
            return index;
        return null;
    }
}
