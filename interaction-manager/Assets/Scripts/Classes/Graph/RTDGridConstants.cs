/// <summary>
/// Grid constants, marker values, symbol/line pattern arrays, and enums for RTD rendering.
/// </summary>
public static class RTDGridConstants
{
    // RTD Grid dimensions
    public const int GRID_HEIGHT = 40;
    public const int GRID_WIDTH = 60;

    // Marker values (semantic encoding)
    public const int BACKGROUND = 0;
    public const int AXIS_MARKER = 1;
    public const int LINE_MARKER = 2;
    public const int ZERO_MARKER = 3;
    public const int DATA_MARKER = 4;
    public const int LINE_SERIES_BASE = 20; // Per-series line markers: 20=series0, 21=series1, etc.

    // Symbol type enum for per-series override dropdowns
    public enum SymbolType { Default = -1, TriangleUp = 0, TriangleDown = 1, Diamond = 2, XCross = 3, Dot = 4, Focus = 5 }

    // Tactile symbol patterns for multi-series differentiation (col_offset, row_offset from center)
    public static readonly (int dx, int dy)[] SYMBOL_TRIANGLE_UP = { (0, 0), (-1, 1), (0, 1), (1, 1) };
    public static readonly (int dx, int dy)[] SYMBOL_TRIANGLE_DOWN = { (-1, -1), (0, -1), (1, -1), (0, 0) };
    public static readonly (int dx, int dy)[] SYMBOL_DIAMOND = { (0, 0), (0, -1), (0, 1), (-1, 0), (1, 0) };
    public static readonly (int dx, int dy)[] SYMBOL_X_CROSS = { (0, 0), (-1, -1), (1, -1), (-1, 1), (1, 1) };
    public static readonly (int dx, int dy)[] SYMBOL_DOT = { (0, 0) };
    public static readonly (int dx, int dy)[] SYMBOL_FOCUS = { (0, -1), (0, 1) };
    public static readonly (int dx, int dy)[] SYMBOL_OVERLAP = { (-1, -1), (0, -1), (1, -1), (-1, 0), (0, 0), (1, 0), (-1, 1), (0, 1), (1, 1) };
    public static readonly (int dx, int dy)[][] SERIES_SYMBOLS = { SYMBOL_TRIANGLE_UP, SYMBOL_TRIANGLE_DOWN, SYMBOL_DIAMOND, SYMBOL_X_CROSS, SYMBOL_DOT, SYMBOL_FOCUS };
    public static readonly string[] SERIES_SYMBOL_NAMES = { "triangle_up", "triangle_down", "diamond", "x_cross", "dot", "focus" };

    // Line patterns for multi-series differentiation (true = draw, false = skip)
    public static readonly bool[] LINE_PATTERN_SOLID = { true };                                    // series 0: ————
    public static readonly bool[] LINE_PATTERN_DASHED = { true, true, false };                      // series 1: — — —
    public static readonly bool[] LINE_PATTERN_DOTTED = { true, false };                            // series 2: · · ·
    public static readonly bool[] LINE_PATTERN_DASHDOT = { true, true, false, true, false };        // series 3: —·—·
    public static readonly bool[][] SERIES_LINE_PATTERNS = { LINE_PATTERN_SOLID, LINE_PATTERN_DASHED, LINE_PATTERN_DOTTED, LINE_PATTERN_DASHDOT };

    // Line thickness per series (in pixels): 1, 2, 3, then cycles
    public static readonly int[] SERIES_LINE_THICKNESSES = { 1, 2, 3 };

    // Grid positioning
    public const int X_AXIS_ROW = 36;  // X-axis row position
    public const int Y_AXIS_COL = 4;

    // Chart area bounds
    public const int CHART_MAX_COL = 58;

    // Bar fill patterns
    public const int BAR_FILL_SOLID = 0;
    public const int BAR_FILL_VERTICAL = 1;
    public const int BAR_FILL_CHECKERBOARD = 2;
    public const int BAR_FILL_HORIZONTAL = 3;
    public const int BAR_FILL_PATTERN_COUNT = 4;
}
