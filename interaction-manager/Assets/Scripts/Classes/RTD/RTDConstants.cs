/// <summary>
/// Centralized constants for the Raised Tactile Display (RTD) DotPad hardware.
/// All hardware specifications and derived dimensions are defined here to ensure consistency.
/// </summary>
public static class RTDConstants
{
    // ===== Hardware Specifications =====
    // DotPad device pixel dimensions
    public const int PIXEL_ROWS = 40;
    public const int PIXEL_COLS = 60;

    // DotPad cell encoding dimensions
    // Each cell represents a 4x2 block of pixels, encoded as a single byte
    public const int CELL_HEIGHT = 4;
    public const int CELL_WIDTH = 2;

    // Braille text line capacity
    public const int TEXT_COLS = 20;

    // ===== Derived Constants =====
    // Number of cells per horizontal line (60 pixels / 2 pixels per cell = 30 cells)
    public const int CELLS_PER_LINE = PIXEL_COLS / CELL_WIDTH;

    // Number of graphic lines (40 pixels / 4 pixels per line = 10 lines)
    public const int NUM_LINES = PIXEL_ROWS / CELL_HEIGHT;

    // Maximum number of overview layers supported
    public const int MAX_OVERVIEW_LAYERS = 4;

    // ===== Unity Visual Pin Heights =====
    public const float PIN_HEIGHT_RAISED = 0.0125f;
    public const float PIN_HEIGHT_LOWERED = 0.01f;

    // ===== Chart Types =====
    public const string CHART_TYPE_BAR = "bar";

    // ===== Data Formatting =====
    /// <summary>
    /// Minimum probability threshold for including a node in TTS or display output.
    /// </summary>
    public const float PROBABILITY_THRESHOLD = 0.2f;
}
