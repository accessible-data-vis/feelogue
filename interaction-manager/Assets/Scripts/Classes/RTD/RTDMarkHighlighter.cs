using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Static helper for data-mark highlight geometry.
/// Handles bar perimeter/interior computation and symbol pin pattern lookup.
/// Replaces RTDBarHighlighter.
/// </summary>
public static class RTDMarkHighlighter
{
    // ===== Unified Entry Point =====

    /// <summary>
    /// Returns mark-aware highlight pixels for the given position, or null if no
    /// mark-specific geometry applies (caller should use its generic shape fallback).
    /// Bar charts → perimeter pixels (thick) or dashed column (thin).
    /// Symbol charts → symbol pin pixels.
    /// </summary>
    public static List<Vector2Int> GetMarkNeighbours(int x, int y, string chartType, InterfaceGraphVisualizer graphVisualizer)
    {
        if (chartType == RTDConstants.CHART_TYPE_BAR)
            return GetBarNeighbours(x, y, graphVisualizer);

        return GetSymbolNeighbours(x, y, graphVisualizer);
    }

    // ===== Bar Geometry =====

    /// <summary>
    /// Returns all bar pixel coordinates for the node at (x, y), or null if none.
    /// </summary>
    public static List<Vector2Int> GetBarCoords(int x, int y, InterfaceGraphVisualizer graphVisualizer)
    {
        var matching = graphVisualizer.GetMatchingNodes(new HashSet<Vector2Int> { new Vector2Int(x, y) });
        var node = matching.FirstOrDefault(n => n.id.StartsWith("data-point"))
                ?? matching.FirstOrDefault();

        if (node?.barCoordinates == null || node.barCoordinates.Count == 0)
            return null;

        return node.barCoordinates.Select(c => new Vector2Int(c.x, c.y)).ToList();
    }

    /// <summary>
    /// Returns the interior pixels of a bar (barCoords minus perimeter).
    /// Returns empty list for thin bars (no interior).
    /// </summary>
    public static List<Vector2Int> GetBarInterior(List<Vector2Int> barCoords)
    {
        var perimeter = GetBarPerimeter(barCoords);
        if (perimeter == null || perimeter.Count == 0)
            return new List<Vector2Int>();

        var perimeterSet = new HashSet<Vector2Int>(perimeter);
        return barCoords.Where(p => !perimeterSet.Contains(p)).ToList();
    }

    /// <summary>
    /// Returns the perimeter pixels of a bar, or null if the bar is thin (fewer than 3 columns).
    /// </summary>
    public static List<Vector2Int> GetBarPerimeter(List<Vector2Int> barCoords)
    {
        if (barCoords == null || barCoords.Count == 0) return null;

        var bounds = GetBounds(barCoords);
        if (!IsThickBar(bounds)) return null;

        return barCoords.Where(p =>
            p.x == bounds.minCol || p.x == bounds.maxCol ||
            p.y == bounds.minRow || p.y == bounds.maxRow).ToList();
    }

    /// <summary>
    /// Returns true if the bar is thick (3+ columns wide).
    /// </summary>
    public static bool IsThickBar(List<Vector2Int> barCoords)
    {
        if (barCoords == null || barCoords.Count == 0) return false;
        return IsThickBar(GetBounds(barCoords));
    }

    public static BarBounds GetBounds(List<Vector2Int> coords)
    {
        var b = new BarBounds
        {
            minCol = int.MaxValue, maxCol = int.MinValue,
            minRow = int.MaxValue, maxRow = int.MinValue
        };
        foreach (var p in coords)
        {
            if (p.x < b.minCol) b.minCol = p.x;
            if (p.x > b.maxCol) b.maxCol = p.x;
            if (p.y < b.minRow) b.minRow = p.y;
            if (p.y > b.maxRow) b.maxRow = p.y;
        }
        return b;
    }

    public struct BarBounds
    {
        public int minCol, maxCol, minRow, maxRow;
    }

    // ===== Symbol Geometry =====

    /// <summary>
    /// Returns the symbol pin pixels for the node at (x, y), or null if the node
    /// has no symbol (not a symbol chart, or symbol name unrecognised).
    /// </summary>
    public static List<Vector2Int> GetSymbolNeighbours(int x, int y, InterfaceGraphVisualizer graphVisualizer)
    {
        // Exact match first
        NodeComponent node = FindSymbolNode(x, y, graphVisualizer, radius: 0);

        // Tolerance fallback: symbol centers may be nudged ±1–2px from touch coords
        if (node == null)
            node = FindSymbolNode(x, y, graphVisualizer, radius: 2);

        if (node != null)
            Debug.Log($"[GetSymbolNeighbours] ({x},{y}): id='{node.id}', symbol='{node.symbol ?? "null"}'");

        if (node == null || string.IsNullOrEmpty(node.symbol))
            return null;

        var pattern = GetSymbolPattern(node.symbol);
        if (pattern == null) return null;

        // Emit pattern relative to the node's actual center, not the raw touch coord
        int cx = node.xy[0], cy = node.xy[1];
        var result = new List<Vector2Int>();
        foreach (var (dx, dy) in pattern)
            TryAdd(cx + dx, cy + dy, result);
        return result;
    }

