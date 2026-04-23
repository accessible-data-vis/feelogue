using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json.Linq;
using static RTDGridConstants;

/// <summary>
/// Renders Vega-Lite specifications to RTD grid format.
/// Orchestrates rendering by delegating to RTDLayout (math), RTDDrawing (grid ops), and RTDGridConstants.
/// </summary>
public static class VegaToRTDRenderer
{
    /// <summary>
    /// Rendering options for RTD grid generation.
    /// </summary>
    public class RenderOptions
    {
        public bool DrawConnectingLines { get; set; }
        public bool UseSeriesSymbols { get; set; }
        public bool UseThickBars { get; set; }
        public HashSet<string> HiddenSeries { get; set; }
        public bool UseSeriesLinePatterns { get; set; }
        public bool UseSeriesLineThickness { get; set; }
        public bool UseBarTextures { get; set; }
        public int SymbolClearance { get; set; }
        public SymbolType[] SeriesSymbolOverrides { get; set; }
        public int RangeFilterStart { get; set; } = -1;
        public int RangeFilterEnd { get; set; } = -1;
    }

    private static int GetSymbolIndex(int seriesIndex, RenderOptions opts)
    {
        if (opts.SeriesSymbolOverrides != null && seriesIndex < opts.SeriesSymbolOverrides.Length
            && opts.SeriesSymbolOverrides[seriesIndex] != SymbolType.Default)
            return (int)opts.SeriesSymbolOverrides[seriesIndex];
        return seriesIndex % SERIES_SYMBOLS.Length;
    }

    /// <summary>
    /// Returns the fixed pixel bounds for the chart data area.
    /// xPixelMax=55 gives a 49px range (6..55), highly composite for even spacing.
    /// </summary>
    private static (int xPixelMin, int xPixelMax, int yPixelMin, int yPixelMax) GetChartPixelBounds()
    {
        return (6, 55, 0, 36);
    }

    /// <summary>
    /// Determines whether Y-axis and X-axis should be hidden based on HiddenSeries options.
    /// </summary>
    private static (bool hideYAxis, bool hideXAxis) GetAxisVisibility(RenderOptions opts)
    {
        bool hideY = opts.HiddenSeries != null && opts.HiddenSeries.Contains("(y-axis)");
        bool hideX = opts.HiddenSeries != null && opts.HiddenSeries.Contains("(x-axis)");
        return (hideY, hideX);
    }

    /// <summary>
    /// Draws axes (X, Y, zero-line) and Y-axis ticks. Returns the zero-line row.
    /// </summary>
    private static int DrawAxesAndTicks(int[,] grid, List<ChartNode> nodes,
        float yMin, float yMax, List<float> yTickValues, string yField,
        int xPixelMax, int yPixelMin, int yPixelMax,
        bool hideYAxis, bool hideXAxis)
    {
        int zeroLineRow = RTDDrawing.DrawAxesAndZeroLine(grid, yMin, yMax, xPixelMax, yPixelMin, yPixelMax, !hideYAxis, !hideXAxis);
        if (!hideYAxis)
            RTDDrawing.DrawYAxisTicks(grid, nodes, yTickValues, yField, yMin, yMax, yPixelMin, yPixelMax);
        return zeroLineRow;
    }

    /// <summary>
    /// Creates an x-axis tick ChartNode, draws the tick marker on the grid, and adds the node to the list.
    /// </summary>
    private static void AddXAxisTick(int[,] grid, List<ChartNode> nodes,
        int col, object xValue, string xField, ref int tickIndex)
    {
        RTDDrawing.DrawXTickMarker(grid, col);

        var xTickNode = new ChartNode($"x-axis-tick-{tickIndex}", "x-axis-tick");
        xTickNode.Coordinates.Add((col, X_AXIS_ROW));
        xTickNode.Values[xField] = xValue;
        nodes.Add(xTickNode);
        tickIndex++;
    }

    /// <summary>
    /// Generate RTD grid from Vega-Lite spec with viewport parameters.
    /// Returns both the grid and node position data.
    /// </summary>
    public static (int[,] grid, List<ChartNode> nodes) Generate(VegaSpec spec, int windowStart, int windowSize, float windowYMin, float windowYMax, RenderOptions opts = null)
    {
        if (opts == null) opts = new RenderOptions();

        // Initialize empty grid
        int[,] grid = new int[GRID_HEIGHT, GRID_WIDTH];

        // Get chart type
        string chartType = spec.GetMarkType();
        Debug.Log($"Generating {chartType} chart with viewport: X=[{windowStart}, {windowStart + windowSize - 1}], Y=[{windowYMin:F2}, {windowYMax:F2}]");
        Debug.Log($"Generate() entry: spec.Encoding={spec.Encoding != null}, spec.Encoding.X={spec.Encoding?.X != null}, spec.Encoding.Y={spec.Encoding?.Y != null}");

        // Get encodings
        var xEncoding = spec.Encoding.X;
        var yEncoding = spec.Encoding.Y;
        string xField = xEncoding.Field;
        string yField = yEncoding.Field;

        // Check for multi-series (color field grouping)
        string colorField = spec.Encoding.GetColorField();
        if (colorField != null)
        {
            Debug.Log($"Multi-series chart detected: color field = '{colorField}'");
            return GenerateMultiSeries(spec, grid, windowStart, windowSize, windowYMin, windowYMax, chartType, xField, yField, colorField, opts);
        }

        if (chartType == "line")
            return GenerateMultiSeries(spec, grid, windowStart, windowSize, windowYMin, windowYMax, chartType, xField, yField, null, opts);

        // Single-series bar/scatter path
        var fullData = spec.Data.Values;
        var windowedData = fullData.GetRange(windowStart, Math.Min(windowSize, fullData.Count - windowStart));

        // Store original indices before Y-filtering (for preserving X-spacing)
        var windowedDataWithIndices = windowedData.Select((d, i) => new { Data = d, WindowIndex = i }).ToList();

        // Filter by Y-window, preserving original window indices
        var filteredData = windowedDataWithIndices.Where(item =>
        {
            if (item.Data.ContainsKey(yField))
            {
                float yValue = RTDLayout.GetNumericValue(item.Data[yField]);
                return yValue >= windowYMin && yValue <= windowYMax;
            }
            return false;
        }).ToList();

        Debug.Log($"Windowed to {windowedData.Count} points, filtered to {filteredData.Count} visible in Y-range");

        List<float> yTickValues = ResolveYTickValues(yEncoding, windowYMin, windowYMax);

        // Use viewport bounds for data mapping, NOT tick values
        // Tick values are just for drawing tick markers
        float yMin = windowYMin;
        float yMax = windowYMax;

        // Draw axes and data (implementation continues below...)
        // Convert anonymous type to tuple for method signature
        var windowedDataTuples = windowedDataWithIndices.Select(item => (item.Data, item.WindowIndex)).ToList();
        var filteredDataTuples = filteredData.Select(item => (item.Data, item.WindowIndex)).ToList();

        // Pass full dataset with global indices for creating all nodes
        var fullDataWithIndices = fullData.Select((d, i) => (d, i)).ToList();

        return DrawGridWithViewport(spec, grid, windowedDataTuples, filteredDataTuples, fullDataWithIndices, windowStart, windowSize, chartType, xField, yField, yMin, yMax, yTickValues, opts);
    }

