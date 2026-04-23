using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;
using System.Linq;
using TMPro;

public class GraphVisualizer : MonoBehaviour, InterfaceGraphVisualizer
{
    public GameObject nodePrefab; // Prefab for nodes
    public GameObject labelPrefab; // Prefab for node labels
    public float nodeScale = 0.1f; // Size of nodes
    public float edgeWidth = 0.05f; // Width of edges
    public float labelHeight = 0.005f;
    public float labelVerticalSpacing = 0.008f; // Spacing between stacked labels
    private Dictionary<string, GameObject> nodes = new Dictionary<string, GameObject>();
    private Dictionary<string, Transform> _pinLookup;
    private Dictionary<string, int> _labelCountPerPin = new Dictionary<string, int>(); // Track labels per pin
    private Dictionary<Vector2Int, List<NodeComponent>> _coordIndex = new Dictionary<Vector2Int, List<NodeComponent>>();

    // ===== Viewport Overlay =====
    private GameObject _viewportOverlay;
    private LineRenderer _viewportLineRenderer;

    // ===== Constants =====
    private const float GRAPH_OFFSET_Y = 0.125f;
    private const float GRAPH_OFFSET_Z = 0.125f;
    private const float SCALE_FACTOR = 0.025f;
    private const float NORMALIZATION_RANGE = 5f;
    private const float NORMALIZATION_CENTER = 2.5f;

    /// <summary>
    /// Generate graph from chart nodes.
    /// </summary>
    public void GenerateGraph(List<ChartNode> chartNodes, string chartType, string dataType)
    {
        Debug.Log($"GenerateGraph: {chartNodes.Count} nodes");
        Debug.Log("Chart Type: " + chartType);
        Debug.Log("Data: " + dataType);

        // Initialize pin lookup
        if (!InitializePinLookup())
            return;

        // Clear old visualization
        ClearVisualization();

        // Compatibility shim: CreateNodes was designed around the legacy Node struct.
        // ConvertChartNodesToLegacyNodes bridges the new ChartNode pipeline until
        // CreateNodes is refactored to accept ChartNode directly.
        var legacyNodes = ConvertChartNodesToLegacyNodes(chartNodes);

        // Calculate graph bounds
        var bounds = CalculateGraphBounds(legacyNodes);

        // Get graph positioning parameters
        Vector3 graphOffset = CalculateGraphOffset();
        float scaleFactor = SCALE_FACTOR;

        // Create Nodes
        CreateNodes(legacyNodes, bounds, graphOffset, scaleFactor, chartType, dataType);

        // Generate and create edges
        var edges = GenerateEdgesFromNodes(chartNodes);
        CreateEdges(edges, scaleFactor);

        BuildCoordIndex();
        Debug.Log($"Graph visualization completed: {chartNodes.Count} nodes, {edges.Count} edges");
    }

    private void BuildCoordIndex()
    {
        _coordIndex.Clear();
        foreach (var nodeObj in nodes.Values)
        {
            var nc = nodeObj.GetComponent<NodeComponent>();
            if (nc == null) continue;

            if (nc.barCoordinates != null && nc.barCoordinates.Count > 0)
            {
                foreach (var coord in nc.barCoordinates)
                {
                    var key = new Vector2Int(coord.x, coord.y);
                    if (!_coordIndex.TryGetValue(key, out var list))
                        _coordIndex[key] = list = new List<NodeComponent>();
                    list.Add(nc);
                }
            }
            else if (nc.xy != null && nc.xy.Length == 2)
            {
                var key = new Vector2Int(nc.xy[0], nc.xy[1]);
                if (!_coordIndex.TryGetValue(key, out var list))
                    _coordIndex[key] = list = new List<NodeComponent>();
                list.Add(nc);
            }
        }
    }

    /// <summary>
    /// Legacy JSON-based graph generation (kept for backward compatibility).
    /// </summary>
    public void GenerateGraph(string json, string chartType, string dataType)
    {
        Debug.Log("Chart Type: " + chartType);
        Debug.Log("Data: " + dataType);

        // Initialize pin lookup
        if (!InitializePinLookup())
            return;

        // Clear old visualization
        ClearVisualization();

        // Deserialize JSON
        GraphData graph = JsonConvert.DeserializeObject<GraphData>(json);

        // Calculate graph bounds
        var bounds = CalculateGraphBounds(graph.nodes);

        // Get graph positioning parameters
        Vector3 graphOffset = CalculateGraphOffset();
        float scaleFactor = SCALE_FACTOR;

        // Create Nodes
        CreateNodes(graph.nodes, bounds, graphOffset, scaleFactor, chartType, dataType);

        // Create Edges
        CreateEdges(graph.links, scaleFactor);

        Debug.Log("Graph visualization completed.");
    }

