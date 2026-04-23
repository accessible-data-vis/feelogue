using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Manages all highlight and pulse effects on the DotPad display.
///
/// Two highlight modes based on caller:
///   Touch / navigation (hand = "left", "right", "navigation"):
///     Pulses the mark's own pixels — bar perimeter, symbol pins, dot, or box fallback.
///   Agent (hand = "agent"):
///     Static box: erases mark pixels, raises surrounding box.
///     Thick bar exception: lowers interior, leaves perimeter.
///
/// Delegates mark geometry to RTDMarkHighlighter.
/// Delegates gesture persistence to RTDGestureHighlightPersistence.
/// </summary>
public class RTDHighlightManager
{
    // Timing constants
    private const float NEIGHBOUR_PIN_DELAY    = 0.025f;
    private const float LINE_SHAPE_PIN_DELAY   = 0f;
    private const float ISOLATION_PIN_DELAY    = 0.025f;
    private const float TOUCH_PULSE_INTERVAL   = 0.4f;
    private const int   LOCAL_ISOLATION_RADIUS = 2;

    // ===== Dependencies =====
    private readonly RTDBufferManager _bufferManager;
    private readonly InterfaceButtonGUI _buttonGUI;
    private readonly InterfaceGraphVisualizer _graphVisualizer;
    private readonly MonoBehaviour _coroutineHost;
    private readonly RTDGestureHighlightPersistence _gesturePersistence;

    // ===== Highlight Configs (pushed from VegaChartLoader) =====
    private HighlightConfig _gestureConfig = new HighlightConfig { Shape = HighlightMarkShape.Mark, Anim = HighlightAnim.Animated, Duration = -1f };
    private HighlightConfig _agentConfig   = new HighlightConfig { Shape = HighlightMarkShape.Box,  Anim = HighlightAnim.Static,   Duration = -1f };
    private HighlightConfig _navConfig     = new HighlightConfig { Shape = HighlightMarkShape.Mark, Anim = HighlightAnim.Animated, Duration = -1f };

    // ===== State =====
    private Dictionary<string, List<Vector2Int>> _activeHighlightPoints   = new Dictionary<string, List<Vector2Int>>();
    private Dictionary<string, HashSet<string>>  _handHighlightKeys       = new Dictionary<string, HashSet<string>>();
    private Dictionary<string, Coroutine>        _activeCoroutines        = new Dictionary<string, Coroutine>();
    private Dictionary<string, Coroutine>        _pendingTouchCoroutines  = new Dictionary<string, Coroutine>();
    private string _currentChartType = "";
    private bool _useSeriesSymbols = false;
    private RTDGridConstants.SymbolType[] _seriesSymbolOverrides;

    // ===== Callbacks to RTDUpdater =====
    public Action<int, int, sbyte>    OnSetOverlay;
    public Action<int, int>           OnRefreshCell;
    public Func<int, byte[]>          OnBuildLineBytesFromView;
    public Action<int, int, byte[]>   OnQueueLineCommand;
    public Action                     OnCancelTail;
    public Action                     OnCancelUnifiedTailOnly;
    public Action<HashSet<int>, bool> OnStartLineTail;
    public Func<bool>                 OnIsStreaming;
    public Action<Action>             OnSetPendingInteractive;
    public Action                     OnNavigationClearNavigation;
    public Action                     OnStopLineTailCoroutine;
    public Action<string>             OnSpeakText;
    public Action                     OnCancelStreamingForInteractive;
    public Action                     OnFlushCommandQueue;
    public Action<HashSet<int>>       OnSendLinesFromView;

    // ===== Properties =====
    public Dictionary<string, List<Vector2Int>> ActiveHighlightPoints => _activeHighlightPoints;
    public RTDGestureHighlightPersistence GesturePersistence => _gesturePersistence;

    // ===== Constructor =====
    public RTDHighlightManager(
        RTDBufferManager bufferManager,
        InterfaceButtonGUI buttonGUI,
        InterfaceGraphVisualizer graphVisualizer,
        MonoBehaviour coroutineHost)
    {
        _bufferManager    = bufferManager;
        _buttonGUI        = buttonGUI;
        _graphVisualizer  = graphVisualizer;
        _coroutineHost    = coroutineHost;
        _gesturePersistence = new RTDGestureHighlightPersistence(buttonGUI, graphVisualizer);
    }

    // ===== Public Methods =====

    public void SetChartType(string chartType)
    {
        _currentChartType = chartType;
    }

    public void SetUseSeriesSymbols(bool value)
    {
        _useSeriesSymbols = value;
    }

    public void SetSeriesSymbolOverrides(RTDGridConstants.SymbolType[] overrides)
    {
        _seriesSymbolOverrides = overrides;
    }

    public void SetHighlightConfigs(HighlightConfig gesture, HighlightConfig agent, HighlightConfig nav)
    {
        _gestureConfig = gesture;
        _agentConfig   = agent;
        _navConfig     = nav;
    }