    /// <summary>
    /// Generate RTD grid for multi-series charts (color field grouping).
    /// Windows by unique X values instead of raw row indices.
    /// </summary>
    private static (int[,] grid, List<ChartNode> nodes) GenerateMultiSeries(
        VegaSpec spec, int[,] grid, int windowStart, int windowSize,
        float windowYMin, float windowYMax, string chartType,
        string xField, string yField, string colorField, RenderOptions opts)
    {
        var nodes = new List<ChartNode>();
        var fullData = spec.Data.Values;

        // Group data by unique X values, preserving order of first appearance
        var uniqueXValues = new List<object>();
        var xGrouped = new Dictionary<string, List<Dictionary<string, object>>>();

        foreach (var row in fullData)
        {
            if (!row.ContainsKey(xField)) continue;
            string xKey = row[xField].ToString();
            if (!xGrouped.ContainsKey(xKey))
            {
                xGrouped[xKey] = new List<Dictionary<string, object>>();
                uniqueXValues.Add(row[xField]);
            }
            xGrouped[xKey].Add(row);
        }

        int uniqueXCount = uniqueXValues.Count;
        Debug.Log($"Multi-series: {uniqueXCount} unique X values, windowStart={windowStart}, windowSize={windowSize}");

        // Window by unique X values
        int effectiveStart = Math.Min(windowStart, uniqueXCount);
        int effectiveSize = Math.Min(windowSize, uniqueXCount - effectiveStart);
        var windowedXValues = uniqueXValues.GetRange(effectiveStart, effectiveSize);

        // Get all series names
        var seriesNames = colorField != null
            ? fullData.Where(d => d.ContainsKey(colorField))
                      .Select(d => d[colorField].ToString())
                      .Distinct()
                      .ToList()
            : new List<string> { "_default" };
        Debug.Log($"Multi-series: {seriesNames.Count} series: [{string.Join(", ", seriesNames)}]");

        // Capture original (pre-filter) index so symbols stay stable when series are hidden
        var allSeriesNames = seriesNames.ToList(); // copy before filtering
        var seriesOriginalIndex = new Dictionary<string, int>();
        for (int i = 0; i < allSeriesNames.Count; i++)
            seriesOriginalIndex[allSeriesNames[i]] = i;

        // Filter out hidden series
        if (opts.HiddenSeries != null && opts.HiddenSeries.Count > 0)
        {
            seriesNames = seriesNames.Where(s => !opts.HiddenSeries.Contains(s)).ToList();
            Debug.Log($"After filtering hidden series: {seriesNames.Count} visible: [{string.Join(", ", seriesNames)}]");
        }

        // Generate Y-axis ticks
        var yEncoding = spec.Encoding.Y;
        List<float> yTickValues = ResolveYTickValues(yEncoding, windowYMin, windowYMax);

        float yMin = windowYMin;
        float yMax = windowYMax;

        var (xPixelMin, xPixelMax, yPixelMin, yPixelMax) = GetChartPixelBounds();

        // For scatter plots with quantitative X: compute xMin/xMax and nice tick values
        bool xIsQuantitative = chartType == "point" && !spec.Encoding.X.IsCategorical();
        float xMin = 0f, xMax = 1f;
        List<float> xTickValues = null;
        if (xIsQuantitative)
        {
            var xEncoding = spec.Encoding.X;
            (xMin, xMax) = RTDLayout.GetNumericDomain(xEncoding, fullData, xField);
            xTickValues = RTDLayout.GenerateNiceTicks(xMin, xMax, 6);
            // Expand mapping domain to nice tick boundaries so ticks align exactly at axis edges
            if (xTickValues.Count >= 2)
            {
                xMin = xTickValues[0];
                xMax = xTickValues[xTickValues.Count - 1];
            }
            Debug.Log($"Scatter X-axis: nice domain=[{xMin}, {xMax}], {xTickValues.Count} nice ticks: [{string.Join(", ", xTickValues)}]");
        }

        var (hideYAxisMulti, hideXAxisMulti) = GetAxisVisibility(opts);
        bool hideAllData = opts.HiddenSeries != null && opts.HiddenSeries.Contains("(all data)");
        int zeroLineRow = DrawAxesAndTicks(grid, nodes, yMin, yMax, yTickValues, yField, xPixelMax, yPixelMin, yPixelMax, hideYAxisMulti, hideXAxisMulti);

        // Draw X-axis ticks
        int windowedCount = windowedXValues.Count;
        // Pre-compute stacked bar geometry (used by bar tick loop and bar drawing section)
        int stackedBarWidth = RTDLayout.CalculateBarWidth(windowedCount);
        int stackedHalfBar = stackedBarWidth / 2;
        int barXPixelMin = xPixelMin + stackedHalfBar;
        int barXPixelMax = xPixelMax - stackedHalfBar;
        int xTickIndex = 0;
        if (!hideXAxisMulti && xIsQuantitative && xTickValues != null)
        {
            // Scatter: draw ticks at proportional positions based on actual X values
            for (int t = 0; t < xTickValues.Count; t++)
            {
                float tickVal = xTickValues[t];
                int col = RTDLayout.MapValueToPixel(tickVal, xMin, xMax, xPixelMin, xPixelMax);
                AddXAxisTick(grid, nodes, col, tickVal, xField, ref xTickIndex);
            }
        }
        else if (!hideXAxisMulti)
        {
            HashSet<float> specTickSet = null;
            var specTickVals = spec.Encoding?.X?.Axis?.GetNumericValues();
            if (specTickVals != null && specTickVals.Length > 0)
                specTickSet = new HashSet<float>(specTickVals);

            const int MAX_X_TICKS = 10;
            for (int i = 0; i < windowedCount; i++)
            {
                int tickXMin = chartType == "bar" ? barXPixelMin : xPixelMin;
                int tickXMax = chartType == "bar" ? barXPixelMax : xPixelMax;
                int col = RTDLayout.MapIndexToPixel(i, windowedCount, tickXMin, tickXMax);
                object xVal = windowedXValues[i];

                bool drawTick = specTickSet != null
                    ? specTickSet.Contains(RTDLayout.GetNumericValue(xVal))
                    : RTDLayout.ShouldDrawXTick(i, windowedCount, MAX_X_TICKS);

                if (drawTick)
                {
                    AddXAxisTick(grid, nodes, col, xVal, xField, ref xTickIndex);
                    CopyRtdIndexBaseField(nodes[nodes.Count - 1].Values, xField, xVal, xGrouped);
                }
            }
        }

        // Stacked bar chart path vs line/scatter path
        if (chartType == "bar")
        {
            // ===== STACKED BAR CHART =====
            // Always use thick bars for stacked charts (thin stacked bars would be confusing)

            // Determine stack order: reverse-alphabetical = bottom segment first.
            // This matches Vega-Lite's default: color domain is alphabetical, bars stack last-alpha at bottom.
            // If the spec provides an explicit color scale domain, use that order reversed instead.
            List<string> stackOrder;
            var colorDomain = spec.Encoding.GetColorStringDomain();
            if (colorDomain != null && colorDomain.Count > 0)
                stackOrder = Enumerable.Reverse(colorDomain).Where(s => seriesNames.Contains(s)).ToList();
            else
                stackOrder = seriesNames.OrderByDescending(s => s).ToList();
            Debug.Log($"Stacked bar chart: {seriesNames.Count} series, {windowedCount} X positions, barWidth={stackedBarWidth}, stack order (bottom→top): [{string.Join(", ", stackOrder)}]");

            int globalNodeIndex = 0;
            int windowEnd = effectiveStart + effectiveSize;

            // Drawing + node creation pass for windowed X positions
            for (int i = 0; i < windowedCount; i++)
            {
                object xVal = windowedXValues[i];
                string xKey = xVal.ToString();
                int col = RTDLayout.MapIndexToPixel(i, windowedCount, barXPixelMin, barXPixelMax);

                if (!xGrouped.ContainsKey(xKey)) continue;

                // Collect values for each series at this X, in stack order (biggest total first = bottom)
                var seriesValues = new List<(string seriesName, float value, Dictionary<string, object> rowData)>();
                foreach (var seriesName in stackOrder)
                {
                    var matchingRow = xGrouped[xKey].FirstOrDefault(r =>
                        r.ContainsKey(colorField) && r[colorField].ToString() == seriesName && r.ContainsKey(yField));
                    if (matchingRow != null)
                    {
                        float val = RTDLayout.GetNumericValue(matchingRow[yField]);
                        seriesValues.Add((seriesName, val, matchingRow));
                    }
                }

                // Stack segments upward from zero-line
                // zeroLineRow is in pixel space (higher row = lower on screen)
                int currentTopRow = zeroLineRow; // Start stacking from zero-line

                for (int si = 0; si < seriesValues.Count; si++)
                {
                    var (seriesName, value, rowData) = seriesValues[si];

                    // Calculate the height of this segment in pixel space
                    // Map the value to pixel rows relative to the zero line
                    int segmentPixelHeight = 0;
                    if (yMax > yMin)
                    {
                        float pixelsPerUnit = (float)(yPixelMax - yPixelMin) / (yMax - yMin);
                        segmentPixelHeight = Math.Max(1, Mathf.RoundToInt(Math.Abs(value) * pixelsPerUnit));
                    }
                    else
                    {
                        segmentPixelHeight = 1;
                    }

                    // Segment goes from currentTopRow upward (lower row numbers = higher on screen)
                    int segStart;
                    if (si > 0)
                        segStart = currentTopRow - 2; // 1-pin gap between segments
                    else
                        segStart = currentTopRow; // No gap for first segment from zero-line

                    int segEnd = segStart - segmentPixelHeight + 1;
                    segEnd = Math.Max(yPixelMin, segEnd); // Clamp to top of chart area

                    if (segEnd > segStart) continue; // Skip if no room

                    // Draw the bar segment (always thick for stacked, dynamic width)
                    int fillPattern = opts.UseBarTextures ? (si % BAR_FILL_PATTERN_COUNT) : BAR_FILL_SOLID;
                    var barCoords = RTDDrawing.DrawBar(grid, col, segEnd, segStart, stackedBarWidth, fillPattern);

                    // Create a ChartNode for this segment
                    var dataNode = new ChartNode($"data-point-{globalNodeIndex}", "data-point");
                    dataNode.Values[xField] = rowData[xField];
                    dataNode.Values[yField] = rowData[yField];
                    dataNode.Values[colorField] = seriesName;
                    dataNode.Series = seriesName;
                    dataNode.Visibility = true;
                    dataNode.Coordinates.AddRange(barCoords);

                    CopyRtdIndexBaseField(dataNode.Values, xField, rowData);

                    nodes.Add(dataNode);
                    globalNodeIndex++;

                    // Move the stacking cursor up past this segment (gap applied at next segment start)
                    currentTopRow = segEnd;
                }
            }

            // Also generate hidden nodes for data outside the window
            for (int xi = 0; xi < uniqueXCount; xi++)
            {
                bool inXWindow = xi >= effectiveStart && xi < windowEnd;
                if (inXWindow) continue; // Already created visible nodes above

                object xVal = uniqueXValues[xi];
                string xKey = xVal.ToString();
                if (!xGrouped.ContainsKey(xKey)) continue;

                foreach (var row in xGrouped[xKey])
                {
                    if (!row.ContainsKey(yField) || !row.ContainsKey(colorField)) continue;

                    string seriesVal = row[colorField].ToString();
                    var dataNode = new ChartNode($"data-point-{globalNodeIndex}", "data-point");
                    dataNode.Values[colorField] = seriesVal;
                    dataNode.Values[xField] = row[xField];
                    dataNode.Values[yField] = row[yField];
                    dataNode.Series = seriesVal;
                    dataNode.Visibility = false;

                    CopyRtdIndexBaseField(dataNode.Values, xField, row);

                    nodes.Add(dataNode);
                    globalNodeIndex++;
                }
            }
        }
        else
        {
            if (!hideAllData)
            {
            // ===== LINE / SCATTER CHART =====
            // Track pixel positions per series for connecting lines
            var seriesPixelPositions = new Dictionary<string, List<(int col, int row)>>();
            foreach (var s in seriesNames)
                seriesPixelPositions[s] = new List<(int, int)>();

            // First pass: collect all data point positions for overlap detection (when symbols enabled)
            var dataPointPositions = new List<(int col, int row, string series, int seriesIndex)>();

            int dataPointIndex = 0;
            for (int i = 0; i < windowedCount; i++)
            {
                int absoluteIndex = effectiveStart + i;
                bool inRangeFilterDraw = opts.RangeFilterStart < 0 ||
                    (absoluteIndex >= opts.RangeFilterStart && absoluteIndex <= opts.RangeFilterEnd);
                if (!inRangeFilterDraw) continue;

                object xVal = windowedXValues[i];
                string xKey = xVal.ToString();
                int col;
                if (xIsQuantitative)
                {
                    float xNum = RTDLayout.GetNumericValue(xVal);
                    col = RTDLayout.MapValueToPixel(xNum, xMin, xMax, xPixelMin, xPixelMax);
                }
                else
                {
                    col = RTDLayout.MapIndexToPixel(i, windowedCount, xPixelMin, xPixelMax);
                }

                if (!xGrouped.ContainsKey(xKey)) continue;

                foreach (var row in xGrouped[xKey])
                {
                    if (!row.ContainsKey(yField)) continue;
                    if (colorField != null && !row.ContainsKey(colorField)) continue;

                    float yVal = RTDLayout.GetNumericValue(row[yField]);
                    string seriesVal = colorField != null ? row[colorField].ToString() : "_default";

                    // Y-filter
                    if (yVal < yMin || yVal > yMax) continue;

                    int pixelRow = RTDLayout.MapValueToPixel(yVal, yMin, yMax, yPixelMax, yPixelMin);
                    pixelRow = Math.Max(yPixelMin, Math.Min(yPixelMax, pixelRow));

                    // When symbols enabled, nudge center away from axes so full pattern fits
                    int drawCol = col;
                    int drawRow = pixelRow;
                    if (opts.UseSeriesSymbols)
                    {
                        (drawCol, drawRow) = RTDLayout.NudgeForSymbol(drawCol, drawRow, yPixelMin);
                    }

                    int seriesIdx = seriesNames.IndexOf(seriesVal);
                    if (seriesIdx == -1) continue; // hidden series
                    int originalSeriesIdx = seriesOriginalIndex.TryGetValue(seriesVal, out int osi) ? osi : seriesIdx;
                    dataPointPositions.Add((drawCol, drawRow, seriesVal, originalSeriesIdx));
                    seriesPixelPositions[seriesVal].Add((drawCol, drawRow));

                    dataPointIndex++;
                }
            }

            // Build overlap map: pixel → list of series indices at that pixel
            Dictionary<(int, int), List<int>> overlapMap = null;
            if (opts.UseSeriesSymbols)
            {
                overlapMap = new Dictionary<(int, int), List<int>>();
                foreach (var pos in dataPointPositions)
                {
                    var key = (pos.col, pos.row);
                    if (!overlapMap.ContainsKey(key))
                        overlapMap[key] = new List<int>();
                    if (!overlapMap[key].Contains(pos.seriesIndex))
                        overlapMap[key].Add(pos.seriesIndex);
                }
            }

            // Second pass: draw data points (with or without symbols)
            foreach (var pos in dataPointPositions)
            {
                if (opts.UseSeriesSymbols)
                {
                    var key = (pos.col, pos.row);
                    bool isOverlap = overlapMap[key].Count >= 2;
                    if (isOverlap)
                    {
                        RTDDrawing.DrawSymbol(grid, pos.col, pos.row, SYMBOL_OVERLAP);
                    }
                    else
                    {
                        int symIdx = GetSymbolIndex(pos.seriesIndex, opts);
                        RTDDrawing.DrawSymbol(grid, pos.col, pos.row, SERIES_SYMBOLS[symIdx]);
                    }
                }
                else
                {
                    grid[pos.row, pos.col] = DATA_MARKER;
                }
            }

            if (opts.UseSeriesSymbols)
            {
                int overlapCount = overlapMap != null ? overlapMap.Values.Count(v => v.Count >= 2) : 0;
                Debug.Log($"Symbols: {dataPointPositions.Count} points drawn with symbols, {overlapCount} overlaps detected");
            }

            // Draw connecting lines between consecutive same-series points (Bresenham)
            if (opts.DrawConnectingLines)
            {
                // Minimum distance between points to draw a connecting line
                // When symbolClearance > 0, skip lines between very close points
                int skipThreshold = opts.SymbolClearance > 0 ? 2 * (1 + opts.SymbolClearance) : 0;

                // When clearance is active, use per-series line markers (LINE_SERIES_BASE + seriesIdx)
                // so clearance only erases its own series' line pixels, not other series'.
                // After clearance, normalize all series markers back to LINE_MARKER.
                bool useSeriesMarkers = opts.SymbolClearance > 0;

                string lineStyle = "plain";
                foreach (var series in seriesNames)
                {
                    int seriesIdx = seriesNames.IndexOf(series);
                    var positions = seriesPixelPositions[series];

                    // Temporarily swap LINE_MARKER target for this series
                    int seriesLineMarker = useSeriesMarkers ? (LINE_SERIES_BASE + seriesIdx) : LINE_MARKER;

                    for (int i = 0; i < positions.Count - 1; i++)
                    {
                        // Skip line segment if endpoints are too close (clearance would erase it anyway)
                        if (skipThreshold > 0)
                        {
                            int dx = Math.Abs(positions[i + 1].col - positions[i].col);
                            int dy = Math.Abs(positions[i + 1].row - positions[i].row);
                            if (Math.Max(dx, dy) <= skipThreshold)
                                continue;
                        }

                        if (opts.UseSeriesLineThickness)
                        {
                            int thickness = SERIES_LINE_THICKNESSES[seriesIdx % SERIES_LINE_THICKNESSES.Length];
                            RTDDrawing.DrawThickBresenhamLine(grid, positions[i].col, positions[i].row, positions[i + 1].col, positions[i + 1].row, thickness, seriesLineMarker);
                            lineStyle = "thick";
                        }
                        else if (opts.UseSeriesLinePatterns)
                        {
                            bool[] pattern = SERIES_LINE_PATTERNS[seriesIdx % SERIES_LINE_PATTERNS.Length];
                            RTDDrawing.DrawPatternedBresenhamLine(grid, positions[i].col, positions[i].row, positions[i + 1].col, positions[i + 1].row, pattern, seriesLineMarker);
                            lineStyle = "patterned";
                        }
                        else
                        {
                            RTDDrawing.DrawBresenhamLine(grid, positions[i].col, positions[i].row, positions[i + 1].col, positions[i + 1].row, seriesLineMarker);
                        }
                    }
                }
                Debug.Log($"Drew connecting lines for {seriesNames.Count} series ({lineStyle})");

                // Clear line pixels around data point centers — only clears own series' lines
                if (opts.SymbolClearance > 0)
                {
                    foreach (var series in seriesNames)
                    {
                        int seriesIdx = seriesNames.IndexOf(series);
                        int seriesLineMarker = LINE_SERIES_BASE + seriesIdx;
                        var centers = seriesPixelPositions[series].Select(p => (p.col, p.row)).ToList();
                        RTDDrawing.ClearLineAroundPoints(grid, centers, opts.SymbolClearance, seriesLineMarker);
                    }

                    // Cross-series gap: erase 1px of other series' line pixels around each symbol center,
                    // skipping positions where the other series also has a symbol (overlap — already handled).
                    if (opts.UseSeriesSymbols)
                    {
                        foreach (var series in seriesNames)
                        {
                            var myCenters = seriesPixelPositions[series].Select(p => (p.col, p.row)).ToList();
                            foreach (var otherSeries in seriesNames)
                            {
                                if (otherSeries == series) continue;
                                int otherIdx = seriesNames.IndexOf(otherSeries);
                                int otherMarker = LINE_SERIES_BASE + otherIdx;
                                var otherCenterSet = seriesPixelPositions[otherSeries]
                                    .Select(p => (p.col, p.row)).ToHashSet();
                                var centersToErase = myCenters
                                    .Where(c => !otherCenterSet.Contains(c))   // skip overlap pixels
                                    .ToList();
                                RTDDrawing.ClearLineAroundPoints(grid, centersToErase, 1, otherMarker);
                            }
                        }
                    }

                    // Normalize remaining series line markers back to LINE_MARKER
                    for (int r = 0; r < GRID_HEIGHT; r++)
                        for (int c = 0; c < GRID_WIDTH; c++)
                            if (grid[r, c] >= LINE_SERIES_BASE)
                                grid[r, c] = LINE_MARKER;
                }
            }

            // Generate nodes for ALL data points (visible and hidden) with visibility flags
            int windowEnd = effectiveStart + effectiveSize;
            int globalNodeIndex = 0;

            // Build symbol lookup for visible points (when symbols enabled)
            Dictionary<(int col, int row, string series), string> symbolLookup = null;
            if (opts.UseSeriesSymbols && overlapMap != null)
            {
                symbolLookup = new Dictionary<(int, int, string), string>();
                foreach (var pos in dataPointPositions)
                {
                    var pixelKey = (pos.col, pos.row);
                    bool isOverlap = overlapMap[pixelKey].Count >= 2;
                    string symName = isOverlap ? "overlap" : SERIES_SYMBOL_NAMES[GetSymbolIndex(pos.seriesIndex, opts)];
                    var lookupKey = (pos.col, pos.row, pos.series);
                    if (!symbolLookup.ContainsKey(lookupKey))
                        symbolLookup[lookupKey] = symName;
                }
            }

            for (int xi = 0; xi < uniqueXCount; xi++)
            {
                object xVal = uniqueXValues[xi];
                string xKey = xVal.ToString();
                if (!xGrouped.ContainsKey(xKey)) continue;

                bool inXWindow = xi >= effectiveStart && xi < windowEnd;

                foreach (var row in xGrouped[xKey])
                {
                    if (!row.ContainsKey(yField)) continue;
                    if (colorField != null && !row.ContainsKey(colorField)) continue;

                    float yVal = RTDLayout.GetNumericValue(row[yField]);
                    string seriesVal = colorField != null ? row[colorField].ToString() : "_default";

                    bool isHiddenSeries = opts.HiddenSeries != null && opts.HiddenSeries.Contains(seriesVal);
                    bool inRangeFilter = opts.RangeFilterStart < 0 ||
                        (xi >= opts.RangeFilterStart && xi <= opts.RangeFilterEnd);
                    bool inYWindow = yVal >= yMin && yVal <= yMax;
                    bool isVisible = !isHiddenSeries && inRangeFilter && inXWindow && inYWindow;

                    bool isHiddenNode = isHiddenSeries || !inRangeFilter;
                    var dataNode = new ChartNode($"data-point-{globalNodeIndex}", isHiddenNode ? "data-hidden" : "data-point");
                    if (!isHiddenNode && colorField != null) dataNode.Values[colorField] = seriesVal;
                    dataNode.Values[xField] = row[xField];
                    dataNode.Values[yField] = row[yField];
                    dataNode.Series = seriesVal;

                    CopyRtdIndexBaseField(dataNode.Values, xField, row);

                    dataNode.Visibility = isVisible;

                    if (isVisible)
                    {
                        int col;
                        if (xIsQuantitative)
                        {
                            float xNum = RTDLayout.GetNumericValue(row[xField]);
                            col = RTDLayout.MapValueToPixel(xNum, xMin, xMax, xPixelMin, xPixelMax);
                        }
                        else
                        {
                            int windowIndex = xi - effectiveStart;
                            col = RTDLayout.MapIndexToPixel(windowIndex, effectiveSize, xPixelMin, xPixelMax);
                        }
                        int pixelRow = RTDLayout.MapValueToPixel(yVal, yMin, yMax, yPixelMax, yPixelMin);
                        pixelRow = Math.Max(yPixelMin, Math.Min(yPixelMax, pixelRow));

                        // Apply same axis nudging as drawing pass
                        if (opts.UseSeriesSymbols)
                        {
                            (col, pixelRow) = RTDLayout.NudgeForSymbol(col, pixelRow, yPixelMin);
                        }

                        dataNode.Coordinates.Add((col, pixelRow));

                        // Assign symbol name if symbols are enabled
                        if (opts.UseSeriesSymbols && symbolLookup != null)
                        {
                            var lookupKey = (col, pixelRow, seriesVal);
                            if (symbolLookup.TryGetValue(lookupKey, out string symName))
                                dataNode.Symbol = symName;
                        }
                    }

                    nodes.Add(dataNode);
                    globalNodeIndex++;
                }
            }
        } // end if (!hideAllData)
        }

        Debug.Log($"Multi-series: Generated {nodes.Count} total nodes: {nodes.Count(n => n.Type.Contains("x-axis"))} X-ticks, {nodes.Count(n => n.Type.Contains("y-axis"))} Y-ticks, {nodes.Count(n => n.Type == "data-point")} data points ({nodes.Count(n => n.Type == "data-point" && n.Visibility)} visible, {nodes.Count(n => n.Type == "data-point" && !n.Visibility)} hidden)");
        return (grid, nodes);
    }

