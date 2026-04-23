using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;
using System.IO;
using System;
using System.Linq;

/// <summary>
/// Loads and manages Vega-Lite charts using the C# renderer (VegaToRTDRenderer).
/// Handles chart discovery, loading, and viewport management.
/// Loads Vega-Lite chart specifications, renders them to RTD grid format, and manages display.
/// </summary>
public class VegaChartLoader : MonoBehaviour
{
    // interface classes
    [SerializeField] private MonoBehaviour mqttManagerService;
    private InterfaceMQTTManager _mqttManager;
    [SerializeField] private MonoBehaviour rtdUpdaterService;
    private InterfaceRTDUpdater _rtdUpdater;
    [SerializeField] private MonoBehaviour graphVisualizerService;
    private InterfaceGraphVisualizer _graphVisualizer;
    [SerializeField] private MonoBehaviour previewGeneratorService;
    private RTDPreviewGenerator _previewGenerator;
    [SerializeField] private ButtonGUI buttonGUI;

    // Multi-series connecting lines
    [Header("Multi-Series Options")]
    [SerializeField] private bool drawConnectingLines = true;
    [SerializeField] private bool useSeriesSymbols = true;
    [SerializeField] private bool useThickBars = false;
    [SerializeField] private bool useSeriesLinePatterns = false;
    [SerializeField] private bool useSeriesLineThickness = false;
    [SerializeField] private bool useBarTextures = false;
    [SerializeField] [Range(0, 2)] private int symbolClearance = 2;
    [SerializeField] private bool interleavedNavigation = false;
    [SerializeField] private BrailleTranslator.BrailleMode brailleMode = BrailleTranslator.BrailleMode.RawDotBytes;

    [Header("Series Symbol Overrides")]
    [SerializeField] private RTDGridConstants.SymbolType seriesSymbol0 = RTDGridConstants.SymbolType.Diamond;
    [SerializeField] private RTDGridConstants.SymbolType seriesSymbol1 = RTDGridConstants.SymbolType.Default;
    [SerializeField] private RTDGridConstants.SymbolType seriesSymbol2 = RTDGridConstants.SymbolType.Default;
    [SerializeField] private RTDGridConstants.SymbolType seriesSymbol3 = RTDGridConstants.SymbolType.Default;

    [Header("Highlight Config")]
    [SerializeField] private HighlightConfig gestureConfig = new HighlightConfig { Shape = HighlightMarkShape.Mark, Anim = HighlightAnim.Animated, Duration = -1f };
    [SerializeField] private HighlightConfig agentConfig   = new HighlightConfig { Shape = HighlightMarkShape.Box,  Anim = HighlightAnim.Static,   Duration = -1f };
    [SerializeField] private HighlightConfig navConfig     = new HighlightConfig { Shape = HighlightMarkShape.Mark, Anim = HighlightAnim.Animated, Duration = -1f };

    [Header("Series Filtering")]
    [SerializeField] private List<string> hiddenSeries = new List<string>();
    [HideInInspector] public List<string> availableSeries = new List<string>();
    private string _hiddenSeriesPublishedKey = "";

    [Header("Range Filter")]
    [Tooltip("Hide data points before this index (-1 = disabled)")]
    [SerializeField] private int rangeFilterStart = -1;
    [Tooltip("Hide data points after this index (-1 = disabled)")]
    [SerializeField] private int rangeFilterEnd = -1;

    [Header("Thick Bar Limits")]
    [SerializeField] private int maxBarsDisplayed = 10;

    // Manual layer selection override
    [Header("Manual Layer Selection")]
    public bool useManualLayerSelection = false;
    public string manualLayerName = "";

    // Available layer names from current chart (populated at runtime)
    [HideInInspector] public List<string> availableLayerNames = new List<string>();

    // ===== Auto-Discovery =====
    private ChartDiscoveryService _chartDiscovery;
    private List<DiscoveredChart> _availableCharts;

    // ===== Current Chart State =====
    private string filePath;
    public string chartType;
    private string dataName;
    private string schema;
    private string imagePath;

    // ===== Current Chart Reference =====
    private DiscoveredChart _currentChart;
    private VegaSpec _currentVegaSpec;
    private string _rawVegaJson; // Raw JSON for clean re-deserialization before layer transforms
    private bool _pendingInspectorRefresh = false;