    public void StopPulsePins()
    {
        OnCancelTail?.Invoke();

        foreach (var cr in _pendingTouchCoroutines.Values)
            if (cr != null) _coroutineHost.StopCoroutine(cr);
        _pendingTouchCoroutines.Clear();

        foreach (var key in _activeCoroutines.Keys.ToList())
            StopRoutine(key);

        foreach (var hand in _handHighlightKeys.Keys.ToList())
            ClearHighlights(hand);

        OnNavigationClearNavigation?.Invoke();
    }

    public void StopPulsePins(IEnumerable<Vector2Int> coords, float interval)
    {
        string key = $"PulsePins_{string.Join("_", coords.Select(c => $"{c.x}-{c.y}"))}_{interval}";
        StopRoutine(key);
    }

    /// <summary>
    /// Agent highlight: static box around the point (or bar-interior for thick bars).
    /// Deferred automatically if streaming is in progress.
    /// </summary>
    public void ShowShape(int x, int y, HighlightShape shape, float duration = -1f, string hand = "agent")
    {
        string key = $"Show_{x}-{y}_{shape}";

        if (OnIsStreaming != null && OnIsStreaming())
        {
            OnSetPendingInteractive?.Invoke(() =>
            {
                var neighbours = GetBoxNeighbours(x, y);
                ClearHighlights(hand);
                StartRoutine(key, ShowShapeCoroutine(key, x, y, neighbours, duration));
                TrackKeyForHand(hand, key);
            });
            Debug.Log("[ShowShape] deferred (streaming in progress)");
            return;
        }

        var nbrs = GetBoxNeighbours(x, y);
        ClearHighlights(hand);
        StartRoutine(key, ShowShapeCoroutine(key, x, y, nbrs, duration));
        TrackKeyForHand(hand, key);
    }

    /// <summary>
    /// Touch / navigation highlight: pulses the mark's own pixels.
    /// </summary>
    public void ShowTouchHighlights(List<Vector2Int> coords, HighlightShape shape, float duration, string hand)
    {
        _gesturePersistence.RecordTouchCenterPoints(hand, coords);

        // Cancel any still-waiting outer coroutine for this hand before starting a new one.
        if (_pendingTouchCoroutines.TryGetValue(hand, out var existing) && existing != null)
            _coroutineHost.StopCoroutine(existing);

        var cr = _coroutineHost.StartCoroutine(ShowTouchHighlightsCoroutine(coords, duration, hand));
        _pendingTouchCoroutines[hand] = cr;
    }

    public void ClearHighlights(string hand)
    {
        var lines = ClearHighlightsMemory(hand);
        if (lines.Count > 0)
            OnSendLinesFromView?.Invoke(lines);
    }

    /// <summary>
    /// Stops all active highlight coroutines for <paramref name="hand"/> and zeroes their
    /// overlay pixels in memory.  Does NOT send to the device — call
    /// <see cref="OnSendLinesFromView"/> with the returned set to flush changes.
    ///
    /// Designed for use by <see cref="ShowTouchHighlightsCoroutine"/> (Static path) so it
    /// can combine the old-clear lines with the new-apply lines into a single batch send.
    /// </summary>
    private HashSet<int> ClearHighlightsMemory(string hand)
    {
        if (_pendingTouchCoroutines.TryGetValue(hand, out var pending) && pending != null)
        {
            _coroutineHost.StopCoroutine(pending);
            _pendingTouchCoroutines.Remove(hand);
        }

        var affectedLines = new HashSet<int>();
        if (!_handHighlightKeys.TryGetValue(hand, out var keys) || keys.Count == 0)
            return affectedLines;

        OnCancelTail?.Invoke();

        foreach (var key in keys.ToList())
        {
            // Capture points BEFORE StopCoroutine — the coroutine's finally block
            // runs synchronously inside StopCoroutine and removes the key from the dict.
            _activeHighlightPoints.TryGetValue(key, out var points);

            if (_activeCoroutines.TryGetValue(key, out var cr))
            {
                if (cr != null) _coroutineHost.StopCoroutine(cr);
                _activeCoroutines.Remove(key);
            }

            _activeHighlightPoints.Remove(key); // no-op if finally already removed it

            if (points != null)
            {
                foreach (var p in points)
                {
                    _bufferManager.Overlay[p.y, p.x] = 0;
                    affectedLines.Add(p.y / RTDConstants.CELL_HEIGHT + 1);
                }
            }
        }

        // Flush AFTER StopCoroutine: clears stale nav commands AND the redundant restore
        // BatchSendLines commands just queued by the finally blocks.
        OnFlushCommandQueue?.Invoke();

        keys.Clear();
        return affectedLines;
    }

    public void PulseShape(int x, int y, HighlightShape shape, float interval = 1f, float duration = -1f, string hand = "agent", bool clearPrevious = true)
    {
        if (clearPrevious)
            ClearHighlights(hand);

        var neighbours = GetNeighbours(x, y, shape);
        string key = $"PulseShape_{hand}_{shape}_{x}_{y}";
        StartRoutine(key, PulseShapeCoroutine(key, x, y, neighbours, interval, duration, shape));
        TrackKeyForHand(hand, key);
    }

