using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;
using System.IO;
using System;
using System.Linq;

/// <summary>
/// Loads Vega-Lite charts, renders them to the RTD grid with VegaToRTDRenderer, and
/// runs the display window and the layered presentation.
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
    // Feedback grammar: gesture selection static, agent reference animated, stepping
    // settles to static; all persist until cleared.
    [SerializeField] private HighlightConfig gestureConfig = new HighlightConfig { Shape = HighlightMarkShape.Box, Anim = HighlightAnim.Static,   Duration = -1f };
    [SerializeField] private HighlightConfig agentConfig   = new HighlightConfig { Shape = HighlightMarkShape.Box, Anim = HighlightAnim.Animated, Duration = -1f };
    [SerializeField] private HighlightConfig navConfig     = new HighlightConfig { Shape = HighlightMarkShape.Box, Anim = HighlightAnim.Settle,   Duration = -1f };

    // Series hidden on the display: the agent's filter, or the presentation's
    // isolation while it runs.
    private readonly List<string> hiddenSeries = new List<string>();
    private readonly List<string> _agentHiddenSeries = new List<string>();
    [HideInInspector] public List<string> availableSeries = new List<string>();

    /// <summary>The user's filter, as the agent last sent it.</summary>
    public IReadOnlyList<string> FilteredSeries => _agentHiddenSeries;
    private string _hiddenSeriesPublishedKey = "";

    [Header("Range Filter")]
    [Tooltip("Hide data points before this index (-1 = disabled)")]
    [SerializeField] private int rangeFilterStart = -1;
    [Tooltip("Hide data points after this index (-1 = disabled)")]
    [SerializeField] private int rangeFilterEnd = -1;

    [Header("Layered Presentation")]
    [Tooltip("Start the layered presentation (title layer) when a chart loads.")]
    [SerializeField] private bool autoStartPresentation = true;

    // Line points or bars shown at once. Seven keeps a line visible between symbols
    // and bars wide enough for every texture with 2 pins between them.
    private const int MAX_MARKS_SHOWN = 7;

    // ===== Auto-Discovery =====
    private ChartDiscoveryService _chartDiscovery;
    private List<DiscoveredChart> _availableCharts;

    // ===== Current Chart Reference =====
    private DiscoveredChart _currentChart;
    private VegaSpec _currentVegaSpec;
    private bool _pendingInspectorRefresh = false;

    // X-axis windowing (index-based)
    private int _windowStart = 0;
    private int _windowSize = 0;  // Current X-window size
    private int _maxWindowPoints = 0;  // Max points for current chart type (25 for line/bar, unlimited for scatter)

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
        AppLog.Detail(LogArea.Agent, "MQTT connected - publishing chart metadata index...");
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
                AppLog.Detail(LogArea.Chart, $"Found chart: ID={chart.id}, dataset='{chart.dataset}', field='{chart.field}'");
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
                AppLog.Detail(LogArea.Chart, $"Found chart: ID={chart.id}, dataName='{chart.dataName}', chartType='{chart.chartType}'");
                return chart.id;
            }
        }

        UnityEngine.Debug.LogWarning($"No chart found for dataName='{dataName}', chartType='{chartType}'");
        return null;
    }

    /// <summary>
    /// Resolve a chart ID to a DiscoveredChart, falling back to the first
    /// available chart when the ID isn't recognized.
    /// </summary>
    private DiscoveredChart ResolveChart(int option)
    {
        var chart = _chartDiscovery.GetChartById(option);
        if (chart == null)
        {
            UnityEngine.Debug.LogWarning($"Chart with ID {option} not found. Using first available chart.");
            chart = _availableCharts?.FirstOrDefault();
        }
        if (chart == null)
        {
            UnityEngine.Debug.LogError("No charts available!");
        }
        return chart;
    }

    /// <summary>
    /// Deserialize a chart's cached schema JSON into a VegaSpec, or return null
    /// if the cached JSON is missing or malformed.
    /// </summary>
    private VegaSpec ParseSpec(DiscoveredChart chart)
    {
        if (string.IsNullOrEmpty(chart.schemaJson))
        {
            UnityEngine.Debug.LogError($"Chart {chart.id} ({chart.jsonFilePath}) has no cached schema JSON");
            return null;
        }
        try
        {
            var spec = JsonConvert.DeserializeObject<VegaSpec>(chart.schemaJson);
            if (spec == null)
                UnityEngine.Debug.LogError($"Failed to deserialize Vega spec for chart {chart.id} ({chart.jsonFilePath})");
            return spec;
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError($"Failed to deserialize Vega spec for chart {chart.id} ({chart.jsonFilePath}): {ex.Message}");
            return null;
        }
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
        AppLog.Detail(LogArea.Chart, "Rediscovering charts...");
        _availableCharts = _chartDiscovery.DiscoverCharts();
        AppLog.Info(LogArea.Chart, $"Rediscovered {_availableCharts.Count} charts");
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
    /// Uses current window state (window start/size and Y-min/max).
    /// </summary>
    /// <param name="brailleTitle">Show the chart title on the braille line (only on a chart load).</param>
    private void GenerateAndDisplayRTDGrid(DiscoveredChart chart, bool brailleTitle = false)
    {
        try
        {
            // Debug: Track how many times this is called
            AppLog.Detail(LogArea.Render, $"GenerateAndDisplayRTDGrid called");
            AppLog.Detail(LogArea.Render, $"Window: X=[{_windowStart}, {_windowStart + _windowSize - 1}], Y=[{_windowYMin:F2}, {_windowYMax:F2}]");
            if (_currentVegaSpec == null)
            {
                UnityEngine.Debug.LogError(" No Vega spec loaded!");
                return;
            }

            // Get field names
            string xField = _currentVegaSpec.Encoding.X.Field;
            string yField = _currentVegaSpec.Encoding.Y.Field;

            // Get windowed data based on the current window
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

            AppLog.Detail(LogArea.Render, $"Window: X=[{_windowStart}, {_windowStart + _windowSize - 1}], Y=[{_windowYMin:F2}, {_windowYMax:F2}] → {windowedData.Count} visible points (after Y-filter)");

            // Generate RTD grid using C# renderer with window parameters
            var hiddenSet = hiddenSeries.Count > 0 ? new HashSet<string>(hiddenSeries) : null;
            var opts = new VegaToRTDRenderer.RenderOptions
            {
                DrawConnectingLines = drawConnectingLines,
                UseSeriesSymbols = useSeriesSymbols,
                HiddenSeries = hiddenSet,
                UseSeriesLinePatterns = useSeriesLinePatterns,
                UseSeriesLineThickness = useSeriesLineThickness,
                UseBarTextures = useBarTextures,
                SymbolClearance = symbolClearance,
                SeriesSymbolOverrides = ResolveSeriesSymbols(),
                RangeFilterStart = rangeFilterStart,
                RangeFilterEnd = rangeFilterEnd
            };
            var (grid, nodes) = VegaToRTDRenderer.Generate(_currentVegaSpec, _windowStart, _windowSize, _windowYMin, _windowYMax, opts);

            AppLog.Detail(LogArea.Render, $"Generated {grid.GetLength(0)}x{grid.GetLength(1)} RTD grid with {nodes.Count} nodes");

            // The mark type, not the metadata's chartType: highlights, stepping order and
            // bar geometry all test for "bar" or "point".
            string markType = _currentVegaSpec.GetMarkType();

            // Generate graph visualization from nodes
            _graphVisualizer.GenerateGraph(nodes, markType, chart.dataName);

            // Set up visibility filtering: show every node only when the window
            // holds all the data; otherwise hide what falls outside it.
            bool showsAllData = _windowStart == 0 && _windowSize >= _totalDataPoints;

            if (showsAllData)
            {
                AppLog.Detail(LogArea.Render, "Window holds all data - showing all nodes");
                _graphVisualizer.ShowAllNodes();
            }
            else
            {
                // Extract X-values from pre-Y-filter data for X-axis tick visibility
                var xWindowValues = windowedDataBeforeYFilter
                    .Where(d => d.ContainsKey(xField))
                    .Select(d => d[xField])
                    .Distinct()
                    .ToList();

                AppLog.Detail(LogArea.Render, $"Applying visibility filter: {windowedData.Count} visible points, Y-domain [{_windowYMin}, {_windowYMax}], X-window values: {xWindowValues.Count}");
                _graphVisualizer.UpdateVisibleNodes(windowedData, xField, yField, _windowYMin, _windowYMax, xWindowValues);
            }

            // Update the window overlay to show which part of the data is drawn
            _graphVisualizer.UpdateWindowOverlay(_windowStart, _windowSize, _totalDataPoints, _windowYMin, _windowYMax, _dataYMin, _dataYMax);

            // Count non-zero pixels for debug
            int nonZero = 0;
            for (int r = 0; r < grid.GetLength(0); r++)
                for (int c = 0; c < grid.GetLength(1); c++)
                    if (grid[r, c] != 0) nonZero++;

            AppLog.Detail(LogArea.Render, $"Grid contains {nonZero} non-background pixels");

            // Clear any active highlights from the previous chart before displaying the new one.
            _rtdUpdater.RefreshScreen(brailleTitle: false);

            // Display on RTD device
            _rtdUpdater.DisplayImage(grid);

            // Display in Unity visualization
            _rtdUpdater.DisplayImageInUnityFromBase();

            // Set chart type and highlight configs for highlight manager
            _rtdUpdater.SetChartType(markType);
            var axes = _currentVegaSpec.Encoding;
            _rtdUpdater.SetAxisUnits(axes?.X?.Field, axes?.X?.Title, axes?.Y?.Field, axes?.Y?.Title);
            _rtdUpdater.SetInterleavedNavigation(interleavedNavigation);
            _rtdUpdater.SetUseSeriesSymbols(useSeriesSymbols);
            _rtdUpdater.SetSeriesSymbolOverrides(ResolveSeriesSymbols());
            _rtdUpdater.SetHighlightConfigs(ForMark(gestureConfig, markType), ForMark(agentConfig, markType), ForMark(navConfig, markType));

            // Set chart title for braille display and refresh
            string chartTitle = chart.DisplayName ?? $"{chart.chartType} - {chart.dataName}";
            _rtdUpdater.SetChartTitle(chartTitle);   // kept current for the refresh button
            if (brailleTitle)
                _rtdUpdater.DisplayBrailleLabel(chartTitle);

            AppLog.Detail(LogArea.Render, $"DisplayImage() and DisplayImageInUnityFromBase() called successfully");
            _rtdUpdater.EnableDataPointNavigation(true, false);

            // Publish layer data to agent
            string currentHiddenKey = string.Join(",", hiddenSeries);
            string publishMarkType = _currentVegaSpec.GetMarkType();
            // The presentation only isolates series for display, so the agent still
            // gets the whole chart. A filter does limit what it sees.
            var hiddenForAgent = _presentationActive ? null
                : (hiddenSeries.Count > 0 ? new HashSet<string>(hiddenSeries) : null);
            ChartMQTTPublisher.PublishCurrentLayerData(_mqttManager, _currentVegaSpec, publishMarkType, _windowStart, _windowSize, _windowYMin, _windowYMax, hiddenForAgent);
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
        AppLog.Detail(LogArea.Chart, $"LoadChart called for option {option}");

        var chart = ResolveChart(option);
        if (chart == null) return;

        AppLog.Info(LogArea.Chart, $"Selected chart {option}: {chart.DisplayName} (json={chart.jsonFilePath}, png={chart.pngFilePath})");

        var spec = ParseSpec(chart);
        if (spec == null) return;

        _currentVegaSpec = spec;
        _rtdUpdater.ClearOverviewModeForLoad();   // also cancels a pending start
        hiddenSeries.Clear();   // series names are chart-specific
        _agentHiddenSeries.Clear();   // the agent resets its filter on every load too
        _presentationActive = false;

        ApplySpec(chart, option);

        // The rest of the request runs after this load, so don't auto-start.
        bool suppressed = _suppressNextAutoStart;
        _suppressNextAutoStart = false;
        if (autoStartPresentation && !suppressed)
            RequestPresentation();
    }

    private bool _suppressNextAutoStart;

    /// <summary>The next load does not start the presentation on its own.</summary>
    public void SuppressNextAutoStart() => _suppressNextAutoStart = true;

    /// <summary>Clear an unused suppression (its load didn't happen).</summary>
    public void AllowAutoStart() => _suppressNextAutoStart = false;

    private const string NoWalkthrough = "There's no walkthrough for this chart.";
    private const float GeneratedTextTimeout = 30f;
    private Coroutine _generatedTextTimeout;

    /// <summary>
    /// Start the layered presentation now if its text is ready, when the agent's
    /// generated text arrives, or say there is no walkthrough.
    /// </summary>
    public void RequestPresentation()
    {
        if (_currentChart == null || _currentVegaSpec == null) return;
        var overview = GetEffectiveOverview();
        if (overview != null && overview.ContainsKey("title"))
        {
            _rtdUpdater.StartOverviewPresentation();
            return;
        }
        string key = OverviewKey(_currentChart.dataName, _currentChart.chartType);
        if (_noGeneratedOverview.Contains(key))
        {
            _rtdUpdater.SpeakNotice(NoWalkthrough);
            return;
        }
        // Generated text arrives a few seconds after load. Wait for it so no layer
        // speaks a placeholder.
        _presentationPendingFor = key;
        if (_generatedTextTimeout != null) StopCoroutine(_generatedTextTimeout);
        _generatedTextTimeout = StartCoroutine(GiveUpOnGeneratedText(key));
    }

    private IEnumerator GiveUpOnGeneratedText(string key)
    {
        yield return new WaitForSeconds(GeneratedTextTimeout);
        _generatedTextTimeout = null;
        if (_presentationPendingFor != key) yield break;
        _presentationPendingFor = null;
        AppLog.Info(LogArea.Presentation, $"No layer text for '{key}' after {GeneratedTextTimeout}s");
        _rtdUpdater.SpeakNotice(NoWalkthrough);
    }

    /// <summary>Stop waiting to start the presentation.</summary>
    public void CancelPendingPresentation()
    {
        _presentationPendingFor = null;
        if (_generatedTextTimeout != null)
        {
            StopCoroutine(_generatedTextTimeout);
            _generatedTextTimeout = null;
        }
    }

    /// <summary>
    /// Apply a parsed Vega spec: resolve layers, run transforms, size the window,
    /// compute the Y-range, push metadata to MQTT, and render the RTD grid.
    /// </summary>
    private void ApplySpec(DiscoveredChart chart, int option)
    {
        // Apply transforms to data if specified in spec
        if (_currentVegaSpec.Transform != null && _currentVegaSpec.Transform.Count > 0 &&
            _currentVegaSpec.Data?.Values != null)
        {
            var transformEngine = new VegaTransformEngine();
            AppLog.Detail(LogArea.Chart, $"Applying {_currentVegaSpec.Transform.Count} transforms to data");

            var transformedData = transformEngine.ApplyTransforms(
                _currentVegaSpec.Data.Values,
                _currentVegaSpec.Transform
            );

            _currentVegaSpec.Data.Values = transformedData;
            AppLog.Detail(LogArea.Chart, $"Transformed data: {transformedData.Count} rows");
        }

        StampRowIds(_currentVegaSpec.Data?.Values);

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
            AppLog.Detail(LogArea.Chart, $"Multi-series detected (color field: '{colorField}'): {_totalDataPoints} unique X values (raw rows: {_currentVegaSpec.Data.Values.Count})");

            // Discover available series names for inspector
            availableSeries = _currentVegaSpec.Data.Values
                .Where(d => d.ContainsKey(colorField))
                .Select(d => d[colorField].ToString())
                .Distinct()
                .ToList();
            AppLog.Detail(LogArea.Chart, $"Available series: [{string.Join(", ", availableSeries)}]");
        }
        else
        {
            _totalDataPoints = _currentVegaSpec.Data.Values.Count;
            availableSeries = new List<string> { "(all data)" };
        }

        // Determine chart type
        string chartType = _currentVegaSpec.GetMarkType();

        // Lines and bars show at most MAX_MARKS_SHOWN; scatterplots show every point.
        _maxWindowPoints = (chartType == "line" || chartType == "bar")
            ? MAX_MARKS_SHOWN
            : _totalDataPoints;

        // Calculate full data Y-range
        string yField = _currentVegaSpec.Encoding?.Y?.Field;

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

            // Bars start at zero, and a stacked bar reaches its total.
            string xField = _currentVegaSpec.Encoding?.X?.Field;
            string seriesField = _currentVegaSpec.Encoding?.GetColorField();
            if (chartType == "bar" && allYValues.Any())
            {
                _dataYMin = Math.Min(0f, _dataYMin);
                if (seriesField != null && xField != null)
                {
                    float maxTotal = _currentVegaSpec.Data.Values
                        .Where(d => d.ContainsKey(yField) && d.ContainsKey(xField))
                        .GroupBy(d => d[xField]?.ToString())
                        .Max(g => g.Sum(d => Math.Abs(Convert.ToSingle(d[yField]))));
                    _dataYMax = Math.Max(_dataYMax, maxTotal);
                }
            }
        }
        else
        {
            _dataYMin = 0f;
            _dataYMax = 5f;
        }

        // Start the window at the first point
        _windowStart = 0;
        _windowSize = Math.Min(_maxWindowPoints, _totalDataPoints);

        // Initialize Y-window from spec domain or data range
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

        AppLog.Detail(LogArea.Chart, $"Initialized window: X=[{_windowStart}, {_windowStart + _windowSize - 1}] of {_totalDataPoints}, Y=[{_windowYMin:F2}, {_windowYMax:F2}]");

        // Generate PNG preview if missing (optional, won't fail if generator not configured)
        if (_previewGenerator != null)
        {
            _previewGenerator.EnsurePreviewExists(chart);
        }

        // Publish chart data (metadata + PNG) to MQTT for agent UI
        ChartMQTTPublisher.PublishChartToMQTT(_mqttManager, chart, GetRenderedChartInfo());

        // Generate and display using C# renderer
        GenerateAndDisplayRTDGrid(chart, brailleTitle: true);

        // Set file option without reloading CSV
        _rtdUpdater.SetFileOptionWithoutReload(option);
    }

    /// <summary>
    /// Give every row a stable id ("row-&lt;index&gt;") so the agent's rows and the pins
    /// can be matched exactly. Rows that already have an _id keep it.
    /// </summary>
    private static void StampRowIds(List<Dictionary<string, object>> rows)
    {
        if (rows == null) return;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i] != null && !rows[i].ContainsKey(RowIdField))
                rows[i][RowIdField] = $"row-{i}";
        }
    }

    public const string RowIdField = "_id";

    /// <summary>
    /// Force a refresh of the current chart display.
    /// </summary>
    public void RefreshChartDisplay()
    {
        if (_currentChart != null && _currentVegaSpec != null)
        {
            AppLog.Detail(LogArea.Render, "Refreshing chart display");
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
        _presentationActive = true;
        _presentationLayerIndex = layerIndex;
        int maxLayers = GetOverviewLayerCount();
        var overview = GetEffectiveOverview();
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

    /// <summary>
    /// Leave the layered presentation: show the user's filter again and restart from
    /// the first layer next time.
    /// </summary>
    public void EndOverviewPresentation()
    {
        if (_presentationActive) AppLog.Info(LogArea.Presentation, "Ended");
        _presentationActive = false;
        _rtdUpdater.ResetOverviewLayer();
        if (_currentChart == null || _currentVegaSpec == null) return;
        hiddenSeries.Clear();
        hiddenSeries.AddRange(_agentHiddenSeries);
        GenerateAndDisplayRTDGrid(_currentChart);
    }

    private string _presentationPendingFor;   // chart key waiting for generated text to start
    private bool _presentationActive;         // a layered-presentation layer is on the display
    private int _presentationLayerIndex = -1;

    /// <summary>
    /// Store the agent's filter; unknown series are ignored. Doesn't redraw: the caller
    /// ends the presentation (which restores the filter) or calls ShowAgentFilter.
    /// </summary>
    public void SetAgentFilter(IEnumerable<string> names)
    {
        _agentHiddenSeries.Clear();
        if (names != null)
            _agentHiddenSeries.AddRange(names.Where(n => availableSeries.Contains(n)).Distinct());
        AppLog.Info(LogArea.Chart, $"Hidden series: [{string.Join(", ", _agentHiddenSeries)}]");
    }

    /// <summary>Show the agent's filter. The redraw republishes the view to the agent.</summary>
    public void ShowAgentFilter()
    {
        _presentationActive = false;
        if (_currentChart == null || _currentVegaSpec == null) return;
        hiddenSeries.Clear();
        hiddenSeries.AddRange(_agentHiddenSeries);
        GenerateAndDisplayRTDGrid(_currentChart);
    }

    /// <summary>The presentation layer on the display ({layer, series}), or null.</summary>
    public Dictionary<string, object> CurrentPresentationLayer()
    {
        if (!_presentationActive || _currentVegaSpec == null || _presentationLayerIndex < 0) return null;
        int maxLayers = GetOverviewLayerCount();
        bool isMultiSeries = availableSeries.Count > 0 && availableSeries[0] != "(all data)";
        int i = _presentationLayerIndex;
        string layer = i == 0 ? "title" : i == 1 ? "x_axis" : i == 2 ? "y_axis"
                     : i == maxLayers - 1 ? "summary" : isMultiSeries ? "series" : "data";
        var info = new Dictionary<string, object> { ["layer"] = layer };
        if (layer == "series" && i - 3 >= 0 && i - 3 < availableSeries.Count)
            info["series"] = availableSeries[i - 3];
        return info;
    }

    /// <summary>
    /// During the presentation, show the series an answer points at that the current
    /// layer hides. The next layer step isolates again. Returns true if it redrew.
    /// </summary>
    public bool RevealSeriesFor(IEnumerable<string> rowIds, IEnumerable<string> seriesNames)
    {
        if (!_presentationActive || _currentChart == null || _currentVegaSpec == null) return false;
        string colorField = _currentVegaSpec.Encoding?.GetColorField();
        bool isMultiSeries = colorField != null && availableSeries.Count > 0 && availableSeries[0] != "(all data)";
        var wanted = new HashSet<string>(seriesNames ?? Enumerable.Empty<string>());
        var ids = new HashSet<string>(rowIds ?? Enumerable.Empty<string>());
        if (ids.Count > 0 && _currentVegaSpec.Data?.Values != null)
        {
            foreach (var row in _currentVegaSpec.Data.Values)
            {
                if (row == null || !row.TryGetValue(RowIdField, out var id) || !ids.Contains(id?.ToString())) continue;
                if (!isMultiSeries) wanted.Add("(all data)");
                else if (row.TryGetValue(colorField, out var s) && s != null) wanted.Add(s.ToString());
            }
        }
        int removed = hiddenSeries.RemoveAll(s => wanted.Contains(s));
        if (removed == 0) return false;
        AppLog.Info(LogArea.Presentation, $"Answer reveals: [{string.Join(", ", wanted)}]");
        GenerateAndDisplayRTDGrid(_currentChart);
        return true;
    }

    // Agent-generated layer text for charts without an "overview" block, keyed by
    // data name and chart type (a line and a bar chart can share data).
    private readonly Dictionary<string, Dictionary<string, string>> _generatedOverviews =
        new Dictionary<string, Dictionary<string, string>>();
    private readonly HashSet<string> _noGeneratedOverview = new HashSet<string>();

    private static string OverviewKey(string dataName, string chartType) => $"{dataName}|{chartType}";

    /// <summary>
    /// Store the agent's layer text for a chart. Null means there is nothing to
    /// describe, so there is no walkthrough.
    /// </summary>
    public void SetGeneratedOverview(string dataName, string chartType, Dictionary<string, string> overview)
    {
        if (string.IsNullOrEmpty(dataName)) return;
        if (string.IsNullOrEmpty(chartType) && _currentChart?.dataName == dataName)
            chartType = _currentChart.chartType;   // an agent that doesn't send the type
        string key = OverviewKey(dataName, chartType);
        bool waiting = _presentationPendingFor == key &&
                       _currentChart != null && OverviewKey(_currentChart.dataName, _currentChart.chartType) == key;

        if (overview == null || overview.Count == 0)
        {
            _noGeneratedOverview.Add(key);
            AppLog.Info(LogArea.Presentation, $"No layer text for '{key}'");
            if (waiting)
            {
                CancelPendingPresentation();
                _rtdUpdater.SpeakNotice(NoWalkthrough);
            }
            return;
        }

        _generatedOverviews[key] = overview;
        _noGeneratedOverview.Remove(key);
        AppLog.Info(LogArea.Presentation, $"Generated layer text received for '{key}' ({overview.Count} layers)");
        if (waiting)
        {
            CancelPendingPresentation();
            _rtdUpdater.StartOverviewPresentation();
        }
    }

    /// <summary>The spec's own layer text wins; otherwise the agent's generated text.</summary>
    private Dictionary<string, string> GetEffectiveOverview()
    {
        var authored = _currentVegaSpec?.Overview;
        if (authored != null && authored.Count > 0) return authored;
        if (_currentChart == null) return authored;
        return _generatedOverviews.TryGetValue(OverviewKey(_currentChart.dataName, _currentChart.chartType), out var generated)
            ? generated : authored;
    }

    /// <summary>
    /// Symbol per series: the spec's shape channel, then the Inspector slots, then the
    /// first symbol no other series uses.
    /// </summary>
    private RTDGridConstants.SymbolType[] ResolveSeriesSymbols()
    {
        var inspector = new[] { seriesSymbol0, seriesSymbol1, seriesSymbol2, seriesSymbol3 };
        int n = Math.Max(inspector.Length, availableSeries.Count);
        var result = new RTDGridConstants.SymbolType[n];
        for (int i = 0; i < n; i++)
            result[i] = i < inspector.Length ? inspector[i] : RTDGridConstants.SymbolType.Default;

        var shapeMap = _currentVegaSpec?.Encoding?.GetShapeMap(_currentVegaSpec.Encoding.GetColorField());
        if (shapeMap != null)
        {
            for (int i = 0; i < availableSeries.Count; i++)
            {
                if (shapeMap.TryGetValue(availableSeries[i], out var shape) &&
                    RTDGridConstants.TryParseVegaShape(shape, out var symbol))
                    result[i] = symbol;
            }
        }

        // Two series should never feel the same, so fill the gaps with unused
        // symbols. They only repeat past six series.
        int rotation = RTDGridConstants.SERIES_SYMBOLS.Length;
        var taken = new HashSet<RTDGridConstants.SymbolType>();
        for (int i = 0; i < availableSeries.Count && i < n; i++)
            if (result[i] != RTDGridConstants.SymbolType.Default) taken.Add(result[i]);
        int next = 0;
        for (int i = 0; i < availableSeries.Count && i < n; i++)
        {
            if (result[i] != RTDGridConstants.SymbolType.Default) continue;
            var pick = (RTDGridConstants.SymbolType)(i % rotation);
            for (int tries = 0; tries < rotation; tries++, next++)
            {
                var candidate = (RTDGridConstants.SymbolType)(next % rotation);
                if (!taken.Contains(candidate)) { pick = candidate; next++; break; }
            }
            result[i] = pick;
            taken.Add(pick);
        }
        return result;
    }

    /// <summary>
    /// Bars highlight their infill rather than a box, the paper's grammar for bars; each
    /// source keeps its own animation and duration.
    /// </summary>
    private static HighlightConfig ForMark(HighlightConfig config, string markType)
    {
        if (markType == "bar")
        {
            config.Shape = HighlightMarkShape.BarInterior;
            config.UseBatchSend = true;
        }
        return config;
    }

    /// <summary>
    /// What the display shows, for the agent: each series' symbol (or, for bars, texture)
    /// by its spoken name, and the Y range and ticks drawn (the spec may not state them).
    /// </summary>
    private object GetRenderedChartInfo()
    {
        string chartType = _currentVegaSpec.GetMarkType();
        bool multiSeries = availableSeries.Count > 0 && availableSeries[0] != "(all data)";
        var symbols = ResolveSeriesSymbols();
        string SymbolFor(int i)
        {
            var sym = (i < symbols.Length && symbols[i] != RTDGridConstants.SymbolType.Default)
                ? symbols[i]
                : (RTDGridConstants.SymbolType)(i % RTDGridConstants.SERIES_SYMBOLS.Length);
            return RTDGridConstants.SpokenSymbolName(sym);
        }

        // Bars are drawn solid or textured, never with symbols; a single-series scatter
        // uses one pin per point.
        object series = null;
        if (chartType == "bar")
        {
            if (multiSeries)
            {
                var stackOrder = VegaToRTDRenderer.BarStackOrder(_currentVegaSpec, availableSeries);
                series = availableSeries.Select(name => new
                {
                    name,
                    texture = RTDGridConstants.SpokenTextureName(
                        VegaToRTDRenderer.BarTexture(_currentVegaSpec, name, stackOrder, useBarTextures))
                }).ToList<object>();
            }
            else
            {
                series = new List<object> { new { name = "data", texture = "solid" } };
            }
        }
        else if (useSeriesSymbols)
        {
            series = multiSeries
                ? availableSeries.Select((name, i) => new { name, symbol = SymbolFor(i) }).ToList<object>()
                : new List<object> { new { name = "data", symbol = chartType == "point" ? "single dot" : SymbolFor(0) } };
        }

        return new
        {
            chart_type = chartType,
            series,
            y_domain = new[] { _windowYMin, _windowYMax },
            y_ticks = VegaToRTDRenderer.ResolveYTickValues(_currentVegaSpec.Encoding?.Y, _windowYMin, _windowYMax),
            points_shown = _windowSize,
            points_total = _totalDataPoints,
        };
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