    // X-axis windowing (index-based)
    private int _windowStart = 0;
    private int _windowSize = 0;  // Current X-window size
    private int _maxViewportPoints = 0;  // Max points for current chart type (25 for line/bar, unlimited for scatter)

    // Y-axis windowing (value-based)
    private float _windowYMin = 0f;      // Current Y-window minimum
    private float _windowYMax = 5f;      // Current Y-window maximum

    // Data range tracking
    private float _dataYMin = 0f;        // Full dataset Y minimum
    private float _dataYMax = 5f;        // Full dataset Y maximum
    private float _fullYMin = 0f;        // Full Y-axis range minimum (from spec or data)
    private float _fullYMax = 5f;        // Full Y-axis range maximum (from spec or data)
    private int _totalDataPoints = 0;

    void Awake()
    {
        _mqttManager = mqttManagerService as InterfaceMQTTManager ?? throw new InvalidOperationException("mqttManagerService not assigned!");
        _rtdUpdater = rtdUpdaterService as InterfaceRTDUpdater ?? throw new InvalidOperationException("rtdUpdaterService not assigned!");
        _graphVisualizer = graphVisualizerService as InterfaceGraphVisualizer ?? throw new InvalidOperationException("graphVisualizerService not assigned!");

        // Preview generator is optional (can be null)
        if (previewGeneratorService != null)
        {
            _previewGenerator = previewGeneratorService as RTDPreviewGenerator;
        }

        // Initialize chart discovery
        _chartDiscovery = new ChartDiscoveryService();
        _availableCharts = _chartDiscovery.DiscoverCharts();
    }

    void Start()
    {
        // Subscribe to MQTT connection event to send chart metadata
        if (_mqttManager != null)
        {
            _mqttManager.MQTTConnected += OnMQTTConnected;
        }
    }

    void OnDestroy()
    {
        // Unsubscribe from events
        if (_mqttManager != null)
        {
            _mqttManager.MQTTConnected -= OnMQTTConnected;
        }
    }

    private void OnValidate()
    {
        if (!Application.isPlaying) return;
        _pendingInspectorRefresh = true;
    }

    void Update()
    {
        if (!_pendingInspectorRefresh) return;
        _pendingInspectorRefresh = false;
        if (_currentVegaSpec == null || _currentChart == null) return;
        BrailleTranslator.Mode = brailleMode;
        _rtdUpdater?.SetInterleavedNavigation(interleavedNavigation);
        _rtdUpdater?.SetHighlightConfigs(gestureConfig, agentConfig, navConfig);
        _rtdUpdater?.RefreshBrailleLabel();
        GenerateAndDisplayRTDGrid(_currentChart);
    }

    private void OnMQTTConnected()
    {
        UnityEngine.Debug.Log(" MQTT connected - publishing chart metadata index...");
        ChartMQTTPublisher.PublishChartMetadataIndex(_mqttManager, _availableCharts);
    }

    /// <summary>
    /// Find chart ID by dataset and field name.
    /// Used for agent commands like "housing-interest rate (%)-line"
    /// </summary>
    public int? FindChartByDatasetAndField(string dataset, string field)
    {
        if (string.IsNullOrEmpty(dataset) || string.IsNullOrEmpty(field))
            return null;

        // Search through available charts
        foreach (var chart in _availableCharts)
        {
            // Match by dataset name and field (case-insensitive)
            if (chart.dataset.Equals(dataset, StringComparison.OrdinalIgnoreCase) &&
                chart.field.Equals(field, StringComparison.OrdinalIgnoreCase))
            {
                UnityEngine.Debug.Log($"Found chart: ID={chart.id}, dataset='{chart.dataset}', field='{chart.field}'");
                return chart.id;
            }
        }

        UnityEngine.Debug.LogWarning($"No chart found for dataset='{dataset}', field='{field}'");
        return null;
    }

    /// <summary>
    /// Find chart ID by data name and chart type.
    /// Used for agent commands like "tslastock-line"
    /// </summary>
    public int? FindChartByDataNameAndType(string dataName, string chartType)
    {
        if (string.IsNullOrEmpty(dataName) || string.IsNullOrEmpty(chartType))
            return null;

        // Search through available charts
        foreach (var chart in _availableCharts)
        {
            // Match by dataName and chartType (case-insensitive)
            if (chart.dataName.Equals(dataName, StringComparison.OrdinalIgnoreCase) &&
                chart.chartType.Equals(chartType, StringComparison.OrdinalIgnoreCase))
            {
                UnityEngine.Debug.Log($"Found chart: ID={chart.id}, dataName='{chart.dataName}', chartType='{chart.chartType}'");
                return chart.id;
            }
        }

        UnityEngine.Debug.LogWarning($"No chart found for dataName='{dataName}', chartType='{chartType}'");
        return null;
    }