    public void PulsePins(IEnumerable<Vector2Int> coords, float interval, float duration = -1f, string hand = "agent")
    {
        string key = $"PulsePins_{hand}_" + string.Join("_", coords.Select(c => $"{c.x}-{c.y}")) + $"_{interval}";
        StartRoutine(key, PulsePinsCoroutine(coords.ToList(), interval, duration, key));
        TrackKeyForHand(hand, key);
    }

    public void HighlightMostRecentTouch()
    {
        string message = _gesturePersistence.GetMostRecentTouchInfo(out var toRestore);

        if (message == null)
        {
            Debug.Log("No touch highlights to show");
            OnSpeakText?.Invoke("No recent touches");
            return;
        }

        foreach (var (coords, shape, hand) in toRestore)
            ShowTouchHighlights(coords, shape, -1f, hand);

        OnSpeakText?.Invoke(message);
    }

    public Dictionary<string, List<Vector2Int>> GetActiveGestureHighlights()
    {
        var result = new Dictionary<string, List<Vector2Int>>();

        foreach (var hand in new[] { "left", "right" })
        {
            if (_handHighlightKeys.TryGetValue(hand, out var keys))
            {
                var coords = keys
                    .Where(k => _activeHighlightPoints.ContainsKey(k))
                    .SelectMany(k => _activeHighlightPoints[k])
                    .Distinct()
                    .ToList();

                if (coords.Count > 0)
                    result[hand] = coords;
            }
        }

        return result;
    }

    // ===== Delegated Gesture Persistence =====

    public void StoreAllGestureHighlights(Func<Vector2Int, (object xValue, object yValue)?> getNodeValues)
    {
        _gesturePersistence.StoreAllGestureHighlights(GetActiveGestureHighlights(), getNodeValues);
    }

    public void RestoreAllGestureHighlights()
    {
        foreach (var (coords, shape, hand) in _gesturePersistence.GetRestoredGestureHighlights())
            ShowTouchHighlights(coords, shape, -1f, hand);
    }

    public void ClearStoredGestureValues()
    {
        _gesturePersistence.ClearStoredGestureValues();
    }

    // ===== Config helpers =====

    private HighlightConfig GetConfigForHand(string hand)
    {
        if (hand == "navigation") return _navConfig;
        if (hand == "agent")      return _agentConfig;
        return _gestureConfig; // "left" or "right"
    }

    private List<Vector2Int> ResolveConfigPins(int x, int y, HighlightMarkShape shape)
    {
        switch (shape)
        {
            case HighlightMarkShape.BarPerimeter:
            {
                var coords = RTDMarkHighlighter.GetBarCoords(x, y, _graphVisualizer);
                return coords != null ? RTDMarkHighlighter.GetBarPerimeter(coords) : null;
            }
            case HighlightMarkShape.BarInterior:
            {
                var coords = RTDMarkHighlighter.GetBarCoords(x, y, _graphVisualizer);
                return coords != null ? RTDMarkHighlighter.GetBarInterior(coords) : null;
            }
            case HighlightMarkShape.Box:
                return GetBoxNeighbours(x, y);
            default: // Mark
            {
                var pins = RTDMarkHighlighter.GetMarkNeighbours(x, y, _currentChartType, _graphVisualizer);
                if (pins != null && pins.Count > 0) return pins;
                // If symbols are enabled on a non-bar chart, try series-0 symbol as fallback
                if (_useSeriesSymbols && _currentChartType != RTDConstants.CHART_TYPE_BAR)
                {
                    var symPins = RTDMarkHighlighter.GetFallbackSymbolPattern(x, y, _graphVisualizer, _seriesSymbolOverrides);
                    if (symPins != null && symPins.Count > 0) return symPins;
                }
                return GetBoxNeighbours(x, y);
            }
        }
    }

    // ===== Coroutines =====