    private static NodeComponent FindSymbolNode(int x, int y, InterfaceGraphVisualizer graphVisualizer, int radius)
    {
        var search = new HashSet<Vector2Int>();
        for (int dx = -radius; dx <= radius; dx++)
            for (int dy = -radius; dy <= radius; dy++)
                search.Add(new Vector2Int(x + dx, y + dy));

        var matching = graphVisualizer.GetMatchingNodes(search);
        return matching.FirstOrDefault(n => !string.IsNullOrEmpty(n.symbol))
            ?? (radius == 0 ? matching.FirstOrDefault(n => n.id.StartsWith("data-point"))
                              ?? matching.FirstOrDefault()
                            : null);
    }

    private static (int dx, int dy)[] GetSymbolPattern(string symbolName)
    {
        for (int i = 0; i < RTDGridConstants.SERIES_SYMBOL_NAMES.Length; i++)
            if (RTDGridConstants.SERIES_SYMBOL_NAMES[i] == symbolName)
                return RTDGridConstants.SERIES_SYMBOLS[i];

        if (symbolName == "overlap")
            return RTDGridConstants.SYMBOL_OVERLAP;

        return null;
    }

    /// <summary>
    /// Fallback for when no symbol is stored on the node: finds the nearest
    /// data-point within radius=3 and returns the appropriate symbol pattern.
    /// Respects seriesSymbolOverrides for series 0 (single-series charts).
    /// Only called when useSeriesSymbols=true on a non-bar chart.
    /// </summary>
    public static List<Vector2Int> GetFallbackSymbolPattern(
        int x, int y, InterfaceGraphVisualizer graphVisualizer,
        RTDGridConstants.SymbolType[] seriesSymbolOverrides = null)
    {
        var search = new HashSet<Vector2Int>();
        for (int dx = -3; dx <= 3; dx++)
            for (int dy = -3; dy <= 3; dy++)
                search.Add(new Vector2Int(x + dx, y + dy));

        var matching = graphVisualizer.GetMatchingNodes(search);
        var node = matching.FirstOrDefault(n => n.id != null && n.id.StartsWith("data-point"))
                 ?? matching.FirstOrDefault();

        if (node == null || node.xy == null || node.xy.Length < 2) return null;

        // Determine series index: null/empty series = single-series chart = 0
        int seriesIdx = string.IsNullOrEmpty(node.series) ? 0 : 0;

        // Use the node's own symbol pattern if set (multi-series path sets this)
        (int dx, int dy)[] pattern = null;
        if (!string.IsNullOrEmpty(node.symbol))
        {
            for (int i = 0; i < RTDGridConstants.SERIES_SYMBOL_NAMES.Length; i++)
                if (RTDGridConstants.SERIES_SYMBOL_NAMES[i] == node.symbol)
                    { pattern = RTDGridConstants.SERIES_SYMBOLS[i]; break; }
            if (pattern == null && node.symbol == "overlap")
                pattern = RTDGridConstants.SYMBOL_OVERLAP;
        }

        // Fallback: use configured override for seriesIdx, else default SERIES_SYMBOLS[seriesIdx]
        if (pattern == null)
        {
            int symIdx = seriesIdx;
            if (seriesSymbolOverrides != null && seriesIdx < seriesSymbolOverrides.Length
                && seriesSymbolOverrides[seriesIdx] != RTDGridConstants.SymbolType.Default)
                symIdx = (int)seriesSymbolOverrides[seriesIdx];
            symIdx = symIdx % RTDGridConstants.SERIES_SYMBOLS.Length;
            pattern = RTDGridConstants.SERIES_SYMBOLS[symIdx];
        }

        var result = new List<Vector2Int>();
        foreach (var (ddx, ddy) in pattern)
            TryAdd(node.xy[0] + ddx, node.xy[1] + ddy, result);

        return result.Count > 0 ? result : null;
    }

    // ===== Grid Utility =====

    internal static void TryAdd(int nx, int ny, List<Vector2Int> list)
    {
        if (nx >= 0 && nx < RTDConstants.PIXEL_COLS && ny >= 0 && ny < RTDConstants.PIXEL_ROWS)
            list.Add(new Vector2Int(nx, ny));
    }

    // ===== Private Helpers =====

    private static List<Vector2Int> GetBarNeighbours(int x, int y, InterfaceGraphVisualizer graphVisualizer)
    {
        var result = new List<Vector2Int>();
        var barCoords = GetBarCoords(x, y, graphVisualizer);

        if (barCoords == null || barCoords.Count == 0)
            return result;

        var bounds = GetBounds(barCoords);
        bool isThick = IsThickBar(bounds);

        Debug.Log($"[MarkHighlight] Bar at ({x},{y}): isThick={isThick}, cols={bounds.minCol}-{bounds.maxCol}, rows={bounds.minRow}-{bounds.maxRow}");

        if (isThick)
        {
            var perimeter = GetBarPerimeter(barCoords);
            foreach (var p in perimeter)
                TryAdd(p.x, p.y, result);
        }
        else
        {
            // Thin bar: dashed pattern (every other pixel along the column)
            for (int i = 1; i < barCoords.Count; i += 2)
                TryAdd(barCoords[i].x, barCoords[i].y, result);
        }

        return result;
    }

    private static bool IsThickBar(BarBounds bounds) =>
        (bounds.maxCol - bounds.minCol) >= 2;
}