    /// <summary>
    /// Draw grid with axes and data for current viewport.
    /// Returns both the grid and node position data.
    /// </summary>
    private static (int[,] grid, List<ChartNode> nodes) DrawGridWithViewport(
        VegaSpec spec,
        int[,] grid,
        List<(Dictionary<string, object> Data, int WindowIndex)> windowedData,
        List<(Dictionary<string, object> Data, int WindowIndex)> filteredData,
        List<(Dictionary<string, object> Data, int GlobalIndex)> fullData,
        int windowStart,
        int windowSize,
        string chartType,
        string xField,
        string yField,
        float yMin,
        float yMax,
        List<float> yTickValues,
        RenderOptions opts = null)
    {
        if (opts == null) opts = new RenderOptions();
        bool hideAllData = opts.HiddenSeries != null && opts.HiddenSeries.Contains("(all data)");
        Debug.Log($"DrawGridWithViewport() entry: spec.Encoding={spec.Encoding != null}, spec.Encoding.X={spec.Encoding?.X != null}, hideAllData={hideAllData}");
        // Track nodes (axis ticks, data points) for direct C# generation
        var nodes = new List<ChartNode>();

        var (xPixelMin, xPixelMax, yPixelMin, yPixelMax) = GetChartPixelBounds();

        var (hideYAxis, hideXAxis) = GetAxisVisibility(opts);
        int zeroLineRow = DrawAxesAndTicks(grid, nodes, yMin, yMax, yTickValues, yField, xPixelMax, yPixelMin, yPixelMax, hideYAxis, hideXAxis);

        // Draw data points and X-axis tick markers
        if (windowedData.Count > 0)
        {
            if (chartType == "bar")
            {
                // Compute bar width and inset pixel range for thick bars
                int singleBarWidth = opts.UseThickBars ? RTDLayout.CalculateBarWidth(windowSize) : 1;
                int singleHalfBar = singleBarWidth / 2;
                int singleBarXMin = xPixelMin + singleHalfBar;
                int singleBarXMax = xPixelMax - singleHalfBar;

                // Get X-axis tick values from Vega spec (if defined and numeric)
                // Note: Check for numeric tick values even if Type is ordinal/nominal,
                // since some specs declare ordinal but provide numeric ticks
                HashSet<float> tickValueSet = null;
                if (spec.Encoding?.X != null)
                {
                    var xAxisTickValues = spec.Encoding?.X?.Axis?.GetNumericValues();
                    if (xAxisTickValues != null && xAxisTickValues.Length > 0)
                    {
                        tickValueSet = new HashSet<float>(xAxisTickValues);
                        Debug.Log($"Using {xAxisTickValues.Length} X-axis ticks from spec: {string.Join(", ", xAxisTickValues)}");
                    }
                    else
                    {
                        Debug.Log($"No numeric X-axis tick values in spec (Type={spec.Encoding.X.Type}) - will draw ticks at all data points");
                    }
                }

                // FIRST: Draw X-tick markers for ALL points in X-window (before Y-filtering)
                // This ensures Z+1 and Z+2 have the same X-ticks
                int xTickIndex = 0;
                foreach (var item in windowedData)
                {
                    var point = item.Data;
                    int windowIndex = item.WindowIndex;

                    // Calculate X position based on WindowIndex
                    int col;
                    if (windowSize > 1)
                    {
                        col = RTDLayout.MapIndexToPixel(windowIndex, windowSize, singleBarXMin, singleBarXMax);
                    }
                    else
                    {
                        col = xPixelMin + (xPixelMax - xPixelMin) / 2;
                    }

                    // Draw X-tick marker only if this point's X-value is in the tick values
                    if (!hideXAxis && tickValueSet != null)
                    {
                        float xVal = RTDLayout.GetNumericValue(point[xField]);
                        if (tickValueSet.Contains(xVal))
                        {
                            AddXAxisTick(grid, nodes, col, xVal, xField, ref xTickIndex);
                            CopyRtdIndexBaseField(nodes[nodes.Count - 1].Values, xField, point);
                        }
                    }
                    // If no tick values specified, draw ticks at evenly-spaced intervals (not all points)
                    else
                    {
                        // Cap tick count to prevent solid blocks when there are many data points
                        const int MAX_X_TICKS = 10;
                        if (RTDLayout.ShouldDrawXTick(windowIndex, windowSize, MAX_X_TICKS))
                        {
                            AddXAxisTick(grid, nodes, col, point[xField], xField, ref xTickIndex);
                            CopyRtdIndexBaseField(nodes[nodes.Count - 1].Values, xField, point);
                        }
                    }
                }

                // SECOND: Draw data points only for Y-filtered points
                if (!hideAllData)
                {
                int dataPointIndex = 0;
                foreach (var item in filteredData)
                {
                    var point = item.Data;
                    int windowIndex = item.WindowIndex;
                    int absoluteIndex = windowStart + windowIndex;
                    bool inRangeFilterSingle = opts.RangeFilterStart < 0 ||
                        (absoluteIndex >= opts.RangeFilterStart && absoluteIndex <= opts.RangeFilterEnd);
                    if (!inRangeFilterSingle) continue;

                    int col;
                    if (windowSize > 1)
                        col = RTDLayout.MapIndexToPixel(windowIndex, windowSize, singleBarXMin, singleBarXMax);
                    else
                        col = xPixelMin + (xPixelMax - xPixelMin) / 2;

                    float yVal = RTDLayout.GetNumericValue(point[yField]);
                    if (yVal < yMin || yVal > yMax) continue;

                    int row = RTDLayout.MapValueToPixel(yVal, yMin, yMax, yPixelMax, yPixelMin);
                    row = Math.Max(yPixelMin, Math.Min(yPixelMax, row));

                    var dataNode = new ChartNode($"data-point-{dataPointIndex}", "data-point");
                    dataNode.Values[xField] = point[xField];
                    dataNode.Values[yField] = yVal;

                    var barCoords = RTDDrawing.DrawBar(grid, col, row, zeroLineRow, singleBarWidth);
                    dataNode.Coordinates.AddRange(barCoords);

                    nodes.Add(dataNode);
                    dataPointIndex++;
                }
                } // end if (!hideAllData)
            }
            else if (!hideAllData)
            {
                // Scatter plot: draw points within viewport
                foreach (var item in filteredData)
                {
                    var point = item.Data;
                    int windowIndex = item.WindowIndex;

                    float yVal = RTDLayout.GetNumericValue(point[yField]);

                    // Filter out points whose Y-value is outside viewport (safety check)
                    if (yVal < yMin || yVal > yMax)
                        continue;

                    // Use windowIndex for X positioning (preserves spacing)
                    int col = RTDLayout.MapIndexToPixel(windowIndex, windowSize, xPixelMin, xPixelMax);
                    int row = RTDLayout.MapValueToPixel(yVal, yMin, yMax, yPixelMax, yPixelMin);
                    row = Math.Max(yPixelMin, Math.Min(yPixelMax, row));


                    grid[row, col] = DATA_MARKER;
                }
            }
        }

        // Generate nodes for ALL data points (not just visible ones) with visibility flags
        // This allows navigation through the full dataset
        if (!hideAllData)
        {
        Debug.Log($"Creating nodes for ALL {fullData.Count} data points with visibility flags");

        var allDataNodes = new List<ChartNode>();
        int windowEnd = windowStart + windowSize;

        foreach (var item in fullData)
        {
            var point = item.Data;
            int globalIndex = item.GlobalIndex;

            float yVal = RTDLayout.GetNumericValue(point[yField]);

            // Determine if this point is in the current viewport
            bool inXWindow = globalIndex >= windowStart && globalIndex < windowEnd;
            bool inYWindow = yVal >= yMin && yVal <= yMax;
            bool isVisible = inXWindow && inYWindow;

            // Create node for ALL data points (visible and hidden)
            var dataNode = new ChartNode($"data-point-{globalIndex}", "data-point");
            dataNode.Values[xField] = point[xField];
            dataNode.Values[yField] = point[yField];  // Store original value to preserve precision for visibility matching

            CopyRtdIndexBaseField(dataNode.Values, xField, point);

            dataNode.Visibility = isVisible;

            // For visible points, calculate pixel coordinates
            if (isVisible)
            {
                // Calculate window-relative index for pixel mapping
                int windowIndex = globalIndex - windowStart;
                int col = RTDLayout.MapIndexToPixel(windowIndex, windowSize, xPixelMin, xPixelMax);
                int row = RTDLayout.MapValueToPixel(yVal, yMin, yMax, yPixelMax, yPixelMin);
                row = Math.Max(yPixelMin, Math.Min(yPixelMax, row));

                if (chartType == "bar")
                {
                    // Store all bar coordinates (matching DrawBar logic) for touch detection
                    int zRow = RTDLayout.MapValueToPixel(0, yMin, yMax, yPixelMax, yPixelMin);
                    zRow = Math.Max(yPixelMin, Math.Min(yPixelMax, zRow));
                    int nodeBarWidth = opts.UseThickBars ? RTDLayout.CalculateBarWidth(windowSize) : 1;
                    int halfW = nodeBarWidth / 2;
                    int drawCol = col;
                    if (nodeBarWidth > 1)
                    {
                        if (drawCol - halfW <= Y_AXIS_COL) drawCol = Y_AXIS_COL + halfW + 1;
                        if (drawCol + halfW > CHART_MAX_COL) drawCol = CHART_MAX_COL - halfW;
                    }
                    int rMin = Math.Min(row, zRow);
                    int rMax = Math.Max(row, zRow);
                    for (int dc = -halfW; dc <= halfW; dc++)
                        for (int br = rMin; br <= rMax; br++)
                            dataNode.Coordinates.Add((drawCol + dc, br));
                }
                else
                {
                    dataNode.Coordinates.Add((col, row));
                }
            }
            // For hidden points, no coordinates (not rendered on grid)

            allDataNodes.Add(dataNode);
        }

        // Replace the data-point nodes from filtered drawing with ALL data point nodes
        nodes.RemoveAll(n => n.Type == "data-point");
        nodes.AddRange(allDataNodes);
        } // end if (!hideAllData)

        Debug.Log($"Generated {nodes.Count} total nodes: {nodes.Count(n => n.Type.Contains("x-axis"))} X-ticks, {nodes.Count(n => n.Type.Contains("y-axis"))} Y-ticks, {nodes.Count(n => n.Type == "data-point")} data points ({nodes.Count(n => n.Type == "data-point" && n.Visibility)} visible, {nodes.Count(n => n.Type == "data-point" && !n.Visibility)} hidden)");
        return (grid, nodes);
    }