    /// <summary>
    /// Touch / navigation: resolve mark pixels for each coord and pulse or hold them
    /// according to the hand's HighlightConfig.
    ///
    /// Static path (Phase 2): clears old overlays in memory, starts inner coroutines
    /// (which write new overlays synchronously before their first yield), then issues
    /// a single combined batch send covering both the cleared and new lines.
    ///
    /// Animated path: keeps existing per-pulse batch-send flow.
    /// </summary>
    private IEnumerator ShowTouchHighlightsCoroutine(List<Vector2Int> coords, float duration, string hand)
    {
        var cfg = GetConfigForHand(hand);

        if (cfg.Anim == HighlightAnim.Static)
        {
            if (cfg.UseBatchSend)
            {
                // Phase 2b: collapse clear + apply into one combined batch send.
                var oldLines = ClearHighlightsMemory(hand);

                if (hand != "agent")
                    OnCancelStreamingForInteractive?.Invoke();

                yield return null;

                _pendingTouchCoroutines.Remove(hand);

                float effectiveDuration = (duration >= 0f) ? duration : cfg.Duration;
                bool invertCenter  = (cfg.Shape == HighlightMarkShape.Box);
                sbyte staticPinValue = (cfg.Shape == HighlightMarkShape.BarInterior || cfg.Shape == HighlightMarkShape.BarPerimeter) ? (sbyte)-1 : (sbyte)1;

                var allLines = new HashSet<int>(oldLines);

                foreach (var coord in coords)
                {
                    var pins = ResolveConfigPins(coord.x, coord.y, cfg.Shape);
                    if (pins == null || pins.Count == 0)
                        pins = GetBoxNeighbours(coord.x, coord.y);

                    string key = $"Touch_{hand}_{coord.x}-{coord.y}";
                    Vector2Int? centerToLower = invertCenter ? coord : (Vector2Int?)null;

                    // The coroutine writes overlays synchronously (before its first yield)
                    // but skips the per-coroutine batch send — we'll do one combined send below.
                    var handle = _coroutineHost.StartCoroutine(
                        ShowStaticPinsCoroutine(key, pins, effectiveDuration, centerToLower, staticPinValue,
                                                suppressInitialSend: true, useBatch: true));
                    _activeCoroutines[key] = handle;
                    TrackKeyForHand(hand, key);

                    foreach (var p in pins)
                        allLines.Add(p.y / RTDConstants.CELL_HEIGHT + 1);
                    if (centerToLower.HasValue)
                        allLines.Add(centerToLower.Value.y / RTDConstants.CELL_HEIGHT + 1);
                }

                // Single combined send covering cleared old lines + new highlight lines.
                if (allLines.Count > 0)
                    OnSendLinesFromView?.Invoke(allLines);
            }
            else
            {
                // Per-cell path: clear (batch is fine for one-shot clear), then each coroutine
                // sends targeted per-cell updates for a smooth, non-rigid feel.
                ClearHighlights(hand);

                if (hand != "agent")
                    OnCancelStreamingForInteractive?.Invoke();

                yield return null;

                _pendingTouchCoroutines.Remove(hand);

                float effectiveDuration = (duration >= 0f) ? duration : cfg.Duration;
                bool invertCenter = (cfg.Shape == HighlightMarkShape.Box);
                sbyte staticPinValue = (cfg.Shape == HighlightMarkShape.BarInterior || cfg.Shape == HighlightMarkShape.BarPerimeter) ? (sbyte)-1 : (sbyte)1;

                foreach (var coord in coords)
                {
                    var pins = ResolveConfigPins(coord.x, coord.y, cfg.Shape);
                    if (pins == null || pins.Count == 0)
                        pins = GetBoxNeighbours(coord.x, coord.y);

                    string key = $"Touch_{hand}_{coord.x}-{coord.y}";
                    Vector2Int? centerToLower = invertCenter ? coord : (Vector2Int?)null;

                    var handle = _coroutineHost.StartCoroutine(
                        ShowStaticPinsCoroutine(key, pins, effectiveDuration, centerToLower, staticPinValue,
                                                suppressInitialSend: false, useBatch: false));
                    _activeCoroutines[key] = handle;
                    TrackKeyForHand(hand, key);
                }
            }
        }
        else
        {
            // Animated path: each pulse cycle issues per-cell or batch sends per cfg.UseBatchSend.
            ClearHighlights(hand);

            if (hand != "agent")
                OnCancelStreamingForInteractive?.Invoke();

            yield return null;

            _pendingTouchCoroutines.Remove(hand);

            float effectiveDuration = (duration >= 0f) ? duration : cfg.Duration;

            foreach (var coord in coords)
            {
                var pins = ResolveConfigPins(coord.x, coord.y, cfg.Shape);
                if (pins == null || pins.Count == 0)
                    pins = GetBoxNeighbours(coord.x, coord.y);

                string key = $"Touch_{hand}_{coord.x}-{coord.y}";
                bool invertCenter = (cfg.Shape == HighlightMarkShape.Box);

                var handle = _coroutineHost.StartCoroutine(
                    PulseMarkCoroutine(key, coord, pins, effectiveDuration, invertCenter,
                                       isBarInterior: cfg.Shape == HighlightMarkShape.BarInterior,
                                       useBatch: cfg.UseBatchSend));
                _activeCoroutines[key] = handle;
                TrackKeyForHand(hand, key);
            }
        }
    }

