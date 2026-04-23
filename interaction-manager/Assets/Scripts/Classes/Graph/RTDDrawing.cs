using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static RTDGridConstants;

/// <summary>
/// Pure grid-drawing operations for RTD rendering. Every method takes int[,] grid and writes marker values.
/// </summary>
public static class RTDDrawing
{
    /// <summary>
    /// Draw a line between two grid points using Bresenham's algorithm.
    /// Uses LINE_MARKER, does not overwrite DATA_MARKER or AXIS_MARKER pixels.
    /// </summary>
    public static void DrawBresenhamLine(int[,] grid, int x0, int y0, int x1, int y1, int marker = LINE_MARKER)
    {
        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;

        while (true)
        {
            // Only draw on background pixels (don't overwrite data points or axes)
            if (y0 >= 0 && y0 < GRID_HEIGHT && x0 >= 0 && x0 < GRID_WIDTH)
            {
                if (grid[y0, x0] == BACKGROUND)
                    grid[y0, x0] = marker;
            }

            if (x0 == x1 && y0 == y1) break;

            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>
    /// Draw a patterned line between two grid points using Bresenham's algorithm.
    /// The pattern array defines which pixels along the path are drawn (true) or skipped (false).
    /// </summary>
    public static void DrawPatternedBresenhamLine(int[,] grid, int x0, int y0, int x1, int y1, bool[] pattern, int marker = LINE_MARKER)
    {
        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        int step = 0;

        while (true)
        {
            if (y0 >= 0 && y0 < GRID_HEIGHT && x0 >= 0 && x0 < GRID_WIDTH)
            {
                if (grid[y0, x0] == BACKGROUND && pattern[step % pattern.Length])
                    grid[y0, x0] = marker;
            }

            if (x0 == x1 && y0 == y1) break;

            step++;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>
    /// Draw a thick line between two grid points by drawing parallel Bresenham lines.
    /// Thickness 1 = normal single line, 2 = line + 1 offset, 3 = line + 2 offsets.
    /// </summary>
    public static void DrawThickBresenhamLine(int[,] grid, int x0, int y0, int x1, int y1, int thickness, int marker = LINE_MARKER)
    {
        // Always draw the center line
        DrawBresenhamLine(grid, x0, y0, x1, y1, marker);

        if (thickness <= 1) return;

        // Determine dominant direction to choose perpendicular offset axis
        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        bool offsetVertically = dx >= dy;

        if (offsetVertically)
        {
            DrawBresenhamLine(grid, x0, y0 - 1, x1, y1 - 1, marker);
            if (thickness >= 3)
                DrawBresenhamLine(grid, x0, y0 + 1, x1, y1 + 1, marker);
        }
        else
        {
            DrawBresenhamLine(grid, x0 - 1, y0, x1 - 1, y1, marker);
            if (thickness >= 3)
                DrawBresenhamLine(grid, x0 + 1, y0, x1 + 1, y1, marker);
        }
    }

    /// <summary>
    /// Draw a bar of given width from rowStart to rowEnd, centered on col.
    /// Returns list of all (col, row) coordinates drawn (for ChartNode touch detection).
    /// Only writes to BACKGROUND cells (won't overwrite axes or other markers).
    /// </summary>
    public static List<(int col, int row)> DrawBar(int[,] grid, int col, int rowStart, int rowEnd, int barWidth, int fillPattern = 0)
    {
        var coords = new List<(int, int)>();
        int leftOff = (barWidth - 1) / 2;
        int rightOff = barWidth / 2;

        if (barWidth > 1)
        {
            // Nudge right if left edge would overlap Y-axis
            if (col - leftOff <= Y_AXIS_COL)
                col = Y_AXIS_COL + leftOff + 1;
            // Nudge left if right edge would go past chart area
            if (col + rightOff > CHART_MAX_COL)
                col = CHART_MAX_COL - rightOff;
        }

        int rMin = Math.Min(rowStart, rowEnd);
        int rMax = Math.Max(rowStart, rowEnd);
        int leftEdge = col - leftOff;
        for (int dc = -leftOff; dc <= rightOff; dc++)
        {
            int c = col + dc;
            if (c < 0 || c >= GRID_WIDTH) continue;
            for (int r = rMin; r <= rMax; r++)
            {
                if (r < 0 || r >= GRID_HEIGHT) continue;

                // Perimeter pixels always drawn solid; texture only on interior
                bool isPerimeter = (dc == -leftOff || dc == rightOff || r == rMin || r == rMax);
                bool draw = true;
                if (!isPerimeter && fillPattern != BAR_FILL_SOLID)
                {
                    if (fillPattern == BAR_FILL_VERTICAL)
                        draw = (c - leftEdge) % 2 == 0;
                    else if (fillPattern == BAR_FILL_HORIZONTAL)
                        draw = (r - rMin) % 2 == 0;
                    else if (fillPattern == BAR_FILL_CHECKERBOARD)
                        draw = (r + c) % 2 == 0;
                }

                if (draw && grid[r, c] == BACKGROUND)
                    grid[r, c] = DATA_MARKER;
                coords.Add((c, r));
            }
        }
        return coords;
    }

    /// <summary>
    /// Draw a tactile symbol pattern centered at (col, row).
    /// Only writes to BACKGROUND cells (won't overwrite axes or other markers).
    /// </summary>
    public static void DrawSymbol(int[,] grid, int col, int row, (int dx, int dy)[] pattern)
    {
        foreach (var (dx, dy) in pattern)
        {
            int c = col + dx;
            int r = row + dy;
            if (r >= 0 && r < GRID_HEIGHT && c >= 0 && c < GRID_WIDTH)
            {
                if (grid[r, c] == BACKGROUND || grid[r, c] == DATA_MARKER)
                    grid[r, c] = DATA_MARKER;
            }
        }
    }

    /// <summary>
    /// Clear LINE_MARKER pixels within a radius of each data point center.
    /// Creates tactile clearance so symbols/points stand out from connecting lines.
    /// </summary>
    public static void ClearLineAroundPoints(int[,] grid, List<(int col, int row)> centers, int clearance, int targetMarker = LINE_MARKER)
    {
        if (clearance <= 0) return;
        int symbolRadius = 1; // all symbols extend at most 1px from center
        foreach (var (cx, cy) in centers)
        {
            // Collect symbol pixels around this center
            var symbolPixels = new List<(int c, int r)>();
            for (int dc = -symbolRadius; dc <= symbolRadius; dc++)
            {
                for (int dr = -symbolRadius; dr <= symbolRadius; dr++)
                {
                    int sc = cx + dc;
                    int sr = cy + dr;
                    if (sr >= 0 && sr < GRID_HEIGHT && sc >= 0 && sc < GRID_WIDTH
                        && grid[sr, sc] == DATA_MARKER)
                    {
                        symbolPixels.Add((sc, sr));
                    }
                }
            }
            // If no symbol pixels found (e.g. symbols off), use center itself
            if (symbolPixels.Count == 0)
                symbolPixels.Add((cx, cy));

            // Clear target marker within clearance of each symbol pixel
            foreach (var (sc, sr) in symbolPixels)
            {
                for (int dc = -clearance; dc <= clearance; dc++)
                {
                    for (int dr = -clearance; dr <= clearance; dr++)
                    {
                        int c = sc + dc;
                        int r = sr + dr;
                        if (r >= 0 && r < GRID_HEIGHT && c >= 0 && c < GRID_WIDTH
                            && grid[r, c] == targetMarker)
                        {
                            grid[r, c] = BACKGROUND;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Draw X-axis tick marker at the given column (3 rows below X_AXIS_ROW, skipping ZERO_MARKER).
    /// </summary>
    public static void DrawXTickMarker(int[,] grid, int col)
    {
        for (int r = X_AXIS_ROW; r < X_AXIS_ROW + 3; r++)
        {
            if (r < GRID_HEIGHT && grid[r, col] != ZERO_MARKER)
                grid[r, col] = AXIS_MARKER;
        }
    }

    /// <summary>
    /// Draws X-axis, Y-axis, and zero-line on the grid. Returns the zero-line row.
    /// </summary>
    public static int DrawAxesAndZeroLine(int[,] grid, float yMin, float yMax,
        int xPixelMax, int yPixelMin, int yPixelMax, bool drawYAxis = true, bool drawXAxis = true)
    {
        int zeroLineRow = RTDLayout.MapValueToPixel(0, yMin, yMax, yPixelMax, yPixelMin);
        zeroLineRow = Math.Max(yPixelMin, Math.Min(yPixelMax, zeroLineRow));

        // Draw X-axis (from Y-axis through data area)
        if (drawXAxis)
            for (int c = Y_AXIS_COL + 1; c <= xPixelMax; c++)
                grid[X_AXIS_ROW, c] = AXIS_MARKER;

        // Draw Y-axis
        if (drawYAxis)
        {
            for (int r = yPixelMin; r <= X_AXIS_ROW; r++)
                grid[r, Y_AXIS_COL] = AXIS_MARKER;
        }

        // Draw zero-line if y=0 is in viewport
        if (drawXAxis && yMin <= 0 && yMax >= 0)
        {
            for (int c = Y_AXIS_COL + 1; c <= xPixelMax; c++)
            {
                if (grid[zeroLineRow, c] != AXIS_MARKER)
                    grid[zeroLineRow, c] = ZERO_MARKER;
            }
        }

        return zeroLineRow;
    }

    /// <summary>
    /// Draws Y-axis tick markers with uniform pixel spacing and creates ChartNodes for each tick.
    /// </summary>
    public static void DrawYAxisTicks(int[,] grid, List<ChartNode> nodes,
        List<float> yTickValues, string yField,
        float yMin, float yMax, int yPixelMin, int yPixelMax)
    {
        var filteredTicks = yTickValues.Where(t => t >= yMin && t <= yMax).ToList();
        int numYTicks = filteredTicks.Count;
        if (numYTicks > 1)
        {
            int yRange = yPixelMax - yPixelMin;
            int gap = yRange / (numYTicks - 1);
            for (int i = 0; i < numYTicks; i++)
            {
                int row = yPixelMax - i * gap;
                row = Math.Max(yPixelMin, Math.Min(yPixelMax, row));

                for (int c = 2; c < 5; c++)
                {
                    if (grid[row, c] != ZERO_MARKER)
                        grid[row, c] = AXIS_MARKER;
                }

                var yTickNode = new ChartNode($"y-axis-tick-{i}", "y-axis-tick");
                yTickNode.Coordinates.Add((Y_AXIS_COL, row));
                yTickNode.Values[yField] = filteredTicks[i];
                nodes.Add(yTickNode);
            }
        }
        else if (numYTicks == 1)
        {
            int row = RTDLayout.MapValueToPixel(filteredTicks[0], yMin, yMax, yPixelMax, yPixelMin);
            row = Math.Max(yPixelMin, Math.Min(yPixelMax, row));
            for (int c = 2; c < 5; c++)
            {
                if (grid[row, c] != ZERO_MARKER)
                    grid[row, c] = AXIS_MARKER;
            }
            var yTickNode = new ChartNode($"y-axis-tick-0", "y-axis-tick");
            yTickNode.Coordinates.Add((Y_AXIS_COL, row));
            yTickNode.Values[yField] = filteredTicks[0];
            nodes.Add(yTickNode);
        }
    }
}