    /// <summary>
    /// Select a chart by ID using auto-discovery.
    /// Replaces the old hardcoded switch statement.
    /// </summary>
    public void SelectFile(int option)
    {
        DiscoveredChart chart = _chartDiscovery.GetChartById(option);

        if (chart == null)
        {
            UnityEngine.Debug.LogWarning($"Chart with ID {option} not found. Using first available chart.");
            chart = _availableCharts.FirstOrDefault();
        }

        if (chart == null)
        {
            UnityEngine.Debug.LogError($"No charts available!");
            return;
        }

        // Set chart properties
        filePath = chart.jsonFilePath;
        chartType = chart.chartType;
        dataName = chart.dataName;
        imagePath = chart.pngFilePath;

        UnityEngine.Debug.Log($"Selected chart {option}: {chart.DisplayName}");
        UnityEngine.Debug.Log($"JSON: {filePath}");
        UnityEngine.Debug.Log($"PNG: {imagePath}");
        UnityEngine.Debug.Log($"Type: {chartType}, Data: {dataName}");
    }

    /// <summary>
    /// Get the total number of available charts.
    /// </summary>
    public int GetChartCount()
    {
        return _availableCharts?.Count ?? 0;
    }

    /// <summary>
    /// Get all available charts for display in UI.
    /// </summary>
    public List<DiscoveredChart> GetAvailableCharts()
    {
        return _availableCharts;
    }

    /// <summary>
    /// Rediscover charts (useful after importing new spec files).
    /// </summary>
    public void RediscoverCharts()
    {
        UnityEngine.Debug.Log(" Rediscovering charts...");
        _availableCharts = _chartDiscovery.DiscoverCharts();
        UnityEngine.Debug.Log($"Rediscovered {_availableCharts.Count} charts");
    }

    /// <summary>
    /// Publish full chart details (schema + image) for a specific chart.
    /// Called when agent requests details for a chart by ID, dataset, or data_name.
    /// </summary>
    public void PublishChartDetails(int chartId)
    {
        ChartMQTTPublisher.PublishChartDetails(_mqttManager, _availableCharts, chartId);
    }