    /// <summary>
    /// Pulses a set of mark pixels (touch / navigation highlight).
    /// Normal: alternates +1 / -1. BarInterior: starts hollow (-1) toggling to 0 and back.
    /// Each toggle writes overlay values directly and issues a single batch line send.
    /// Natural expiry also restores via a single batch send.
    /// </summary>
    private IEnumerator PulseMarkCoroutine(string key, Vector2Int center, List<Vector2Int> pins, float duration, bool invertCenter = false, bool isBarInterior = false, bool useBatch = false)
    {
        float elapsed = 0f;
        // BarInterior: start "filled" so first toggle → hollow (-1).
        // Normal: start "down" so first toggle → raised (+1).
        bool up = isBarInterior;
        sbyte overlayUp   = isBarInterior ? (sbyte)0  : (sbyte)1;
        sbyte overlayDown = (sbyte)-1; // hollow for bar, lowered for normal

        // Pre-compute the full pin list once (used for batch sends throughout).
        var allPins = new List<Vector2Int>(pins);
        if (invertCenter) allPins.Add(center);

        try
        {
            var prevOverlay = new Dictionary<Vector2Int, sbyte>();
            foreach (var p in pins)
                prevOverlay[p] = _bufferManager.Overlay[p.y, p.x];
            if (invertCenter) prevOverlay[center] = _bufferManager.Overlay[center.y, center.x];

            _activeHighlightPoints[key] = new List<Vector2Int>(pins);
            if (invertCenter) _activeHighlightPoints[key].Add(center);

            while (duration < 0f || elapsed < duration)
            {
                up = !up;
                sbyte v = up ? overlayUp : overlayDown;
                foreach (var p in pins)
                    _bufferManager.Overlay[p.y, p.x] = v;
                if (invertCenter)
                    _bufferManager.Overlay[center.y, center.x] = up ? (sbyte)-1 : (sbyte)1;

                if (useBatch)
                {
                    BatchSendLines(allPins);
                }
                else
                {
                    foreach (var p in pins)
                        OnSetOverlay?.Invoke(p.y, p.x, up ? overlayUp : overlayDown);
                    if (invertCenter) OnSetOverlay?.Invoke(center.y, center.x, up ? (sbyte)-1 : (sbyte)1);
                }

                yield return new WaitForSeconds(TOUCH_PULSE_INTERVAL);
                elapsed += TOUCH_PULSE_INTERVAL;
            }

            // Natural expiry: restore previous overlay values, then send.
            foreach (var p in pins)
                _bufferManager.Overlay[p.y, p.x] = prevOverlay[p];
            if (invertCenter)
                _bufferManager.Overlay[center.y, center.x] = prevOverlay[center];

            if (useBatch)
            {
                BatchSendLines(allPins);
            }
            else
            {
                OnFlushCommandQueue?.Invoke();
                foreach (var p in pins) OnRefreshCell?.Invoke(p.y, p.x);
                if (invertCenter) OnRefreshCell?.Invoke(center.y, center.x);
            }
        }
        finally
        {
            _activeCoroutines.Remove(key);
            _activeHighlightPoints.Remove(key);
        }
    }

    /// <summary>
    /// Sets a set of pins once and holds until cleared or duration expires.
    /// pinValue: +1 to raise (default), -1 to lower (e.g. BarInterior hollow effect).
    ///
    /// useBatch=true (bars): overlay writes happen synchronously before the first yield;
    ///   suppressInitialSend=true lets the caller defer the send for a combined batch;
    ///   the finally block restores via a single batch line send.
    /// useBatch=false (symbols/marks): uses OnSetOverlay for per-cell sends (suppressInitialSend
    ///   is ignored); cleanup uses OnRefreshCell per pin.
    /// </summary>
    private IEnumerator ShowStaticPinsCoroutine(string key, List<Vector2Int> pins, float duration, Vector2Int? centerToLower = null, sbyte pinValue = 1, bool suppressInitialSend = false, bool useBatch = false)
    {
        var prevOverlay = new Dictionary<Vector2Int, sbyte>();
        foreach (var p in pins)
            prevOverlay[p] = _bufferManager.Overlay[p.y, p.x];
        if (centerToLower.HasValue)
            prevOverlay[centerToLower.Value] = _bufferManager.Overlay[centerToLower.Value.y, centerToLower.Value.x];

        _activeHighlightPoints[key] = new List<Vector2Int>(pins);
        if (centerToLower.HasValue) _activeHighlightPoints[key].Add(centerToLower.Value);

        // Materialise the full pin list once; reused in both try and finally.
        var allPins = new List<Vector2Int>(pins);
        if (centerToLower.HasValue) allPins.Add(centerToLower.Value);

        try
        {
            // Write overlay values synchronously (before first yield).
            foreach (var p in pins)
                _bufferManager.Overlay[p.y, p.x] = pinValue;
            if (centerToLower.HasValue)
                _bufferManager.Overlay[centerToLower.Value.y, centerToLower.Value.x] = -1;

            if (useBatch)
            {
                if (!suppressInitialSend)
                    BatchSendLines(allPins);
            }
            else
            {
                // suppressInitialSend is ignored for per-cell path; OnSetOverlay sends immediately
                foreach (var p in pins) OnSetOverlay?.Invoke(p.y, p.x, pinValue);
                if (centerToLower.HasValue) OnSetOverlay?.Invoke(centerToLower.Value.y, centerToLower.Value.x, -1);
            }

            if (duration >= 0f)
                yield return new WaitForSeconds(duration);
            else
                yield return new WaitUntil(() => false); // held until ClearHighlights stops it
        }
        finally
        {
            // Restore overlay values unconditionally.
            foreach (var p in pins)
                _bufferManager.Overlay[p.y, p.x] = prevOverlay[p];
            if (centerToLower.HasValue)
                _bufferManager.Overlay[centerToLower.Value.y, centerToLower.Value.x] = prevOverlay[centerToLower.Value];

            if (useBatch)
            {
                BatchSendLines(allPins);
            }
            else
            {
                foreach (var p in pins) OnRefreshCell?.Invoke(p.y, p.x);
                if (centerToLower.HasValue) OnRefreshCell?.Invoke(centerToLower.Value.y, centerToLower.Value.x);
            }

            _activeCoroutines.Remove(key);
            _activeHighlightPoints.Remove(key);
        }
    }