    // ===== Helper Methods =====

    /// <summary>
    /// Compatibility shim: converts ChartNode list to the legacy Node format used by CreateNodes.
    /// Exists because CreateNodes was designed around Node; remove when CreateNodes is refactored
    /// to accept List&lt;ChartNode&gt; directly.
    /// </summary>
    private List<Node> ConvertChartNodesToLegacyNodes(List<ChartNode> chartNodes)
    {
        var legacyNodes = new List<Node>();

        foreach (var chartNode in chartNodes)
        {
            var node = new Node
            {
                id = chartNode.Id,
                type = chartNode.Type,
                visibility = chartNode.Visibility,
                series = chartNode.Series,
                symbol = chartNode.Symbol,
                values = chartNode.Values,
                coordinates = chartNode.Coordinates.Select(coord => new float[] { coord.x, coord.y }).ToArray(),
                xy = chartNode.Coordinates.Count > 0 ? new int[] { chartNode.Coordinates[0].x, chartNode.Coordinates[0].y } : new int[] { 0, 0 },
            };

            legacyNodes.Add(node);
        }

        return legacyNodes;
    }

    /// <summary>
    /// Generate edges from ChartNode list.
    /// Creates edges between:
    /// - Adjacent X-axis ticks
    /// - Adjacent Y-axis ticks
    /// - Sequential data points
    /// - Data points to nearest axis ticks
    /// </summary>
    private List<Edge> GenerateEdgesFromNodes(List<ChartNode> chartNodes)
    {
        var edges = new List<Edge>();

        // Separate nodes by type
        var xAxisTicks = chartNodes.Where(n => n.Type == "x-axis-tick").OrderBy(n => n.Id).ToList();
        var yAxisTicks = chartNodes.Where(n => n.Type == "y-axis-tick").OrderBy(n => n.Id).ToList();
        var dataPoints = chartNodes.Where(n => n.Type == "data-point").OrderBy(n => n.Id).ToList();

        // 1. Connect successive x-axis ticks
        for (int i = 0; i < xAxisTicks.Count - 1; i++)
        {
            edges.Add(new Edge { source = xAxisTicks[i].Id, target = xAxisTicks[i + 1].Id });
        }

        // 2. Connect successive y-axis ticks
        for (int i = 0; i < yAxisTicks.Count - 1; i++)
        {
            edges.Add(new Edge { source = yAxisTicks[i].Id, target = yAxisTicks[i + 1].Id });
        }

        // 2.5. Connect origin point (x-axis-tick-0 to y-axis-tick-0)
        if (xAxisTicks.Count > 0 && yAxisTicks.Count > 0)
        {
            edges.Add(new Edge { source = xAxisTicks[0].Id, target = yAxisTicks[0].Id });
        }

        // 3. Connect sequential data points (sorted by x-coordinate)
        // If any data points have Series set, group by series and connect within each group
        bool hasSeriesData = dataPoints.Any(n => n.Series != null);
        if (hasSeriesData)
        {
            var seriesGroups = dataPoints.GroupBy(n => n.Series ?? "");
            int seriesEdgeCount = 0;
            foreach (var group in seriesGroups)
            {
                var sorted = group.OrderBy(n => n.Coordinates.Count > 0 ? n.Coordinates[0].x : 0).ToList();
                for (int i = 0; i < sorted.Count - 1; i++)
                {
                    edges.Add(new Edge { source = sorted[i].Id, target = sorted[i + 1].Id });
                    seriesEdgeCount++;
                }
            }
            Debug.Log($"Series-aware edges: {seriesEdgeCount} edges across {seriesGroups.Count()} series");
        }
        else
        {
            var sortedDataPoints = dataPoints.OrderBy(n => n.Coordinates.Count > 0 ? n.Coordinates[0].x : 0).ToList();
            for (int i = 0; i < sortedDataPoints.Count - 1; i++)
            {
                edges.Add(new Edge { source = sortedDataPoints[i].Id, target = sortedDataPoints[i + 1].Id });
            }
        }

        // 4. Connect each data point to its nearest x-axis tick
        foreach (var dataPoint in dataPoints)
        {
            if (dataPoint.Coordinates.Count == 0) continue;

            int dataX = dataPoint.Coordinates[0].x;
            var nearestXTick = xAxisTicks.OrderBy(tick =>
                Math.Abs(tick.Coordinates.Count > 0 ? tick.Coordinates[0].x - dataX : int.MaxValue)
            ).FirstOrDefault();

            if (nearestXTick != null)
            {
                edges.Add(new Edge { source = dataPoint.Id, target = nearestXTick.Id });
            }
        }

        // 5. Connect each data point to its nearest y-axis tick
        foreach (var dataPoint in dataPoints)
        {
            if (dataPoint.Coordinates.Count == 0) continue;

            int dataY = dataPoint.Coordinates[0].y;
            var nearestYTick = yAxisTicks.OrderBy(tick =>
                Math.Abs(tick.Coordinates.Count > 0 ? tick.Coordinates[0].y - dataY : int.MaxValue)
            ).FirstOrDefault();

            if (nearestYTick != null)
            {
                edges.Add(new Edge { source = dataPoint.Id, target = nearestYTick.Id });
            }
        }

        Debug.Log($"Generated {edges.Count} edges: {xAxisTicks.Count - 1} X-axis, {yAxisTicks.Count - 1} Y-axis, {dataPoints.Count * 2} data-to-axis");

        return edges;
    }