    // ===== Helper Methods =====

    private static List<float> ResolveYTickValues(VegaChannel yEncoding, float windowYMin, float windowYMax)
    {
        float[] yAxisTickValues = null;
        if (yEncoding != null && !yEncoding.IsCategorical())
        {
            yAxisTickValues = yEncoding.Axis?.GetNumericValues();
        }

        if (yAxisTickValues != null && yAxisTickValues.Length > 0)
        {
            var filtered = yAxisTickValues.Where(tick => tick >= windowYMin && tick <= windowYMax).ToList();
            if (filtered.Count > 0)
                return filtered;
        }

        return RTDLayout.GenerateNiceTicks(windowYMin, windowYMax, 6);
    }

    private static void CopyRtdIndexBaseField(Dictionary<string, object> nodeValues,
        string xField, Dictionary<string, object> sourceRow)
    {
        if (!xField.EndsWith("_rtd_index")) return;
        string baseField = xField.Substring(0, xField.Length - "_rtd_index".Length);
        if (sourceRow.ContainsKey(baseField))
            nodeValues[baseField] = sourceRow[baseField];
    }

    private static void CopyRtdIndexBaseField(Dictionary<string, object> nodeValues,
        string xField, object xVal,
        Dictionary<string, List<Dictionary<string, object>>> xGrouped)
    {
        if (!xField.EndsWith("_rtd_index")) return;
        string baseField = xField.Substring(0, xField.Length - "_rtd_index".Length);
        string xKey = xVal.ToString();
        if (xGrouped.ContainsKey(xKey) && xGrouped[xKey].Count > 0 && xGrouped[xKey][0].ContainsKey(baseField))
            nodeValues[baseField] = xGrouped[xKey][0][baseField];
    }
}