    /// <summary>
    /// Agent highlight coroutine. Shape and animation driven by GetAgentConfig().
    /// </summary>
    private IEnumerator ShowShapeCoroutine(string key, int cx, int cy, List<Vector2Int> neighbours, float duration, bool enableTail = true)
    {
        var cfg = _agentConfig;
        float effectiveDuration = (duration >= 0f) ? duration : cfg.Duration;

        var configPins = ResolveConfigPins(cx, cy, cfg.Shape);
        if (configPins == null || configPins.Count == 0)
            configPins = GetBoxNeighbours(cx, cy);

        try
        {
            // ── Animated: delegate to PulseMarkCoroutine ───────────────────
            if (cfg.Anim == HighlightAnim.Animated)
            {
                string pulseKey = $"Touch_agent_{cx}-{cy}";
                var coord = new Vector2Int(cx, cy);
                var handle = _coroutineHost.StartCoroutine(PulseMarkCoroutine(pulseKey, coord, configPins, effectiveDuration));
                _activeCoroutines[pulseKey] = handle;
                TrackKeyForHand("agent", pulseKey);
                yield break;
            }

            // ── Static path ────────────────────────────────────────────────
            bool isBarShape = (cfg.Shape == HighlightMarkShape.BarInterior || cfg.Shape == HighlightMarkShape.BarPerimeter);

            // Optional local isolation (skip for bar-specific shapes)
            var isolatedPts = new List<Vector2Int>();
            var prevIso = new Dictionary<Vector2Int, sbyte>();
            if (_buttonGUI.GetLocalIsolationMode() && !isBarShape)
            {
                var iso = HighlightWithLocalIsolation(new Vector2Int(cx, cy), configPins);
                foreach (var kv in iso)
                {
                    var p = kv.Key;
                    isolatedPts.Add(p);
                    prevIso[p] = _bufferManager.Overlay[p.y, p.x];
                    OnSetOverlay?.Invoke(p.y, p.x, -1);
                    yield return new WaitForSeconds(ISOLATION_PIN_DELAY);
                }
                yield return null;
            }

            var prevOverlay = new Dictionary<Vector2Int, sbyte>();
            var affectedPts = new List<Vector2Int>();
            _activeHighlightPoints[key] = affectedPts; // shared ref — cleanup works at any point mid-animation

            // BarInterior and BarPerimeter lower pins; everything else raises them
            sbyte pinValue = (cfg.Shape == HighlightMarkShape.BarInterior || cfg.Shape == HighlightMarkShape.BarPerimeter) ? (sbyte)-1 : (sbyte)1;
            bool usePinDelay = !isBarShape;

            foreach (var p in configPins)
            {
                prevOverlay[p] = _bufferManager.Overlay[p.y, p.x];
                OnSetOverlay?.Invoke(p.y, p.x, pinValue);
                affectedPts.Add(p);
                if (usePinDelay)
                    yield return new WaitForSeconds(NEIGHBOUR_PIN_DELAY);
            }

            if (cfg.Shape == HighlightMarkShape.Box)
            {
                var center = new Vector2Int(cx, cy);
                prevOverlay[center] = _bufferManager.Overlay[cy, cx];
                affectedPts.Add(center);        // tracked BEFORE overlay is set
                OnSetOverlay?.Invoke(cy, cx, -1);
            }

            // _activeHighlightPoints[key] is already set (shared ref); just append isolation pins
            if (isolatedPts.Count > 0)
                _activeHighlightPoints[key].AddRange(isolatedPts);

            if (enableTail)
            {
                var affectedLines = new HashSet<int>();
                foreach (var p in affectedPts)  affectedLines.Add(p.y / RTDConstants.CELL_HEIGHT + 1);
                foreach (var p in isolatedPts)  affectedLines.Add(p.y / RTDConstants.CELL_HEIGHT + 1);
                OnStartLineTail?.Invoke(affectedLines, false);
            }

            if (effectiveDuration > 0f)
            {
                yield return new WaitForSeconds(effectiveDuration);

                foreach (var p in affectedPts)
                {
                    _bufferManager.Overlay[p.y, p.x] = prevOverlay.TryGetValue(p, out var v) ? v : (sbyte)0;
                    OnRefreshCell?.Invoke(p.y, p.x);
                }
                foreach (var p in isolatedPts)
                {
                    _bufferManager.Overlay[p.y, p.x] = prevIso[p];
                    OnRefreshCell?.Invoke(p.y, p.x);
                }
            }
        }
        finally
        {
            _activeCoroutines.Remove(key);
        }
    }

