using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manages Unity visual representation of the DotPad pins.
/// Updates GameObject colors and heights based on buffer state.
/// </summary>
public class RTDUnityVisualizer
{
    // ===== Internal Data Structures =====
    private class PinParts
    {
        public Transform root;
        public Transform visual;
        public Renderer renderer;
    }

    // ===== Dependencies =====
    private readonly Transform _rtdRoot;
    private readonly RTDBufferManager _bufferManager;
    private readonly Dictionary<string, List<Vector2Int>> _activeHighlightPoints;
    private readonly InterfaceGraphVisualizer _graphVisualizer;

    // ===== State =====
    private Dictionary<string, PinParts> _dotLookup;
    private HashSet<Vector2Int> _hoveredPins = new HashSet<Vector2Int>();
    private HashSet<Vector2Int> _axisCoords = new HashSet<Vector2Int>();

    // ===== Constructor =====

    public RTDUnityVisualizer(Transform rtdRoot,
                               RTDBufferManager bufferManager,
                               Dictionary<string, List<Vector2Int>> activeHighlightPoints,
                               InterfaceGraphVisualizer graphVisualizer)
    {
        _rtdRoot = rtdRoot;
        _bufferManager = bufferManager;
        _activeHighlightPoints = activeHighlightPoints;
        _graphVisualizer = graphVisualizer;
    }

    // ===== Initialization =====

    /// <summary>
    /// Cache all pin GameObjects for fast lookup. Call this once after scene is loaded.
    /// </summary>
    public void Initialize()
    {
        if (_rtdRoot == null)
        {
            Debug.LogWarning("[Visualizer] RTD root not found!");
            _dotLookup = new Dictionary<string, PinParts>();
            return;
        }

        _dotLookup = new Dictionary<string, PinParts>();
        foreach (Transform child in _rtdRoot.transform) // pin roots named "x,y"
        {
            var parts = new PinParts { root = child };

            // Prefer a child named "PinVisual"; fallback to first with MeshRenderer
            Transform visual = child.Find("PinVisual");
            if (visual == null && child.childCount > 0)
            {
                for (int i = 0; i < child.childCount; i++)
                {
                    if (child.GetChild(i).GetComponent<MeshRenderer>() != null)
                    {
                        visual = child.GetChild(i);
                        break;
                    }
                }
            }

            parts.visual = visual;
            if (visual != null)
                parts.renderer = visual.GetComponent<MeshRenderer>();
            else
                Debug.LogWarning($"[Visualizer] Pin '{child.name}' has no PinVisual child.");

            _dotLookup[child.name.Trim()] = parts;
        }

        Debug.Log($"[Visualizer] Initialized {_dotLookup.Count} pins");
    }

    // ===== Full Refresh =====

    /// <summary>
    /// Refresh all pins from the base image (ignores overlay).
    /// Also builds axis coordinate cache (all pins with BaseImage value = 1).
    /// </summary>
    public void RefreshFromBase()
    {
        // Build axis cache: all pins with value 1 in BaseImage are axes (green)
        _axisCoords.Clear();
        for (int y = 0; y < RTDConstants.PIXEL_ROWS; y++)
        {
            for (int x = 0; x < RTDConstants.PIXEL_COLS; x++)
            {
                if (_bufferManager.BaseImage[y, x] == 1)
                {
                    _axisCoords.Add(new Vector2Int(x, y));
                }
            }
        }
        Debug.Log($"[RefreshFromBase] Built axis cache: {_axisCoords.Count} axis pins (BaseImage value=1)");

        // Refresh all pins
        for (int y = 0; y < RTDConstants.PIXEL_ROWS; y++)
        {
            for (int x = 0; x < RTDConstants.PIXEL_COLS; x++)
            {
                if (_dotLookup.TryGetValue($"{x},{y}", out var pin))
                    PaintDot(pin, _bufferManager.BaseImage[y, x], new Vector2Int(x, y));
            }
        }
    }

    /// <summary>
    /// Refresh all pins from the composed view (base + overlay).
    /// </summary>
    public void RefreshFromView()
    {
        for (int y = 0; y < RTDConstants.PIXEL_ROWS; y++)
        {
            for (int x = 0; x < RTDConstants.PIXEL_COLS; x++)
            {
                // overlay -> 1 = force up, -1 = force down, 0 = fall back to base
                int v = _bufferManager.Overlay[y, x] == 1 ? 1 :
                        _bufferManager.Overlay[y, x] == -1 ? 0 :
                        _bufferManager.BaseImage[y, x];
                if (_dotLookup.TryGetValue($"{x},{y}", out var pin))
                    PaintDot(pin, v, new Vector2Int(x, y));
            }
        }
    }

    /// <summary>
    /// Refresh all pins. Uses view (base + overlay) by default.
    /// </summary>
    public void Refresh()
    {
        RefreshFromView();
    }

    // ===== Single Pin Update =====

    /// <summary>
    /// Refresh a single pin from the view.
    /// </summary>
    public void RefreshPin(Vector2Int coord)
    {
        if (_dotLookup.TryGetValue($"{coord.x},{coord.y}", out var pin))
        {
            int v = _bufferManager.Overlay[coord.y, coord.x] == 1 ? 1 :
                    _bufferManager.Overlay[coord.y, coord.x] == -1 ? 0 :
                    _bufferManager.BaseImage[coord.y, coord.x];
            PaintDot(pin, v, coord);
        }
    }


    // ===== Hover Management =====

    /// <summary>
    /// Mark a pin as hovered (renders blue).
    /// </summary>
    public void SetHover(Vector2Int coord, bool isHovered)
    {
        if (isHovered)
            _hoveredPins.Add(coord);
        else
            _hoveredPins.Remove(coord);

        RefreshPin(coord);
    }

    /// <summary>
    /// Clear all hover states.
    /// </summary>
    public void ClearAllHovers()
    {
        var toRefresh = new List<Vector2Int>(_hoveredPins);
        _hoveredPins.Clear();

        foreach (var coord in toRefresh)
            RefreshPin(coord);
    }

    // ===== Internal Rendering =====

    /// <summary>
    /// Paint a single pin GameObject with the given value and coordinate.
    /// Value: 0=lowered, 1=raised green, 2=black, 3=cyan, 4=red
    /// </summary>
    private void PaintDot(PinParts pin, int value, Vector2Int coord)
    {
        if (pin == null)
            return;

        // Check if this pin is an axis (cached during RefreshFromBase)
        bool isAxis = _axisCoords.Contains(coord);

        // Determine color
        if (_hoveredPins.Contains(coord))
        {
            // Hovered = blue
            if (pin.renderer != null)
                pin.renderer.material.color = Color.blue;
        }
        else
        {
            // Check if this pin is being actively highlighted (overlay forces it raised)
            bool isGestureHighlight = (_bufferManager.Overlay[coord.y, coord.x] == 1);

            // Apply color based on value
            if (pin.renderer != null)
            {
                pin.renderer.material.color = value switch
                {
                    1 => isGestureHighlight ? new Color(0.6f, 0.2f, 1f) : Color.green,
                    2 => Color.black,
                    3 => Color.cyan,
                    4 => Color.red,
                    _ => isAxis ? Color.green : Color.white
                };
            }
        }

        // Set height (scale the visual child on Y axis only)
        if (pin.visual != null)
        {
            var s = pin.visual.localScale;
            s.y = (value >= 1 && value <= 4) ? RTDConstants.PIN_HEIGHT_RAISED : RTDConstants.PIN_HEIGHT_LOWERED;
            pin.visual.localScale = s;
        }
    }

}