    /// <summary>
    /// Generate RTD grid from Vega-Lite JSON and display on device.
    /// Uses current viewport state (window start/size and Y-min/max).
    /// </summary>
    private void GenerateAndDisplayRTDGrid(DiscoveredChart chart)
    {
        try
        {
            // Debug: Track how many times this is called
            UnityEngine.Debug.Log($"GenerateAndDisplayRTDGrid called");
            UnityEngine.Debug.Log($"Viewport: X=[{_windowStart}, {_windowStart + _windowSize - 1}], Y=[{_windowYMin:F2}, {_windowYMax:F2}]");
            if (_currentVegaSpec == null)
            {
                UnityEngine.Debug.LogError(" No Vega spec loaded!");
                return;
            }

            // Get field names
            string xField = _currentVegaSpec.Encoding.X.Field;
            string yField = _currentVegaSpec.Encoding.Y.Field;

            // Get windowed data based on current viewport
            var fullData = _currentVegaSpec.Data.Values;
            string colorFieldForWindowing = _currentVegaSpec.Encoding?.GetColorField();
            List<Dictionary<string, object>> windowedDataBeforeYFilter;

            if (colorFieldForWindowing != null)
            {
                // Multi-series: window by unique X values, not raw row indices
                var uniqueXValues = fullData
                    .Where(d => d.ContainsKey(xField))
                    .Select(d => d[xField].ToString())
                    .Distinct()
                    .ToList();

                int effectiveStart = Math.Min(_windowStart, uniqueXValues.Count);
                int effectiveSize = Math.Min(_windowSize, uniqueXValues.Count - effectiveStart);
                var windowedXSet = new HashSet<string>(uniqueXValues.GetRange(effectiveStart, effectiveSize));

                windowedDataBeforeYFilter = fullData
                    .Where(d => d.ContainsKey(xField) && windowedXSet.Contains(d[xField].ToString()))
                    .ToList();
            }
            else
            {
                // Single-series: window by raw row indices
                windowedDataBeforeYFilter = fullData.GetRange(_windowStart, Math.Min(_windowSize, fullData.Count - _windowStart));
            }

            // Filter by Y-window
            var windowedData = windowedDataBeforeYFilter.Where(d =>
            {
                if (d.ContainsKey(yField))
                {
                    float yValue = Convert.ToSingle(d[yField]);
                    return yValue >= _windowYMin && yValue <= _windowYMax;
                }
                return false;
            }).ToList();

            UnityEngine.Debug.Log($"Viewport: X=[{_windowStart}, {_windowStart + _windowSize - 1}], Y=[{_windowYMin:F2}, {_windowYMax:F2}] → {windowedData.Count} visible points (after Y-filter)");

            // Generate RTD grid using C# renderer with viewport parameters
            var hiddenSet = hiddenSeries.Count > 0 ? new HashSet<string>(hiddenSeries) : null;
            var opts = new VegaToRTDRenderer.RenderOptions
            {
                DrawConnectingLines = drawConnectingLines,
                UseSeriesSymbols = useSeriesSymbols,
                UseThickBars = useThickBars,
                HiddenSeries = hiddenSet,
                UseSeriesLinePatterns = useSeriesLinePatterns,
                UseSeriesLineThickness = useSeriesLineThickness,
                UseBarTextures = useBarTextures,
                SymbolClearance = symbolClearance,
                SeriesSymbolOverrides = new[] { seriesSymbol0, seriesSymbol1, seriesSymbol2, seriesSymbol3 },
                RangeFilterStart = rangeFilterStart,
                RangeFilterEnd = rangeFilterEnd
            };
            var (grid, nodes) = VegaToRTDRenderer.Generate(_currentVegaSpec, _windowStart, _windowSize, _windowYMin, _windowYMax, opts);

            UnityEngine.Debug.Log($"Generated {grid.GetLength(0)}x{grid.GetLength(1)} RTD grid with {nodes.Count} nodes");

            // Generate graph visualization from nodes
            _graphVisualizer.GenerateGraph(nodes, chartType, dataName);

            // Set up visibility filtering
            bool isFullyZoomedOut = (_windowSize >= _maxViewportPoints) && _windowStart == 0;

            if (isFullyZoomedOut)
            {
                UnityEngine.Debug.Log(" Fully zoomed out - showing all nodes");
                _graphVisualizer.ShowAllNodes();
            }
            else
            {
                // Extract X-values from pre-Y-filter data for X-axis tick visibility
                var xViewportValues = windowedDataBeforeYFilter
                    .Where(d => d.ContainsKey(xField))
                    .Select(d => d[xField])
                    .Distinct()
                    .ToList();

                UnityEngine.Debug.Log($"Applying visibility filter: {windowedData.Count} visible points, Y-domain [{_windowYMin}, {_windowYMax}], X-viewport values: {xViewportValues.Count}");
                _graphVisualizer.UpdateVisibleNodes(windowedData, xField, yField, _windowYMin, _windowYMax, xViewportValues);
            }

            // Update viewport overlay to show current zoom/pan state
            _graphVisualizer.UpdateViewportOverlay(_windowStart, _windowSize, _totalDataPoints, _windowYMin, _windowYMax, _dataYMin, _dataYMax);

            // Count non-zero pixels for debug
            int nonZero = 0;
            for (int r = 0; r < grid.GetLength(0); r++)
                for (int c = 0; c < grid.GetLength(1); c++)
                    if (grid[r, c] != 0) nonZero++;

            UnityEngine.Debug.Log($"Grid contains {nonZero} non-background pixels");

            // Clear any active highlights from the previous chart before displaying the new one.
            _rtdUpdater.RefreshScreen();

            // Display on RTD device
            _rtdUpdater.DisplayImage(grid);

            // Display in Unity visualization
            _rtdUpdater.DisplayImageInUnityFromBase();

            // Set chart type and highlight configs for highlight manager
            _rtdUpdater.SetChartType(chartType);
            _rtdUpdater.SetInterleavedNavigation(interleavedNavigation);
            _rtdUpdater.SetUseSeriesSymbols(useSeriesSymbols);
            _rtdUpdater.SetSeriesSymbolOverrides(new[] { seriesSymbol0, seriesSymbol1, seriesSymbol2, seriesSymbol3 });
            _rtdUpdater.SetHighlightConfigs(gestureConfig, agentConfig, navConfig);

            // Set chart title for braille display and refresh
            string chartTitle = chart.DisplayName ?? $"{chart.chartType} - {chart.dataName}";
            _rtdUpdater.SetChartTitle(chartTitle);
            _rtdUpdater.DisplayBrailleLabel(chartTitle);

            UnityEngine.Debug.Log($"DisplayImage() and DisplayImageInUnityFromBase() called successfully");
            _rtdUpdater.EnableDataPointNavigation(true, false);

            // Publish layer data to agent
            string currentHiddenKey = string.Join(",", hiddenSeries);
            if (_currentVegaSpec.Layer != null && _currentVegaSpec.Layer.Count > 0)
            {
                var currentLayer = _currentVegaSpec.Layer
                    .FirstOrDefault(l => l.Data?.Values != null && l.Data.Values.Count == _totalDataPoints);

                if (currentLayer != null)
                {
                    ChartMQTTPublisher.PublishCurrentLayerData(_mqttManager, _currentVegaSpec, currentLayer.Name, _windowStart, _windowSize, _windowYMin, _windowYMax, hiddenSeries.Count > 0 ? new HashSet<string>(hiddenSeries) : null);
                }
            }
            else
            {
                string publishMarkType = _currentVegaSpec.GetMarkType();
                ChartMQTTPublisher.PublishCurrentLayerData(_mqttManager, _currentVegaSpec, publishMarkType, _windowStart, _windowSize, _windowYMin, _windowYMax, hiddenSeries.Count > 0 ? new HashSet<string>(hiddenSeries) : null);
            }
            _hiddenSeriesPublishedKey = currentHiddenKey;
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError($"Failed to generate RTD grid: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // ===== Chart Loading =====

    /// <summary>
    /// Load and display a chart from a Vega-Lite JSON specification.
    /// </summary>
    public void LoadChart(int option)
    {
        UnityEngine.Debug.Log($"LoadChart called for option {option}");

        SelectFile(option);

        // Get the selected chart
        DiscoveredChart chart = _chartDiscovery.GetChartById(option);
        if (chart == null)
        {
            UnityEngine.Debug.LogError($"Chart {option} not found!");
            return;
        }

        // Load Vega spec to get data range
        string jsonFullPath = chart.GetFullJsonPath();
        if (!File.Exists(jsonFullPath))
        {
            UnityEngine.Debug.LogError($"JSON file not found: {jsonFullPath}");
            return;
        }

        string vegaJson = File.ReadAllText(jsonFullPath);
        _rawVegaJson = vegaJson;
        _currentVegaSpec = JsonConvert.DeserializeObject<VegaSpec>(vegaJson);

        if (_currentVegaSpec.Layer != null)
        {
            UnityEngine.Debug.Log($"Deserialized {_currentVegaSpec.Layer.Count} layers from JSON");
            availableLayerNames.Clear();
            for (int i = 0; i < _currentVegaSpec.Layer.Count; i++)
            {
                var layer = _currentVegaSpec.Layer[i];
                UnityEngine.Debug.Log($"Layer {i}: name='{layer.Name}', hasTransform={layer.Transform != null}, transformCount={layer.Transform?.Count ?? 0}");
                if (!string.IsNullOrEmpty(layer.Name))
                {
                    availableLayerNames.Add(layer.Name);
                }
            }
            if (availableLayerNames.Count > 0)
            {
                UnityEngine.Debug.Log($"Available layers for manual selection: {string.Join(", ", availableLayerNames)}");

                // Set state needed before applying layer
                _maxViewportPoints = 25;
                _currentChart = chart;

                // Apply the first layer so top-level encoding/mark/data are populated
                string initialLayer = (!string.IsNullOrEmpty(manualLayerName) && availableLayerNames.Contains(manualLayerName))
                    ? manualLayerName
                    : availableLayerNames[0];
                ApplyNamedLayer(initialLayer);
                return; // ApplyNamedLayer calls GenerateAndDisplayRTDGrid, so we're done
            }
        }
        else
        {
            availableLayerNames.Clear();
        }

        // Apply transforms to data if specified in spec (for single-layer charts)
        if (_currentVegaSpec.Transform != null && _currentVegaSpec.Transform.Count > 0 &&
            _currentVegaSpec.Data?.Values != null &&
            (_currentVegaSpec.Layer == null || _currentVegaSpec.Layer.Count == 0))
        {
            var transformEngine = new VegaTransformEngine();
            string xField = _currentVegaSpec.Encoding?.X?.Field;
            UnityEngine.Debug.Log($"Applying {_currentVegaSpec.Transform.Count} transforms to data (X-field: {xField ?? "none"})");

            var transformedData = transformEngine.ApplyTransforms(
                _currentVegaSpec.Data.Values,
                _currentVegaSpec.Transform,
                xField
            );

            _currentVegaSpec.Data.Values = transformedData;
            UnityEngine.Debug.Log($"Transformed data: {transformedData.Count} rows");
        }

        _currentChart = chart;

        // Check for multi-series (color field grouping)
        string colorField = _currentVegaSpec.Encoding?.GetColorField();
        if (colorField != null)
        {
            // Multi-series: total data points = unique X values (not raw rows)
            string xFieldForCount = _currentVegaSpec.Encoding.X.Field;
            _totalDataPoints = _currentVegaSpec.Data.Values
                .Where(d => d.ContainsKey(xFieldForCount))
                .Select(d => d[xFieldForCount].ToString())
                .Distinct()
                .Count();
            UnityEngine.Debug.Log($"Multi-series detected (color field: '{colorField}'): {_totalDataPoints} unique X values (raw rows: {_currentVegaSpec.Data.Values.Count})");

            // Discover available series names for inspector
            availableSeries = _currentVegaSpec.Data.Values
                .Where(d => d.ContainsKey(colorField))
                .Select(d => d[colorField].ToString())
                .Distinct()
                .ToList();
            UnityEngine.Debug.Log($"Available series: [{string.Join(", ", availableSeries)}]");
        }
        else
        {
            _totalDataPoints = _currentVegaSpec.Data.Values.Count;
            availableSeries = new List<string> { "(all data)" };
        }

        // Determine chart type
        string chartType = _currentVegaSpec.GetMarkType();

        // Set max viewport points based on chart type
        // Line charts: 50 pins wide / 2 = 25 max points
        // Bar charts: depends on thick mode — need min 3px bar + 1px gap = max 12 bars
        // Scatter plots can show all points (overlapping is OK)
        string colorField2 = _currentVegaSpec.Encoding?.GetColorField();
        bool isStackedBar = (chartType == "bar" && colorField2 != null);
        if (chartType == "bar" && (useThickBars || isStackedBar))
        {
            _maxViewportPoints = maxBarsDisplayed;
        }
        else if (chartType == "line" || chartType == "bar")
        {
            _maxViewportPoints = 25;
        }
        else
        {
            _maxViewportPoints = _totalDataPoints;  // No limit for scatter
        }

        // Calculate full data Y-range
        // For multi-layer specs, encoding may be inside layers rather than at top level
        string yField = _currentVegaSpec.Encoding?.Y?.Field
            ?? _currentVegaSpec.Layer?.FirstOrDefault()?.Encoding?.Y?.Field;

        if (yField != null && _currentVegaSpec.Data?.Values != null)
        {
            var allYValues = _currentVegaSpec.Data.Values
                .Where(d => d.ContainsKey(yField))
                .Select(d => Convert.ToSingle(d[yField]))
                .ToList();

            if (allYValues.Any())
            {
                _dataYMin = allYValues.Min();
                _dataYMax = allYValues.Max();
            }
            else
            {
                _dataYMin = 0f;
                _dataYMax = 1f;
            }
        }
        else
        {
            _dataYMin = 0f;
            _dataYMax = 5f;
        }

        // Initialize viewport to show reasonable starting view
        _windowStart = 0;
        _windowSize = Math.Min(_maxViewportPoints, _totalDataPoints);

        // Initialize Y-viewport from spec domain or data range
        if (_currentVegaSpec.Encoding?.Y?.Scale != null && _currentVegaSpec.Encoding.Y.Scale.Domain != null)
        {
            (_windowYMin, _windowYMax) = _currentVegaSpec.Encoding.Y.Scale.GetNumericDomain();
        }
        else
        {
            _windowYMin = _dataYMin;
            _windowYMax = _dataYMax;
        }

        // Store the full Y-axis range
        _fullYMin = _windowYMin;
        _fullYMax = _windowYMax;

        UnityEngine.Debug.Log($"Initialized viewport: X=[{_windowStart}, {_windowStart + _windowSize - 1}] of {_totalDataPoints}, Y=[{_windowYMin:F2}, {_windowYMax:F2}]");

        // Generate PNG preview if missing (optional, won't fail if generator not configured)
        if (_previewGenerator != null)
        {
            _previewGenerator.EnsurePreviewExists(chart);
        }

        // Publish chart data (metadata + PNG) to MQTT for agent UI
        ChartMQTTPublisher.PublishChartToMQTT(_mqttManager, chart);

        // Generate and display using C# renderer
        GenerateAndDisplayRTDGrid(chart);

        // Set file option without reloading CSV
        _rtdUpdater.SetFileOptionWithoutReload(option);
    }

    /// <summary>
    /// Apply a named data layer (e.g. "yearly", "quarterly", "monthly") from the Vega spec.
    /// Replaces top-level data, encoding, and mark with the layer's values, then re-renders.
    /// </summary>
    public void ApplyNamedLayer(string layerName)
    {
        if (_currentVegaSpec?.Layer == null || _currentChart == null)
            return;

        var layer = _currentVegaSpec.Layer.Find(l => l.Name == layerName);
        if (layer == null)
        {
            UnityEngine.Debug.LogWarning($"Layer '{layerName}' not found in spec");
            return;
        }

        // Restore original data before applying layer transforms (re-deserialize to avoid mutation)
        if (_rawVegaJson != null)
        {
            var freshSpec = JsonConvert.DeserializeObject<VegaSpec>(_rawVegaJson);
            if (freshSpec?.Data?.Values != null)
                _currentVegaSpec.Data.Values = freshSpec.Data.Values;
        }

        // Apply layer transforms to data
        if (layer.Data?.Values != null)
        {
            _currentVegaSpec.Data.Values = layer.Data.Values;
        }
        else if (layer.Transform != null && layer.Transform.Count > 0)
        {
            var engine = new VegaTransformEngine();
            var transformedData = engine.ApplyTransforms(
                _currentVegaSpec.Data.Values,
                layer.Transform);
            _currentVegaSpec.Data.Values = transformedData;
        }

        // Apply layer encoding and mark
        if (layer.Encoding != null)
            _currentVegaSpec.Encoding = layer.Encoding;
        if (layer.Mark != null)
            _currentVegaSpec.Mark = layer.Mark;

        // Reset windowing for new data size
        _totalDataPoints = _currentVegaSpec.Data.Values.Count;
        _windowStart = 0;
        _windowSize = Math.Min(_totalDataPoints, _maxViewportPoints);

        // Initialize Y-viewport from layer's encoding scale domain or data range
        string yField = _currentVegaSpec.Encoding?.Y?.Field;
        if (_currentVegaSpec.Encoding?.Y?.Scale?.Domain != null)
        {
            (_windowYMin, _windowYMax) = _currentVegaSpec.Encoding.Y.Scale.GetNumericDomain();
        }
        else if (yField != null)
        {
            var yValues = _currentVegaSpec.Data.Values
                .Where(d => d.ContainsKey(yField))
                .Select(d => Convert.ToSingle(d[yField]))
                .ToList();
            _windowYMin = yValues.Any() ? yValues.Min() : 0f;
            _windowYMax = yValues.Any() ? yValues.Max() : 1f;
        }
        _fullYMin = _windowYMin;
        _fullYMax = _windowYMax;
        _dataYMin = _windowYMin;
        _dataYMax = _windowYMax;

        UnityEngine.Debug.Log($"Applied layer '{layerName}' ({_totalDataPoints} data points, Y=[{_windowYMin:F2}, {_windowYMax:F2}])");
        GenerateAndDisplayRTDGrid(_currentChart);
    }


    /// <summary>
    /// Force a refresh of the current chart display.
    /// </summary>
    public void RefreshChartDisplay()
    {
        if (_currentChart != null && _currentVegaSpec != null)
        {
            UnityEngine.Debug.Log(" Refreshing chart display");
            GenerateAndDisplayRTDGrid(_currentChart);
        }
        else
        {
            UnityEngine.Debug.LogWarning(" Cannot refresh: No chart currently loaded");
        }
    }

    // ===== Overview Layer Support =====

    /// <summary>
    /// Returns the total number of overview layers for the current chart.
    /// Layer sequence: title (full) → x_axis (axes only) → y_axis (axes only) → series... → summary (full)
    /// Multi-series: 1 + 1 + 1 + N + 1 = N + 4
    /// Single-series: 1 + 1 + 1 + 1 = 4
    /// </summary>
    public int GetOverviewLayerCount()
    {
        if (_currentVegaSpec == null) return 5;

        string colorField = _currentVegaSpec.Encoding?.GetColorField();
        if (colorField != null && availableSeries.Count > 0 && availableSeries[0] != "(all data)")
        {
            return availableSeries.Count + 4; // title + x-axis + y-axis + N series + summary
        }
        return 5; // title + x-axis + y-axis + data + summary
    }

    /// <summary>
    /// Set the overview layer and return the description text for TTS.
    /// Layer 0: title + full chart
    /// Layer 1: X-axis only (no Y-axis, no data), key "x_axis"
    /// Layer 2: Y-axis only (no X-axis, no data), key "y_axis"
    /// Layer 3..N+2 (multi-series): each series isolated with both axes, key = series name
    /// Layer 3 (single-series): full data with both axes, key "data"
    /// Last layer: summary + full chart, key "summary"
    /// </summary>
    public (string description, bool found) SetOverviewLayer(int layerIndex)
    {
        int maxLayers = GetOverviewLayerCount();
        var overview = _currentVegaSpec?.Overview;
        bool isMultiSeries = availableSeries.Count > 0 && availableSeries[0] != "(all data)";

        // Layer 0: title + full chart
        if (layerIndex == 0)
        {
            hiddenSeries.Clear();
            GenerateAndDisplayRTDGrid(_currentChart);
            return GetOverviewDescription(overview, "title");
        }

        // Layer 1: X-axis only (hide Y-axis + all data)
        if (layerIndex == 1)
        {
            HideAllSeries();
            hiddenSeries.Add("(y-axis)");
            GenerateAndDisplayRTDGrid(_currentChart);
            return GetOverviewDescription(overview, "x_axis");
        }

        // Layer 2: Y-axis only (hide X-axis + all data)
        if (layerIndex == 2)
        {
            HideAllSeries();
            hiddenSeries.Add("(x-axis)");
            GenerateAndDisplayRTDGrid(_currentChart);
            return GetOverviewDescription(overview, "y_axis");
        }

        // Last layer: summary + full chart
        if (layerIndex == maxLayers - 1)
        {
            hiddenSeries.Clear();
            GenerateAndDisplayRTDGrid(_currentChart);
            return GetOverviewDescription(overview, "summary");
        }

        // Data layers (layer 3 onward)
        if (isMultiSeries)
        {
            // Isolated: show only series[seriesIndex], hide all others
            int seriesIndex = layerIndex - 3;
            if (seriesIndex >= 0 && seriesIndex < availableSeries.Count)
            {
                hiddenSeries.Clear();
                for (int i = 0; i < availableSeries.Count; i++)
                    if (i != seriesIndex) hiddenSeries.Add(availableSeries[i]);
                GenerateAndDisplayRTDGrid(_currentChart);
                return GetOverviewDescription(overview, availableSeries[seriesIndex]);
            }
        }
        else
        {
            // Single-series data layer: show full chart
            hiddenSeries.Clear();
            GenerateAndDisplayRTDGrid(_currentChart);
            return GetOverviewDescription(overview, "data");
        }

        // Fallback
        hiddenSeries.Clear();
        GenerateAndDisplayRTDGrid(_currentChart);
        return GetOverviewDescription(overview, "title");
    }

    private void HideAllSeries()
    {
        hiddenSeries.Clear();
        hiddenSeries.AddRange(availableSeries);
    }

    private (string description, bool found) GetOverviewDescription(Dictionary<string, string> overview, string key)
    {
        if (overview != null && overview.TryGetValue(key, out string desc))
        {
            return (desc, true);
        }
        UnityEngine.Debug.LogWarning($"[Overview] No description found for key '{key}'");
        return ($"Layer: {key}", false);
    }

}