    private IEnumerator PulseShapeCoroutine(string key, int cx, int cy, List<Vector2Int> neighbours, float interval, float duration, HighlightShape shape)
    {
        float elapsed = 0f;
        bool onState = false;

        try
        {
            var isoPrev = new Dictionary<Vector2Int, sbyte>();
            if (_buttonGUI.GetLocalIsolationMode())
            {
                var iso = HighlightWithLocalIsolation(new Vector2Int(cx, cy), neighbours);
                foreach (var kv in iso)
                {
                    var p = kv.Key;
                    isoPrev[p] = _bufferManager.Overlay[p.y, p.x];
                    OnSetOverlay?.Invoke(p.y, p.x, -1);
                    yield return new WaitForSeconds(ISOLATION_PIN_DELAY);
                }
                yield return null;
            }

            var center = new Vector2Int(cx, cy);
            var prevOverlay = new Dictionary<Vector2Int, sbyte> { [center] = _bufferManager.Overlay[cy, cx] };
            foreach (var n in neighbours) prevOverlay[n] = _bufferManager.Overlay[n.y, n.x];

            var affectedPoints = new List<Vector2Int> { center };
            affectedPoints.AddRange(neighbours);
            _activeHighlightPoints[key] = affectedPoints;

            while (duration < 0f || elapsed < duration)
            {
                onState = !onState;

                if (onState)
                {
                    foreach (var n in neighbours)
                    {
                        OnSetOverlay?.Invoke(n.y, n.x, 1);
                        if (shape == HighlightShape.Line)
                            yield return new WaitForSeconds(LINE_SHAPE_PIN_DELAY);
                    }
                    OnSetOverlay?.Invoke(cy, cx, -1);
                }
                else
                {
                    foreach (var n in neighbours)
                    {
                        OnSetOverlay?.Invoke(n.y, n.x, -1);
                        if (shape == HighlightShape.Line)
                            yield return new WaitForSeconds(LINE_SHAPE_PIN_DELAY);
                    }
                    OnSetOverlay?.Invoke(cy, cx, 1);
                }

                yield return new WaitForSeconds(interval);
                elapsed += interval;
            }

            _bufferManager.Overlay[cy, cx] = prevOverlay[center];
            OnRefreshCell?.Invoke(cy, cx);
            foreach (var n in neighbours)
            {
                _bufferManager.Overlay[n.y, n.x] = prevOverlay[n];
                OnRefreshCell?.Invoke(n.y, n.x);
            }
            foreach (var kv in isoPrev)
            {
                _bufferManager.Overlay[kv.Key.y, kv.Key.x] = kv.Value;
                OnRefreshCell?.Invoke(kv.Key.y, kv.Key.x);
            }
        }
        finally
        {
            _activeCoroutines.Remove(key);
            _activeHighlightPoints.Remove(key);
        }
    }

    private IEnumerator PulsePinsCoroutine(List<Vector2Int> coords, float interval, float duration, string routineKey)
    {
        float elapsed = 0f;
        bool up = false;

        try
        {
            var isoPrev = new Dictionary<Vector2Int, sbyte>();
            if (_buttonGUI.GetLocalIsolationMode())
            {
                foreach (var coord in coords)
                {
                    var iso = HighlightWithLocalIsolation(coord, new List<Vector2Int>());
                    foreach (var kv in iso)
                    {
                        var p = kv.Key;
                        if (!isoPrev.ContainsKey(p))
                        {
                            isoPrev[p] = _bufferManager.Overlay[p.y, p.x];
                            OnSetOverlay?.Invoke(p.y, p.x, -1);
                            yield return new WaitForSeconds(ISOLATION_PIN_DELAY);
                        }
                    }
                }
                yield return null;
            }

            var prevOverlay = new Dictionary<Vector2Int, sbyte>();
            foreach (var p in coords) prevOverlay[p] = _bufferManager.Overlay[p.y, p.x];

            while (duration < 0f || elapsed < duration)
            {
                up = !up;
                foreach (var p in coords)
                    OnSetOverlay?.Invoke(p.y, p.x, up ? (sbyte)1 : (sbyte)-1);
                yield return new WaitForSeconds(interval);
                elapsed += interval;
            }

            foreach (var p in coords)
            {
                _bufferManager.Overlay[p.y, p.x] = prevOverlay[p];
                OnRefreshCell?.Invoke(p.y, p.x);
            }
            foreach (var kv in isoPrev)
            {
                _bufferManager.Overlay[kv.Key.y, kv.Key.x] = kv.Value;
                OnRefreshCell?.Invoke(kv.Key.y, kv.Key.x);
            }
        }
        finally
        {
            _activeCoroutines.Remove(routineKey);
        }
    }

