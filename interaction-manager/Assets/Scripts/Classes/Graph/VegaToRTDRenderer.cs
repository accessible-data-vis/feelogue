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

    public const int X_PIXEL_MIN = 6;
    public const int X_PIXEL_MAX_LIMIT = 58;   // last column available to data

    /// <summary>
    /// Fixed bounds for the chart data area: used when x is mapped by value (scatter with a
    /// quantitative x) or when even spacing would leave too much blank space.
    /// </summary>
    private static (int xPixelMin, int xPixelMax, int yPixelMin, int yPixelMax) GetChartPixelBounds()
    {
        return (X_PIXEL_MIN, 55, 0, 36);
    }

    /// <summary>
    /// Bounds for index-mapped x (categorical/temporal). The right edge moves in so every gap
    /// is the same width. Falls back to the fixed bounds when the blank space on the right
    /// would be wider than two gaps.
    /// </summary>
    private static (int xPixelMin, int xPixelMax, int yPixelMin, int yPixelMax) GetChartPixelBounds(int visiblePointCount)
    {
        if (visiblePointCount < 2) return GetChartPixelBounds();
        int gap = (X_PIXEL_MAX_LIMIT - X_PIXEL_MIN) / (visiblePointCount - 1);
        if (gap < 1) return GetChartPixelBounds();   // more points than columns
        int xPixelMax = X_PIXEL_MIN + gap * (visiblePointCount - 1);
        if (X_PIXEL_MAX_LIMIT - xPixelMax > 2 * gap) return GetChartPixelBounds();
        return (X_PIXEL_MIN, xPixelMax, 0, 36);
    }

    /// <summary>Bounds for bars: from the left of the data area to the last bar's right edge.</summary>
    private static (int xPixelMin, int xPixelMax, int yPixelMin, int yPixelMax) GetBarPixelBounds(int barCount)
    {
        int n = Math.Max(1, barCount);
        int rightEdge = RTDLayout.BarCenter(n - 1, n) + RTDLayout.BarWidth(n) / 2;
        return (X_PIXEL_MIN, rightEdge, 0, 36);
    }

    /// <summary>
    /// A series' bar texture: the one the spec's usermeta names for it, else its place in
    /// the stack order. Solid when textures are off.
    /// </summary>
    public static int BarTexture(VegaSpec spec, string series, List<string> stackOrder, bool useTextures)
    {
        if (!useTextures) return BAR_FILL_SOLID;
        var named = spec.Usermeta?.Textures;
        if (named != null && named.TryGetValue(series, out var name) && TryParseTexture(name, out int pattern))
            return pattern;
        return Math.Max(0, stackOrder.IndexOf(series)) % BAR_FILL_PATTERN_COUNT;
    }

    /// <summary>
    /// Bar stack order, bottom to top: the colour scale's domain reversed when the spec
    /// gives one, else reverse-alphabetical, as Vega-Lite stacks. A series' place in it
    /// also picks its texture.
    /// </summary>
    public static List<string> BarStackOrder(VegaSpec spec, List<string> series)
    {
        var colorDomain = spec.Encoding?.GetColorStringDomain();
        if (colorDomain == null || colorDomain.Count == 0)
            return series.OrderByDescending(s => s).ToList();
        var ordered = Enumerable.Reverse(colorDomain).Where(series.Contains).ToList();
        ordered.AddRange(series.Where(s => !ordered.Contains(s)).OrderByDescending(s => s));
        return ordered;
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
    /// Generate RTD grid from Vega-Lite spec with window parameters.
    /// Returns both the grid and node position data.
    /// </summary>
    public static (int[,] grid, List<ChartNode> nodes) Generate(VegaSpec spec, int windowStart, int windowSize, float windowYMin, float windowYMax, RenderOptions opts = null)
    {
        if (opts == null) opts = new RenderOptions();

        // Initialize empty grid
        int[,] grid = new int[GRID_HEIGHT, GRID_WIDTH];

        // Get chart type
        string chartType = spec.GetMarkType();
        AppLog.Detail(LogArea.Render, $"Generating {chartType} chart with window: X=[{windowStart}, {windowStart + windowSize - 1}], Y=[{windowYMin:F2}, {windowYMax:F2}]");
        AppLog.Detail(LogArea.Render, $"Generate() entry: spec.Encoding={spec.Encoding != null}, spec.Encoding.X={spec.Encoding?.X != null}, spec.Encoding.Y={spec.Encoding?.Y != null}");

        // Get encodings
        var xEncoding = spec.Encoding.X;
        var yEncoding = spec.Encoding.Y;
        string xField = xEncoding.Field;
        string yField = yEncoding.Field;

        // Check for multi-series (color field grouping)
        string colorField = spec.Encoding.GetColorField();
        if (colorField != null)
        {
            AppLog.Detail(LogArea.Render, $"Multi-series chart detected: color field = '{colorField}'");
            return GenerateMultiSeries(spec, grid, windowStart, windowSize, windowYMin, windowYMax, chartType, xField, yField, colorField, opts);
        }

        // Single-series lines and bars share the multi-series code (one series)
        if (chartType == "line" || chartType == "bar")
            return GenerateMultiSeries(spec, grid, windowStart, windowSize, windowYMin, windowYMax, chartType, xField, yField, null, opts);

        // Single-series scatter path
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

        AppLog.Detail(LogArea.Render, $"Windowed to {windowedData.Count} points, filtered to {filteredData.Count} visible in Y-range");

        List<float> yTickValues = ResolveYTickValues(yEncoding, windowYMin, windowYMax);

        // Map data with the window bounds, not the tick values (ticks are only drawn)
        float yMin = windowYMin;
        float yMax = windowYMax;

        // Draw axes and data (implementation continues below...)
        // Convert anonymous type to tuple for method signature
        var windowedDataTuples = windowedDataWithIndices.Select(item => (item.Data, item.WindowIndex)).ToList();
        var filteredDataTuples = filteredData.Select(item => (item.Data, item.WindowIndex)).ToList();

        // Pass full dataset with global indices for creating all nodes
        var fullDataWithIndices = fullData.Select((d, i) => (d, i)).ToList();

        return DrawGridWithWindow(spec, grid, windowedDataTuples, filteredDataTuples, fullDataWithIndices, windowStart, windowSize, chartType, xField, yField, yMin, yMax, yTickValues, opts);
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
        AppLog.Detail(LogArea.Render, $"Multi-series: {uniqueXCount} unique X values, windowStart={windowStart}, windowSize={windowSize}");

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
        AppLog.Detail(LogArea.Render, $"Multi-series: {seriesNames.Count} series: [{string.Join(", ", seriesNames)}]");

        // Capture original (pre-filter) index so symbols stay stable when series are hidden
        var allSeriesNames = seriesNames.ToList(); // copy before filtering
        var seriesOriginalIndex = new Dictionary<string, int>();
        for (int i = 0; i < allSeriesNames.Count; i++)
            seriesOriginalIndex[allSeriesNames[i]] = i;

        // Filter out hidden series
        if (opts.HiddenSeries != null && opts.HiddenSeries.Count > 0)
        {
            seriesNames = seriesNames.Where(s => !opts.HiddenSeries.Contains(s)).ToList();
            AppLog.Detail(LogArea.Render, $"After filtering hidden series: {seriesNames.Count} visible: [{string.Join(", ", seriesNames)}]");
        }

        // Generate Y-axis ticks
        var yEncoding = spec.Encoding.Y;
        List<float> yTickValues = ResolveYTickValues(yEncoding, windowYMin, windowYMax);

        float yMin = windowYMin;
        float yMax = windowYMax;

        // Scatter with a quantitative X maps by value, not index; its uneven gaps are data.
        bool xIsQuantitative = chartType == "point" && !spec.Encoding.X.IsCategorical();

        var (xPixelMin, xPixelMax, yPixelMin, yPixelMax) = xIsQuantitative
            ? GetChartPixelBounds()
            : chartType == "bar"
                ? GetBarPixelBounds(windowedXValues.Count)
                : GetChartPixelBounds(windowedXValues.Count);

        // For scatter plots with quantitative X: compute xMin/xMax and nice tick values
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
            AppLog.Detail(LogArea.Render, $"Scatter X-axis: nice domain=[{xMin}, {xMax}], {xTickValues.Count} nice ticks: [{string.Join(", ", xTickValues)}]");
        }

        var (hideYAxisMulti, hideXAxisMulti) = GetAxisVisibility(opts);
        bool hideAllData = opts.HiddenSeries != null && opts.HiddenSeries.Contains("(all data)");
        int zeroLineRow = DrawAxesAndTicks(grid, nodes, yMin, yMax, yTickValues, yField, xPixelMax, yPixelMin, yPixelMax, hideYAxisMulti, hideXAxisMulti);

        // Draw X-axis ticks
        int windowedCount = windowedXValues.Count;
        int barWidth = RTDLayout.BarWidth(windowedCount);
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
                int col = chartType == "bar"
                    ? RTDLayout.BarCenter(i, windowedCount)
                    : RTDLayout.MapIndexToPixel(i, windowedCount, xPixelMin, xPixelMax);
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

        // Bar chart path (a single series is a stack of one) vs line/scatter path
        if (chartType == "bar")
        {
            // ===== Bar chart =====
            // Stack order and textures come from the whole series list, so a series keeps
            // its place in the order and its texture when others are hidden.
            var fullStackOrder = BarStackOrder(spec, allSeriesNames);
            var stackOrder = fullStackOrder.Where(s => seriesNames.Contains(s)).ToList();
            AppLog.Detail(LogArea.Render, $"Bar chart: {seriesNames.Count} series, {windowedCount} X positions, barWidth={barWidth}, stack order (bottom→top): [{string.Join(", ", stackOrder)}]");

            int globalNodeIndex = 0;
            int windowEnd = effectiveStart + effectiveSize;
            int RowOf(float v) =>
                Math.Max(yPixelMin, Math.Min(yPixelMax, RTDLayout.MapValueToPixel(v, yMin, yMax, yPixelMax, yPixelMin)));

            if (!hideAllData)
            {
            // Drawing + node creation pass for windowed X positions
            for (int i = 0; i < windowedCount; i++)
            {
                object xVal = windowedXValues[i];
                string xKey = xVal.ToString();
                int col = RTDLayout.BarCenter(i, windowedCount);

                if (!xGrouped.ContainsKey(xKey)) continue;

                // This bar's segments, bottom first
                var seriesValues = new List<(string seriesName, float value, Dictionary<string, object> rowData)>();
                foreach (var seriesName in stackOrder)
                {
                    var matchingRow = xGrouped[xKey].FirstOrDefault(r => r.ContainsKey(yField) &&
                        (colorField == null || (r.ContainsKey(colorField) && r[colorField].ToString() == seriesName)));
                    float value = matchingRow != null ? RTDLayout.GetNumericValue(matchingRow[yField]) : 0f;
                    if (value != 0f)   // a zero segment draws nothing, and takes no gap
                        seriesValues.Add((seriesName, value, matchingRow));
                }

                // Segments start one row off the zero line, which is drawn and is never part
                // of a bar. Edges sit on the running totals, so the bar's top is at its total;
                // the 1-pin gap between two segments comes out of the taller one, and when
                // neither can spare a row the segments above move up one. Every segment
                // keeps at least one row. A lone segment runs from the zero line to its
                // value, downward when negative.
                var bottoms = new int[seriesValues.Count];
                var tops = new int[seriesValues.Count];
                if (seriesValues.Count == 1 && seriesValues[0].value < 0)
                {
                    tops[0] = zeroLineRow + 1;
                    bottoms[0] = Math.Min(Math.Max(RowOf(seriesValues[0].value), tops[0]), X_AXIS_ROW - 1);
                }
                else if (seriesValues.Count > 0)
                {
                    float runningTotal = 0f;
                    for (int si = 0; si < seriesValues.Count; si++)
                    {
                        runningTotal += Math.Abs(seriesValues[si].value);
                        bottoms[si] = si == 0 ? zeroLineRow - 1 : tops[si - 1] - 1;
                        tops[si] = Math.Min(RowOf(runningTotal), bottoms[si]);
                    }
                    int Rows(int si) => bottoms[si] - tops[si] + 1;
                    for (int si = 1; si < seriesValues.Count; si++)
                    {
                        if (Rows(si - 1) >= Rows(si) && Rows(si - 1) >= 2) tops[si - 1]++;
                        else if (Rows(si) >= 2) bottoms[si]--;
                        else
                            for (int sj = si; sj < seriesValues.Count; sj++) { tops[sj]--; bottoms[sj]--; }
                    }
                }

                for (int si = 0; si < seriesValues.Count; si++)
                {
                    var (seriesName, value, rowData) = seriesValues[si];
                    int bottomRow = bottoms[si], topRow = Math.Max(yPixelMin, tops[si]);
                    if (bottomRow < yPixelMin || topRow > bottomRow) continue;   // no room on the display

                    int fillPattern = BarTexture(spec, seriesName, fullStackOrder, opts.UseBarTextures);
                    var barCoords = RTDDrawing.DrawBar(grid, col, topRow, bottomRow, barWidth, fillPattern);

                    // Labels read x, series, value: "Q1, Software, 120 thousand dollars"
                    var dataNode = new ChartNode($"data-point-{globalNodeIndex}", "data-point");
                    CopyLabelFields(dataNode.Values, spec, rowData, xField, yField, colorField);
                    dataNode.Values[xField] = rowData[xField];
                    if (colorField != null) dataNode.Values[colorField] = seriesName;
                    dataNode.Values[yField] = rowData[yField];
                    CopyRowId(dataNode.Values, rowData);
                    dataNode.Series = seriesName;
                    dataNode.Visibility = true;
                    dataNode.Coordinates.AddRange(barCoords);

                    CopyRtdIndexBaseField(dataNode.Values, xField, rowData);

                    nodes.Add(dataNode);
                    globalNodeIndex++;
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
                    if (!row.ContainsKey(yField) || (colorField != null && !row.ContainsKey(colorField))) continue;

                    string seriesVal = colorField != null ? row[colorField].ToString() : "_default";
                    var dataNode = new ChartNode($"data-point-{globalNodeIndex}", "data-point");
                    if (colorField != null) dataNode.Values[colorField] = seriesVal;
                    dataNode.Values[xField] = row[xField];
                    dataNode.Values[yField] = row[yField];
                    CopyRowId(dataNode.Values, row);
                    dataNode.Series = seriesVal;
                    dataNode.Visibility = false;

                    CopyRtdIndexBaseField(dataNode.Values, xField, row);

                    nodes.Add(dataNode);
                    globalNodeIndex++;
                }
            }
            } // end if (!hideAllData)
        }
        else
        {
            if (!hideAllData)
            {
            // ===== Line / scatter chart =====
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
                AppLog.Detail(LogArea.Render, $"Symbols: {dataPointPositions.Count} points drawn with symbols, {overlapCount} overlaps detected");
            }

            // Draw connecting lines between consecutive same-series points (Bresenham).
            // Never on a scatterplot: its points are not a sequence.
            if (opts.DrawConnectingLines && chartType != "point")
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
                AppLog.Detail(LogArea.Render, $"Drew connecting lines for {seriesNames.Count} series ({lineStyle})");

                // Clear line pixels around data point centers (own series' lines only)
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
                    // skipping positions where the other series also has a symbol (overlaps are handled already).
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

            // Generate nodes for all data points (visible and hidden) with visibility flags
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
                    if (!isHiddenNode) CopyLabelFields(dataNode.Values, spec, row, xField, yField, colorField);
                    // Labels read x, series, value ("February 2025, Memory, $190"); a scatterplot
                    // point names its group before its two values.
                    if (chartType != "point") dataNode.Values[xField] = row[xField];
                    if (!isHiddenNode && colorField != null) dataNode.Values[colorField] = seriesVal;
                    dataNode.Values[xField] = row[xField];
                    dataNode.Values[yField] = row[yField];
                    CopyRowId(dataNode.Values, row);
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

        AppLog.Detail(LogArea.Render, $"Multi-series: Generated {nodes.Count} total nodes: {nodes.Count(n => n.Type.Contains("x-axis"))} X-ticks, {nodes.Count(n => n.Type.Contains("y-axis"))} Y-ticks, {nodes.Count(n => n.Type == "data-point")} data points ({nodes.Count(n => n.Type == "data-point" && n.Visibility)} visible, {nodes.Count(n => n.Type == "data-point" && !n.Visibility)} hidden)");
        return (grid, nodes);
    }

    /// <summary>
    /// Draw grid with axes and data for the current window.
    /// Returns both the grid and node position data.
    /// </summary>
    private static (int[,] grid, List<ChartNode> nodes) DrawGridWithWindow(
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
        AppLog.Detail(LogArea.Render, $"DrawGridWithWindow() entry: spec.Encoding={spec.Encoding != null}, spec.Encoding.X={spec.Encoding?.X != null}, hideAllData={hideAllData}");
        // Track nodes (axis ticks, data points) for direct C# generation
        var nodes = new List<ChartNode>();

        var (xPixelMin, xPixelMax, yPixelMin, yPixelMax) =
            (chartType == "point" && !spec.Encoding.X.IsCategorical())
                ? GetChartPixelBounds()
                : GetChartPixelBounds(windowSize);

        var (hideYAxis, hideXAxis) = GetAxisVisibility(opts);
        int zeroLineRow = DrawAxesAndTicks(grid, nodes, yMin, yMax, yTickValues, yField, xPixelMax, yPixelMin, yPixelMax, hideYAxis, hideXAxis);

        // Draw data points and X-axis tick markers
        if (windowedData.Count > 0)
        {
            if (!hideAllData)
            {
                // Scatter plot: draw points within the window
                foreach (var item in filteredData)
                {
                    var point = item.Data;
                    int windowIndex = item.WindowIndex;

                    float yVal = RTDLayout.GetNumericValue(point[yField]);

                    // Filter out points whose Y-value is outside the window (safety check)
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

        // Generate nodes for all data points (not just visible ones) with visibility flags
        // This allows navigation through the full dataset
        if (!hideAllData)
        {
        AppLog.Detail(LogArea.Render, $"Creating nodes for ALL {fullData.Count} data points with visibility flags");

        var allDataNodes = new List<ChartNode>();
        int windowEnd = windowStart + windowSize;

        foreach (var item in fullData)
        {
            var point = item.Data;
            int globalIndex = item.GlobalIndex;

            float yVal = RTDLayout.GetNumericValue(point[yField]);

            // Determine if this point is in the current window
            bool inXWindow = globalIndex >= windowStart && globalIndex < windowEnd;
            bool inYWindow = yVal >= yMin && yVal <= yMax;
            bool isVisible = inXWindow && inYWindow;

            // Create a node for every data point (visible and hidden)
            var dataNode = new ChartNode($"data-point-{globalIndex}", "data-point");
            CopyLabelFields(dataNode.Values, spec, point, xField, yField);
            dataNode.Values[xField] = point[xField];
            dataNode.Values[yField] = point[yField];  // Store original value to preserve precision for visibility matching
            CopyRowId(dataNode.Values, point);

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

                dataNode.Coordinates.Add((col, row));
            }
            // For hidden points, no coordinates (not rendered on grid)

            allDataNodes.Add(dataNode);
        }

        // Replace the data-point nodes from the filtered drawing with all data point nodes
        nodes.RemoveAll(n => n.Type == "data-point");
        nodes.AddRange(allDataNodes);
        } // end if (!hideAllData)

        AppLog.Detail(LogArea.Render, $"Generated {nodes.Count} total nodes: {nodes.Count(n => n.Type.Contains("x-axis"))} X-ticks, {nodes.Count(n => n.Type.Contains("y-axis"))} Y-ticks, {nodes.Count(n => n.Type == "data-point")} data points ({nodes.Count(n => n.Type == "data-point" && n.Visibility)} visible, {nodes.Count(n => n.Type == "data-point" && !n.Visibility)} hidden)");
        return (grid, nodes);
    }

    // ===== Helper Methods =====

    public static List<float> ResolveYTickValues(VegaChannel yEncoding, float windowYMin, float windowYMax)
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

    /// <summary>
    /// Copy the fields the spec's tooltip names, other than the plotted ones (a
    /// scatterplot point's name, say). They go in first, so the point's label starts
    /// with them.
    /// </summary>
    private static void CopyLabelFields(Dictionary<string, object> nodeValues, VegaSpec spec,
        Dictionary<string, object> sourceRow, params string[] plotted)
    {
        foreach (var field in spec.Encoding.GetTooltipFields())
            if (Array.IndexOf(plotted, field) < 0 && sourceRow.TryGetValue(field, out var value))
                nodeValues[field] = value;
    }

    /// <summary>Copy the row id onto the pin so agent highlights can find it.</summary>
    private static void CopyRowId(Dictionary<string, object> nodeValues, Dictionary<string, object> sourceRow)
    {
        if (sourceRow != null && sourceRow.TryGetValue(VegaChartLoader.RowIdField, out var id))
            nodeValues[VegaChartLoader.RowIdField] = id;
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