    /// <summary>
    /// Initialize the pin lookup dictionary from the DotPad GameObject.
    /// </summary>
    private bool InitializePinLookup()
    {
        var graphitiGO = GameObject.FindGameObjectWithTag("DotPad");
        if (graphitiGO == null)
        {
            Debug.LogError(" No GameObject with tag 'DotPad' found.");
            return false;
        }

        int pinsLayer = LayerMask.NameToLayer("Pins");
        _pinLookup = graphitiGO
            .GetComponentsInChildren<Transform>(true)
            .Where(t => t.gameObject.layer == pinsLayer)
            .ToDictionary(t => t.name.Trim(), t => t);

        return true;
    }

    /// <summary>
    /// Clear existing nodes and edges from the visualization.
    /// </summary>
    private void ClearVisualization()
    {
        int childCount = transform.childCount;
        Debug.Log($"Clearing {childCount} children from GraphVisualizer");

        // Destroy in reverse order to avoid index shifting issues
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Transform child = transform.GetChild(i);
            Destroy(child.gameObject);
        }

        nodes.Clear();
        _labelCountPerPin.Clear();
        _coordIndex.Clear();
        Debug.Log($"Cleared visualization. Remaining children: {transform.childCount}");
    }

    /// <summary>
    /// Calculate the bounding box of all graph nodes.
    /// </summary>
    private (float minX, float minY, float maxX, float maxY, float width, float height) CalculateGraphBounds(List<Node> graphNodes)
    {
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        foreach (var node in graphNodes)
        {
            if (node.coordinates != null && node.coordinates.Length > 0)
            {
                float nodeX = node.coordinates[0][0];
                float nodeY = node.coordinates[0][1];

                minX = Mathf.Min(minX, nodeX);
                minY = Mathf.Min(minY, nodeY);
                maxX = Mathf.Max(maxX, nodeX);
                maxY = Mathf.Max(maxY, nodeY);
            }
        }

        float width = (maxX - minX) > 0 ? (maxX - minX) : 1;
        float height = (maxY - minY) > 0 ? (maxY - minY) : 1;

        Debug.Log($"Graph Bounds - minX: {minX}, maxX: {maxX}, minY: {minY}, maxY: {maxY}");

        return (minX, minY, maxX, maxY, width, height);
    }

    /// <summary>
    /// Calculate the offset position for the graph based on the DotPad object.
    /// </summary>
    private Vector3 CalculateGraphOffset()
    {
        GameObject targetObject = GameObject.FindGameObjectWithTag("DotPad");
        return targetObject != null
            ? targetObject.transform.position + new Vector3(0f, GRAPH_OFFSET_Y, GRAPH_OFFSET_Z)
            : Vector3.zero;
    }

    /// <summary>
    /// Create all graph nodes with their labels.
    /// </summary>
    private void CreateNodes(List<Node> graphNodes,
                             (float minX, float minY, float maxX, float maxY, float width, float height) bounds,
                             Vector3 graphOffset,
                             float scaleFactor,
                             string chartType,
                             string dataType)
    {
        foreach (var node in graphNodes)
        {
            Vector3 position;
            bool isVisible = node.visibility;

            if (node.coordinates != null && node.coordinates.Length > 0)
            {
                float nodeX = node.coordinates[0][0];
                float nodeY = node.coordinates[0][1];

                // Normalize coordinates
                float normalizedX = (nodeX - bounds.minX) / bounds.width * NORMALIZATION_RANGE - NORMALIZATION_CENTER;
                float normalizedY = (nodeY - bounds.minY) / bounds.height * NORMALIZATION_RANGE - NORMALIZATION_CENTER;

                // Flip Y-axis to correct orientation (grid Y=0 at top, Unity Y increases upward)
                normalizedY = -normalizedY;

                position = (new Vector3(normalizedX, normalizedY, 0) * scaleFactor) + graphOffset;
            }
            else
            {
                // Hidden nodes (no coordinates) - position far off-screen
                position = new Vector3(1000f, 1000f, 1000f);
            }

            // Create node GameObject (for both visible and hidden nodes)
            GameObject nodeObj = CreateNodeObject(node, position, scaleFactor);

            // Initialize node component
            NodeComponent nodeComponent = InitializeNodeComponent(nodeObj, node, chartType, dataType);

            // Only create labels for visible nodes
            if (isVisible && node.coordinates != null && node.coordinates.Length > 0)
            {
                CreateLabelForNode(node, nodeComponent);
            }

            // Set visibility based on node.visibility field
            nodeObj.SetActive(isVisible);

            nodes[node.id] = nodeObj;

            if (isVisible)
                Debug.Log($"Node {node.id} - Visible at coordinates: {node.coordinates?[0][0]}, {node.coordinates?[0][1]}");
        }
    }

    /// <summary>
    /// Create a single node GameObject.
    /// </summary>
    private GameObject CreateNodeObject(Node node, Vector3 position, float scaleFactor)
    {
        GameObject nodeObj = Instantiate(nodePrefab, position, Quaternion.identity, transform);
        nodeObj.transform.localScale = Vector3.one * nodeScale * scaleFactor;
        nodeObj.name = $"Node {node.id}";

        nodeObj.SetActive(true);
        var renderer = nodeObj.GetComponent<Renderer>();
        if (renderer != null)
            renderer.enabled = true;

        return nodeObj;
    }

    /// <summary>
    /// Initialize the NodeComponent for a node GameObject.
    /// </summary>
    private NodeComponent InitializeNodeComponent(GameObject nodeObj, Node node, string chartType, string dataType)
    {
        NodeComponent nodeComponent = nodeObj.AddComponent<NodeComponent>();
        Dictionary<string, object> nodeValues = node.values ?? new Dictionary<string, object>();
        nodeComponent.Initialize(node.id, node.type, node.visibility, node.xy, node.xy, nodeValues);
        nodeComponent.series = node.series;
        nodeComponent.symbol = node.symbol;

        // For bar charts, use all coordinates from the node (C# renderer provides them)
        if (chartType == "bar" && node.coordinates != null && node.coordinates.Length > 1)
        {
            nodeComponent.barCoordinates = node.coordinates
                .Select(coord => new NodeComponent.Coordinate((int)coord[0], (int)coord[1]))
                .ToList();
        }
        else
        {
            nodeComponent.PopulateBarCoordinates(node.xy, chartType, dataType);
        }

        return nodeComponent;
    }

    /// <summary>
    /// Create a label for a node and attach it to the corresponding pin.
    /// </summary>
    private void CreateLabelForNode(Node node, NodeComponent nodeComponent)
    {
        if (node.xy == null || node.xy.Length < 2)
        {
            Debug.LogWarning($"Skipping label for {node.id}: no valid xy coordinates");
            return;
        }

        string key = $"{node.xy[0]},{node.xy[1]}";

        string nodeType = node.type ?? "unknown";
        if (nodeType.Contains("axis-tick"))
        {
            Debug.Log($"AXIS TICK: {node.id} (type={nodeType}) requesting pin key '{key}' (x={node.xy[0]}, y={node.xy[1]})");
        }

        if (_pinLookup.TryGetValue(key, out Transform pinTf))
        {
            if (!_labelCountPerPin.ContainsKey(key))
                _labelCountPerPin[key] = 0;

            int labelIndex = _labelCountPerPin[key];
            _labelCountPerPin[key]++;

            float verticalOffset = labelHeight + (labelIndex * labelVerticalSpacing);
            Vector3 worldLabelPos = pinTf.position + Vector3.up * verticalOffset;

            var lbl = Instantiate(labelPrefab, worldLabelPos, Quaternion.identity, transform);
            lbl.name = $"Label {node.id}";

            if (Camera.main != null)
                lbl.transform.rotation = Quaternion.LookRotation(Camera.main.transform.forward);

            string text = RTDDataFormatter.FormatNodeValues(node.values);

            if (nodeType.Contains("axis-tick"))
            {
                Debug.Log($"AXIS TICK PLACED: {node.id} text='{text}' at Unity pin '{key}' → world pos {worldLabelPos} (pin name: {pinTf.name})");
            }
            else
            {
                Debug.Log($"Creating label for node {node.id} at pin {key} (stack index {labelIndex}): '{text}' (values: {string.Join(", ", node.values.Select(kv => $"{kv.Key}={kv.Value}"))})");
            }

            if (lbl.TryGetComponent<TMP_Text>(out var tmp))
            {
                tmp.text = text;
                tmp.alignment = TextAlignmentOptions.Center;
            }
            else if (lbl.TryGetComponent<TextMesh>(out var tm))
            {
                tm.text = text;
                tm.anchor = TextAnchor.LowerCenter;
            }

            lbl.AddComponent<Billboard>();
        }
        else
        {
            Debug.LogWarning($"Pin '{key}' not found under DotPad—skipping label for node {node.id} (type={nodeType})");
        }
    }

    /// <summary>
    /// Create all graph edges.
    /// </summary>
    private void CreateEdges(List<Edge> links, float scaleFactor)
    {
        foreach (var edge in links)
        {
            if (nodes.TryGetValue(edge.source, out var sourceObj) &&
                nodes.TryGetValue(edge.target, out var targetObj))
            {
                GameObject edgeObj = new GameObject("Edge");
                edgeObj.transform.SetParent(transform);
                LineRenderer lineRenderer = edgeObj.AddComponent<LineRenderer>();
                lineRenderer.startWidth = edgeWidth * scaleFactor;
                lineRenderer.endWidth = edgeWidth * scaleFactor;
                lineRenderer.positionCount = 2;
                lineRenderer.SetPosition(0, sourceObj.transform.position);
                lineRenderer.SetPosition(1, targetObj.transform.position);
            }
        }
    }

    public List<NodeComponent> GetMatchingNodes(HashSet<Vector2Int> coords)
    {
        if (coords == null || coords.Count == 0)
            return new List<NodeComponent>();

        var result = new HashSet<NodeComponent>();
        foreach (var coord in coords)
        {
            if (_coordIndex.TryGetValue(coord, out var nodeList))
                foreach (var nc in nodeList)
                    result.Add(nc);
        }
        return result.ToList();
    }

    public List<NodeComponent> GetMatchingNodesBasedOnValue(List<float> searchValues, bool requireAll = true)
    {
        List<NodeComponent> matchingNodes = new List<NodeComponent>();

        foreach (GameObject nodeObj in nodes.Values)
        {
            NodeComponent node = nodeObj.GetComponent<NodeComponent>();
            if (node == null || node.serializedValues == null) continue;

            bool matched;
            if (requireAll)
            {
                matched = true;
                foreach (float sv in searchValues)
                {
                    bool found = node.serializedValues.Any(val =>
                    {
                        if (float.TryParse(val.value, out float numericValue))
                            return Mathf.Approximately(numericValue, sv);
                        return false;
                    });
                    if (!found) { matched = false; break; }
                }
            }
            else
            {
                matched = false;
                foreach (float sv in searchValues)
                {
                    if (node.serializedValues.Any(val =>
                    {
                        if (float.TryParse(val.value, out float numericValue))
                            return Mathf.Approximately(numericValue, sv);
                        return false;
                    }))
                    {
                        matched = true;
                        break;
                    }
                }
            }

            if (matched)
                matchingNodes.Add(node);
        }

        return matchingNodes;
    }

    public NodeComponent GetMatchingNodeByXY(string xValue, float yValue)
    {
        foreach (GameObject nodeObj in nodes.Values)
        {
            NodeComponent node = nodeObj.GetComponent<NodeComponent>();
            if (node == null || node.values == null) continue;

            if (!node.type.StartsWith("data")) continue;

            bool xMatch = false;
            bool yMatch = false;

            foreach (var kvp in node.values)
            {
                string valStr = kvp.Value?.ToString() ?? "";

                if (valStr == xValue)
                    xMatch = true;

                if (float.TryParse(valStr, out float numVal))
                {
                    if (Mathf.Approximately(numVal, yValue))
                        yMatch = true;
                }
            }

            if (xMatch && yMatch)
                return node;
        }

        return null;
    }

    public Dictionary<string, GameObject> GetNodes()
    {
        return nodes;
    }

    /// <summary>
    /// Get all axis coordinates (x-axis and y-axis nodes) for rendering purposes.
    /// </summary>
    public HashSet<Vector2Int> GetAxisCoordinates()
    {
        var axisCoords = new HashSet<Vector2Int>();

        foreach (var nodeObj in nodes.Values)
        {
            var node = nodeObj.GetComponent<NodeComponent>();
            if (node == null || node.xy == null || node.xy.Length != 2)
                continue;

            if (node.type.Contains("x-axis") || node.type.Contains("y-axis"))
            {
                axisCoords.Add(new Vector2Int(node.xy[0], node.xy[1]));
            }
        }

        return axisCoords;
    }

    // ===== UpdateVisibleNodes and helpers =====

    /// <summary>
    /// Update visibility of GameObjects based on windowed data range.
    /// Hides data points and their labels that are outside the visible window.
    /// Also filters axis tick labels to match the visible range.
    /// </summary>
    public void UpdateVisibleNodes(List<Dictionary<string, object>> visibleData, string xField, string yField,
        float? axisDomainYMin = null, float? axisDomainYMax = null, List<object> xViewportValues = null)
    {
        if (visibleData == null || visibleData.Count == 0)
        {
            Debug.LogWarning(" No visible data provided to UpdateVisibleNodes");
            return;
        }

        var visiblePoints = BuildVisiblePointKeys(visibleData, xField, yField);
        Debug.Log($"Sample visible keys: {string.Join(", ", visiblePoints.Take(5))}");

        var xValues = visibleData.Select(d => d.ContainsKey(xField) ? d[xField] : null).Where(v => v != null).ToList();
        var (yMin, yMax) = ResolveYRange(visibleData, yField, axisDomainYMin, axisDomainYMax);

        float? xMin = null, xMax = null;
        var numericXValues = xValues.Select(v => TryGetNumericValue(v)).Where(v => v.HasValue).Select(v => v.Value).ToList();
        if (numericXValues.Any())
        {
            xMin = numericXValues.Min();
            xMax = numericXValues.Max();
        }

        Debug.Log($"UpdateVisibleNodes: {visiblePoints.Count} visible data points, Y range: {yMin}-{yMax}, X range: {xMin}-{xMax}");

        int hiddenCount = 0, shownCount = 0, axisHiddenCount = 0;

        foreach (var kvp in nodes)
        {
            var nodeObj = kvp.Value;
            var nc = nodeObj.GetComponent<NodeComponent>();
            if (nc == null) continue;

            if (nc.type.Contains("axis-tick"))
            {
                bool visible = ApplyAxisTickVisibility(kvp.Key, nodeObj, nc, xField, yField,
                    xMin, xMax, yMin, yMax, xValues, xViewportValues, axisDomainYMin, axisDomainYMax,
                    shownCount, hiddenCount);
                if (!visible) axisHiddenCount++;
                continue;
            }

            // Always show other axis elements (axis lines, zero markers)
            if (nc.type.Contains("axis") || nc.type.Contains("zero"))
            {
                nodeObj.SetActive(true);
                continue;
            }

            if (nc.type == "data-point" || nc.type == "data-marker" || nc.type.StartsWith("data-mark"))
            {
                ApplyDataPointVisibility(kvp.Key, nodeObj, nc, visiblePoints, xField, yField,
                    ref shownCount, ref hiddenCount);
            }
        }

        Debug.Log($"UpdateVisibleNodes: data shown={shownCount}, data hidden={hiddenCount}, axis ticks hidden={axisHiddenCount}");
    }

    /// <summary>
    /// Build a set of "xVal_yVal" key strings for all visible data points.
    /// </summary>
    private HashSet<string> BuildVisiblePointKeys(List<Dictionary<string, object>> visibleData, string xField, string yField)
    {
        var keys = new HashSet<string>();
        foreach (var point in visibleData)
        {
            if (point.ContainsKey(xField) && point.ContainsKey(yField))
                keys.Add($"{point[xField]}_{point[yField]}");
        }
        return keys;
    }

    /// <summary>
    /// Resolve the Y display range from the axis domain override or from visible data.
    /// </summary>
    private (float? yMin, float? yMax) ResolveYRange(List<Dictionary<string, object>> visibleData, string yField,
        float? axisDomainYMin, float? axisDomainYMax)
    {
        if (axisDomainYMin.HasValue && axisDomainYMax.HasValue)
        {
            Debug.Log($"Using renderer's Y-axis domain for tick filtering: [{axisDomainYMin}, {axisDomainYMax}]");
            return (axisDomainYMin, axisDomainYMax);
        }

        var yValues = visibleData.Select(d => d.ContainsKey(yField) ? d[yField] : null).Where(v => v != null).ToList();
        var numericY = yValues.Select(v => TryGetNumericValue(v)).Where(v => v.HasValue).Select(v => v.Value).ToList();
        float? yMin = numericY.Any() ? numericY.Min() : (float?)null;
        float? yMax = numericY.Any() ? numericY.Max() : (float?)null;
        Debug.Log($"Calculated Y-range from visible data: [{yMin}, {yMax}]");
        return (yMin, yMax);
    }

    /// <summary>
    /// Set visibility of a node GameObject and its corresponding label together.
    /// </summary>
    private void SetNodeAndLabelVisibility(string nodeId, GameObject nodeObj, bool visible)
    {
        nodeObj.SetActive(visible);
        Transform labelTf = transform.Find($"Label {nodeId}");
        if (labelTf != null)
            labelTf.gameObject.SetActive(visible);
    }

    /// <summary>
    /// Apply visibility to an axis-tick node based on whether its value is within the current viewport.
    /// Returns the computed visibility for the caller to track counts.
    /// </summary>
    private bool ApplyAxisTickVisibility(string nodeId, GameObject nodeObj, NodeComponent nc,
        string xField, string yField,
        float? xMin, float? xMax, float? yMin, float? yMax,
        List<object> xValues, List<object> xViewportValues,
        float? axisDomainYMin, float? axisDomainYMax,
        int shownCount, int hiddenCount)
    {
        bool isVisible = true;

        // Y-axis ticks: check if tick value falls within visible Y range
        if (nc.type.Contains("y-axis") && yMin.HasValue && yMax.HasValue &&
            nc.values != null && nc.values.ContainsKey(yField))
        {
            var tickValue = TryGetNumericValue(nc.values[yField]);
            if (tickValue.HasValue)
            {
                if (axisDomainYMin.HasValue && axisDomainYMax.HasValue)
                {
                    isVisible = tickValue.Value >= yMin.Value && tickValue.Value <= yMax.Value;
                    Debug.Log($"Y-tick {tickValue.Value}: visible={isVisible} (domain [{yMin.Value}, {yMax.Value}])");
                }
                else
                {
                    float range = yMax.Value - yMin.Value;
                    float paddedMin = yMin.Value - range * 0.1f;
                    float paddedMax = yMax.Value + range * 0.1f;
                    isVisible = tickValue.Value >= paddedMin && tickValue.Value <= paddedMax;
                }
            }
        }

        // X-axis ticks: check if tick category is among the visible X values
        if (nc.type.Contains("x-axis") && nc.values != null && nc.values.ContainsKey(xField))
        {
            string tickCategory = nc.values[xField].ToString();

            List<string> visibleXCategories = (xViewportValues != null && xViewportValues.Count > 0)
                ? xViewportValues.Select(v => v.ToString()).ToList()
                : xValues.Select(v => v.ToString()).ToList();

            isVisible = visibleXCategories.Contains(tickCategory);

            if (shownCount + hiddenCount < 3)
                Debug.Log($"X-axis tick '{tickCategory}': visible={isVisible} (in {visibleXCategories.Count} visible categories)");
        }

        SetNodeAndLabelVisibility(nodeId, nodeObj, isVisible);
        return isVisible;
    }

    /// <summary>
    /// Apply visibility to a data-point node based on whether its values are in the visible set.
    /// </summary>
    private void ApplyDataPointVisibility(string nodeId, GameObject nodeObj, NodeComponent nc,
        HashSet<string> visiblePoints, string xField, string yField,
        ref int shownCount, ref int hiddenCount)
    {
        bool isVisible = false;

        if (nc.values != null)
        {
            if (shownCount + hiddenCount < 3)
                Debug.Log($"Node {nc.type} has fields: [{string.Join(", ", nc.values.Keys)}]");

            if (nc.values.TryGetValue(xField, out var xVal) && nc.values.TryGetValue(yField, out var yVal))
            {
                string key = $"{xVal}_{yVal}";
                isVisible = visiblePoints.Contains(key);

                if (shownCount + hiddenCount < 5)
                    Debug.Log($"Checking {nc.type}: key='{key}', visible={isVisible}");
            }
            else if (shownCount + hiddenCount < 3)
            {
                Debug.Log($"Node missing xField='{xField}' or yField='{yField}'");
            }
        }

        SetNodeAndLabelVisibility(nodeId, nodeObj, isVisible);
        if (isVisible) shownCount++; else hiddenCount++;
    }

    private float? TryGetNumericValue(object value)
    {
        if (value == null) return null;
        if (value is float f) return f;
        if (value is double d) return (float)d;
        if (value is int i) return (float)i;
        if (value is long l) return (float)l;
        if (value is Newtonsoft.Json.Linq.JValue jval)
        {
            try { return jval.ToObject<float>(); } catch { return null; }
        }
        if (value is string s && float.TryParse(s, out float result)) return result;
        return null;
    }

    /// <summary>
    /// Show all nodes and labels.
    /// </summary>
    public void ShowAllNodes()
    {
        foreach (var kvp in nodes)
        {
            SetNodeAndLabelVisibility(kvp.Key, kvp.Value, true);
        }
        Debug.Log(" ShowAllNodes: all nodes and labels visible");
    }

    /// <summary>
    /// Update viewport overlay to show current data windowing state.
    /// Draws a rectangle showing the visible portion of the full dataset.
    /// </summary>
    public void UpdateViewportOverlay(int windowStart, int windowSize, int totalDataPoints,
        float windowYMin, float windowYMax, float dataYMin, float dataYMax)
    {
        if (_viewportOverlay != null)
            Destroy(_viewportOverlay);

        _viewportOverlay = new GameObject("ViewportOverlay");
        _viewportOverlay.transform.SetParent(transform);

        _viewportLineRenderer = _viewportOverlay.AddComponent<LineRenderer>();
        _viewportLineRenderer.material = new Material(Shader.Find("Sprites/Default"));
        _viewportLineRenderer.startColor = Color.cyan;
        _viewportLineRenderer.endColor = Color.cyan;
        _viewportLineRenderer.startWidth = 0.002f;
        _viewportLineRenderer.endWidth = 0.002f;
        _viewportLineRenderer.positionCount = 5;
        _viewportLineRenderer.useWorldSpace = false;
        _viewportLineRenderer.loop = true;

        float xMin = 0f, xMax = NORMALIZATION_RANGE;
        float yMin = 0f, yMax = NORMALIZATION_RANGE;

        if (totalDataPoints > 0)
        {
            float xStartNorm = (float)windowStart / totalDataPoints;
            float xEndNorm = (float)(windowStart + windowSize) / totalDataPoints;
            xMin = xStartNorm * NORMALIZATION_RANGE - NORMALIZATION_CENTER;
            xMax = xEndNorm * NORMALIZATION_RANGE - NORMALIZATION_CENTER;
        }

        float yRange = dataYMax - dataYMin;
        if (yRange > 0)
        {
            float yMinNorm = (windowYMin - dataYMin) / yRange;
            float yMaxNorm = (windowYMax - dataYMin) / yRange;
            yMin = yMinNorm * NORMALIZATION_RANGE - NORMALIZATION_CENTER;
            yMax = yMaxNorm * NORMALIZATION_RANGE - NORMALIZATION_CENTER;
        }

        Vector3 offset = CalculateGraphOffset();
        float scale = SCALE_FACTOR;

        Vector3[] corners = new Vector3[5];
        corners[0] = new Vector3(xMin, yMin, 0.001f) * scale + offset;
        corners[1] = new Vector3(xMax, yMin, 0.001f) * scale + offset;
        corners[2] = new Vector3(xMax, yMax, 0.001f) * scale + offset;
        corners[3] = new Vector3(xMin, yMax, 0.001f) * scale + offset;
        corners[4] = corners[0];

        _viewportLineRenderer.SetPositions(corners);

        Debug.Log($"Viewport overlay: X=[{windowStart}, {windowStart + windowSize}]/{totalDataPoints}, Y=[{windowYMin:F2}, {windowYMax:F2}]/[{dataYMin:F2}, {dataYMax:F2}]");
    }

    [System.Serializable]
    public class Node
    {
        [SerializeField] public string id;
        [SerializeField] public string type;
        [SerializeField] public bool visibility;
        [SerializeField] public string series;
        [SerializeField] public string symbol;
        [SerializeField] public Dictionary<string, object> values;
        [SerializeField] public float[][] coordinates;
        [SerializeField] public int[] xy;
    }

    [System.Serializable]
    public class Edge
    {
        public string source;
        public string target;
    }

    [System.Serializable]
    public class GraphData
    {
        public List<Node> nodes;
        public List<Edge> links;
    }
}