    // ===== Private Helpers =====

    /// <summary>
    /// Collects the 1-based line indices covering every point in <paramref name="points"/>
    /// and invokes <see cref="OnSendLinesFromView"/> with that set.
    /// A single line command refreshes all 30 cells in a 4-pixel-tall row in one ACK-gated
    /// operation, replacing up to 30 individual cell commands.
    /// </summary>
    private void BatchSendLines(IEnumerable<Vector2Int> points)
    {
        var lines = new HashSet<int>();
        foreach (var p in points)
            lines.Add(p.y / RTDConstants.CELL_HEIGHT + 1);
        if (lines.Count > 0)
            OnSendLinesFromView?.Invoke(lines);
    }

    private void TrackKeyForHand(string hand, string key)
    {
        if (!_handHighlightKeys.ContainsKey(hand))
            _handHighlightKeys[hand] = new HashSet<string>();
        _handHighlightKeys[hand].Add(key);
    }

    private void StartRoutine(string key, IEnumerator routine)
    {
        if (key.StartsWith("Pulse", StringComparison.OrdinalIgnoreCase))
            OnCancelTail?.Invoke();
        else if (key.StartsWith("Show_", StringComparison.OrdinalIgnoreCase))
            OnCancelUnifiedTailOnly?.Invoke();

        if (_activeCoroutines.TryGetValue(key, out var old) && old != null)
            _coroutineHost.StopCoroutine(old);

        _activeCoroutines[key] = _coroutineHost.StartCoroutine(routine);
    }

    private void StopRoutine(string key)
    {
        if (_activeCoroutines.TryGetValue(key, out var cr))
        {
            if (cr != null) _coroutineHost.StopCoroutine(cr);
            _activeCoroutines.Remove(key);
        }
    }

    /// <summary>
    /// Returns the 8-pixel surrounding box for agent highlights.
    /// </summary>
    private List<Vector2Int> GetBoxNeighbours(int x, int y)
    {
        var result = new List<Vector2Int>();
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0)
                    RTDMarkHighlighter.TryAdd(x + dx, y + dy, result);
        return result;
    }

    /// <summary>
    /// Returns shape-specific neighbours for agent PulseShape calls.
    /// </summary>
    private List<Vector2Int> GetNeighbours(int x, int y, HighlightShape shape)
    {
        var result = new List<Vector2Int>();

        if (shape == HighlightShape.Box)
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (dx != 0 || dy != 0)
                        RTDMarkHighlighter.TryAdd(x + dx, y + dy, result);
        }
        else if (shape == HighlightShape.Cross)
        {
            RTDMarkHighlighter.TryAdd(x + 1, y,     result);
            RTDMarkHighlighter.TryAdd(x - 1, y,     result);
            RTDMarkHighlighter.TryAdd(x,     y + 1, result);
            RTDMarkHighlighter.TryAdd(x,     y - 1, result);
        }
        else if (shape == HighlightShape.Line)
        {
            for (int yy = 0; yy < RTDConstants.PIXEL_ROWS; yy++)
                RTDMarkHighlighter.TryAdd(x, yy, result);
        }
        else if (shape == HighlightShape.Focus)
        {
            RTDMarkHighlighter.TryAdd(x, y - 1, result);
            RTDMarkHighlighter.TryAdd(x, y + 1, result);
        }
        // HighlightShape.Pins: no neighbours

        return result;
    }

    private Dictionary<Vector2Int, int> HighlightWithLocalIsolation(Vector2Int center, List<Vector2Int> neighbours)
    {
        var isolated = new Dictionary<Vector2Int, int>();
        var allHighlighted = new HashSet<Vector2Int>(neighbours) { center };

        for (int dx = -LOCAL_ISOLATION_RADIUS; dx <= LOCAL_ISOLATION_RADIUS; dx++)
            for (int dy = -LOCAL_ISOLATION_RADIUS; dy <= LOCAL_ISOLATION_RADIUS; dy++)
            {
                int nx = center.x + dx, ny = center.y + dy;
                var pt = new Vector2Int(nx, ny);

                if ((dx != 0 || dy != 0) &&
                    nx >= 0 && nx < RTDConstants.PIXEL_COLS &&
                    ny >= 0 && ny < RTDConstants.PIXEL_ROWS &&
                    !allHighlighted.Contains(pt) &&
                    !isolated.ContainsKey(pt) &&
                    _bufferManager.GetViewBit(ny, nx))
                {
                    isolated[pt] = _bufferManager.Overlay[ny, nx];
                }
            }

        if (isolated.Count > 0)
            Debug.Log($"[LocalIsolation] Pins isolated: {string.Join(", ", isolated.Keys.Select(v => $"({v.x},{v.y})"))}");
        else
            Debug.Log("[LocalIsolation] No pins isolated");

        return isolated;
    }
}
