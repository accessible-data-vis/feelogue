using System;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

/// <summary>
/// Orchestrates all responses to valid touch events - visual highlights, audio feedback,
/// braille display, and navigation updates.
/// </summary>
public class TouchResponseOrchestrator
{
    // Dependencies
    private InterfaceRTDUpdater _rtdUpdater;
    private InterfaceTextToSpeech _textToSpeech;
    private InterfaceButtonGUI _buttonGUI;
    private SpeechSettings _speechSettings;

    // State
    private string _lastTouchData = "";
    private bool _lastTouchWasDoubleTap = false;
    private Vector2Int? _currentSpeakingPoint = null;

    public TouchResponseOrchestrator(
        InterfaceRTDUpdater rtdUpdater,
        InterfaceTextToSpeech textToSpeech,
        InterfaceButtonGUI buttonGUI,
        SpeechSettings speechSettings)
    {
        _rtdUpdater = rtdUpdater;
        _textToSpeech = textToSpeech;
        _buttonGUI = buttonGUI;
        _speechSettings = speechSettings;
    }


    /// <summary>
    /// Handles pin blinking for single-tap mode.
    /// </summary>
    public void HandlePinBlink(HashSet<Vector2Int> nodePositions)
    {
        if (!_buttonGUI.GetBlinkTapState() || _buttonGUI.GetDoubleTapState())
            return;

        _rtdUpdater.PulsePins(nodePositions, 0.5f, 2.0f);
    }

    /// <summary>
    /// Handles double-tap responses - highlights, navigation, braille, and TTS.
    /// </summary>
    public void HandleDoubleTapResponse(
        List<NodeComponent> matchingNodes,
        List<float> probabilities,
        Vector2 mostLikelyPin,
        List<Vector2Int> highConfidencePositions,
        string hand,
        string serializedTouchData,
        string fingerName)
    {
        AppLog.Info(LogArea.Touch, $"DOUBLE TAP detected on {fingerName}!");

        if (highConfidencePositions.Count == 0)
        {
            UnityEngine.Debug.LogWarning($"[DoubleTap] No high-confidence positions (threshold 0.2), touch context not stored for the agent.");
            return;
        }

        _rtdUpdater.CancelPendingPresentation();   // a double tap counts as input
        Vector2Int coord = Vector2Int.RoundToInt(mostLikelyPin);

        // Check if we're already speaking for this same point
        if (_textToSpeech.IsSpeaking() && _currentSpeakingPoint.HasValue && _currentSpeakingPoint.Value == coord)
        {
            AppLog.Detail(LogArea.Touch, $"Ignoring duplicate double-tap on {coord} - already speaking for this point");
            return;
        }

        // Commit touch data only once we know the full response will fire
        _lastTouchData = serializedTouchData;
        _lastTouchWasDoubleTap = true;

        // Visual highlight
        ShowGestureHighlight(highConfidencePositions, hand);

        // Navigation update
        _rtdUpdater.SetNavigationIndexOnly(coord);

        // Speech and braille each follow their GUI toggle.
        bool speak = _buttonGUI.GetValueAudioState();
        bool braille = _buttonGUI.GetValueBrailleState();
        if (speak || braille)
        {
            string label = _rtdUpdater.FormatValuesForTTS(matchingNodes, probabilities);
            if (string.IsNullOrWhiteSpace(label))
            {
                // TTS doesn't call back for empty text, so return before setting
                // _currentSpeakingPoint.
                UnityEngine.Debug.LogWarning($"[DoubleTap] Empty label for {coord}, nothing spoken or brailled.");
                return;
            }
            if (braille)
                _rtdUpdater.DisplayBrailleLabel(label);
            if (speak)
            {
                // Track which point we're speaking for and clear it when done
                _currentSpeakingPoint = coord;
                _textToSpeech.ConvertTextToSpeech(label, _speechSettings, () => {
                    _currentSpeakingPoint = null;
                });
            }
        }
    }

    /// <summary>
    /// Creates serialized JSON representation of touch data.
    /// </summary>
    public string SerializeTouchData(List<NodeComponent> matchingNodes, Dictionary<string, object> nodesDict)
    {
        if (matchingNodes == null)
            matchingNodes = new List<NodeComponent>();
        if (nodesDict == null)
            nodesDict = new Dictionary<string, object>();

        var touchData = new {
            node_count = matchingNodes.Count,
            nodes = nodesDict,
            touch_timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        return JsonConvert.SerializeObject(touchData, Formatting.Indented);
    }

    /// <summary>
    /// Gets the last touch data (only returns if it was a double-tap in double-tap mode).
    /// </summary>
    public string GetLastTouchData()
    {
        return _lastTouchWasDoubleTap ? _lastTouchData : null;
    }

    /// <summary>
    /// Resets the last touch data state.
    /// </summary>
    public void ResetLastTouchData()
    {
        _lastTouchData = "";
        _lastTouchWasDoubleTap = false;
    }

    // Private helpers

    private void ShowGestureHighlight(List<Vector2Int> positions, string hand)
    {
        _rtdUpdater.ShowTouchHighlights(positions, HighlightShape.Box, -1f, hand);
    }

}
