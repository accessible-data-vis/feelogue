using System;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class AgentResponseHandler : MonoBehaviour
{
    private const string FALLBACK_SPEECH = "I didn't quite get that, please try again.";
    private const float PULSE_INTERVAL = 2.0f;

    [SerializeField] private MonoBehaviour agentWakeWordService;
    private InterfaceAgentWakeWord _agentWakeWord;
    [SerializeField] private MonoBehaviour textToSpeechService;
    private InterfaceTextToSpeech _textToSpeech;
    [SerializeField] private MonoBehaviour speechToTextService;
    private InterfaceSpeechToText _speechToText;
    [SerializeField] private MonoBehaviour rtdUpdaterService;
    private InterfaceRTDUpdater _rtdUpdater;
    [SerializeField] private MonoBehaviour audioToneManagerService;
    private InterfaceAudioToneManager _audioToneManager;
    [SerializeField] private MonoBehaviour buttonGUIService;
    private InterfaceButtonGUI _buttonGUI;
    [SerializeField] private MonoBehaviour graphVisualizerService;
    private InterfaceGraphVisualizer _graphVisualizer;
    [SerializeField] private MonoBehaviour mqttManagerService;
    private InterfaceMQTTManager _mqttManager;
    [SerializeField] private SpeechSettings speechSettings;
    [SerializeField] private VegaChartLoader vegaChartLoader;

    [SerializeField] private PositionReport leftPositionReport;
    [SerializeField] private PositionReport rightPositionReport;

    private JObject _lastAgentResponseJson;

    private RTDTextChunkingController _chunkingController;

    void Awake()
    {
        _agentWakeWord = agentWakeWordService as InterfaceAgentWakeWord ?? throw new InvalidOperationException("agentWakeWordService not assigned!");
        _textToSpeech = textToSpeechService as InterfaceTextToSpeech ?? throw new InvalidOperationException("textToSpeechService not assigned!");
        _speechToText = speechToTextService as InterfaceSpeechToText ?? throw new InvalidOperationException("speechToTextService not assigned!");
        _rtdUpdater = rtdUpdaterService as InterfaceRTDUpdater ?? throw new InvalidOperationException("rtdUpdaterService not assigned!");
        _audioToneManager = audioToneManagerService as InterfaceAudioToneManager ?? throw new InvalidOperationException("audioToneManagerService not assigned!");
        _buttonGUI = buttonGUIService as InterfaceButtonGUI ?? throw new InvalidOperationException("buttonGUIService not assigned!");
        _graphVisualizer = graphVisualizerService as InterfaceGraphVisualizer ?? throw new InvalidOperationException("graphVisualizerService not assigned!");
        _mqttManager = mqttManagerService as InterfaceMQTTManager ?? throw new InvalidOperationException("mqttManagerService not assigned!");

        _chunkingController = new RTDTextChunkingController(
            _textToSpeech,
            _rtdUpdater,
            speechSettings,
            HandleNodePulsing
        );

        _agentWakeWord.WakeWordDetected += OnWakeWordDetected;
        _mqttManager.MessageReceived += OnMQTTMessage;
    }

    private void OnWakeWordDetected()
    {
        UnityEngine.Debug.Log("Detected Wake Word!");
        _audioToneManager.PlayStartTone();
        _agentWakeWord.PauseWakeWord();
        _speechToText.StartSpeechRecognition(transcript => OnSpeechResult(transcript, true), true, "", OnRecognitionComplete);
    }

    public void HandleButtonSpeech(string transcript, bool fromWakeWord)
    {
        OnSpeechResult(transcript, fromWakeWord);
    }

    private void OnSpeechResult(string transcript, bool fromWakeWord)
    {
        UnityEngine.Debug.Log($"Speech Recognized: {transcript}");

        if (IsTranscriptCancelled(transcript))
        {
            HandleCancelledTranscript();
            return;
        }

        JObject parsedTranscript = ParseTranscript(transcript);
        if (parsedTranscript == null)
        {
            UnityEngine.Debug.LogWarning("Failed to parse transcript JSON, aborting.");
            return;
        }

        if (fromWakeWord)
            _audioToneManager.PlayEndTone();

        if (_buttonGUI.GetWaitToneMode())
            _audioToneManager.LoopWaitTone();

        if (fromWakeWord && !_speechToText.GetSkipStatus())
            BlinkCursor();

        string combinedMessageJson = ComposeCombinedMessage(parsedTranscript);
        UnityEngine.Debug.Log($"Final Combined Message: {combinedMessageJson}");

        if (_speechToText.GetSkipStatus())
            HandleSkippedMessage();
        else
            PublishAndReset(combinedMessageJson);
    }

    private bool IsTranscriptCancelled(string transcript)
    {
        return string.IsNullOrWhiteSpace(transcript) || transcript == "cancelled transcript";
    }

    private JObject ParseTranscript(string transcript)
    {
        try
        {
            return JObject.Parse(transcript.Trim());
        }
        catch (JsonReaderException ex)
        {
            UnityEngine.Debug.LogWarning($"Transcript JSON parse error: {ex.Message}");
            return null;
        }
    }

    public void BlinkCursor()
    {
        int cursorCol = RTDGridConstants.GRID_WIDTH - 1;
        int cursorRow = RTDGridConstants.GRID_HEIGHT - 1;
        List<Vector2Int> nodePosition = new List<Vector2Int> { new Vector2Int(cursorCol, cursorRow) };

        switch (_buttonGUI.GetProcessingMode())
        {
            case ProcessingMode.PulsePins:
                _rtdUpdater.PulsePins(nodePosition, 1.0f, -1f);
                break;
            case ProcessingMode.PulseShape:
                _rtdUpdater.PulseShape(cursorCol, cursorRow, HighlightShape.Line, PULSE_INTERVAL, -1f);
                break;
            case ProcessingMode.PulseLoadingBar:
                _rtdUpdater.PulseLoadingBar();
                break;
            default:
                _rtdUpdater.PulsePins(nodePosition, 1.0f, -1f);
                break;
        }
        UnityEngine.Debug.Log("Blinking command sent to start cursor");
    }

    private void HandleCancelledTranscript()
    {
        UnityEngine.Debug.LogWarning("Skipping further processing due to cancelled transcript.");
        _speechToText.SetSkipStatus(false);
    }

    private void OnRecognitionComplete()
    {
        UnityEngine.Debug.Log("Speech-to-Text Finished.");
        _agentWakeWord.ResumeWakeWord();
    }

    private string ComposeCombinedMessage(JObject parsedTranscript)
    {
        bool bothAnchored = HasValidTouchData(leftPositionReport) && HasValidTouchData(rightPositionReport);

        var combinedMessage = new
        {
            user_request_for_agent = new
            {
                transcript = ExtractTranscriptData(parsedTranscript),
                touchdata = new
                {
                    left_touch = GetTouchData(leftPositionReport),
                    right_touch = GetTouchData(rightPositionReport)
                },
                highlighted_context = bothAnchored ? (object)"No highlight" : GetHighlightedContext()
            }
        };
        return JsonConvert.SerializeObject(combinedMessage, Formatting.Indented);
    }

    private bool HasValidTouchData(PositionReport positionReport)
    {
        if (positionReport == null) return false;
        var touchDataJson = positionReport.GetLastTouchData();
        return !string.IsNullOrEmpty(touchDataJson) && touchDataJson.StartsWith("{");
    }

    private object GetTouchData(PositionReport positionReport)
    {
        if (positionReport == null)
        {
            UnityEngine.Debug.Log("GetTouchData: positionReport is null");
            return "No touch";
        }

        var touchDataJson = positionReport.GetLastTouchData();
        UnityEngine.Debug.Log($"GetTouchData: touchDataJson = {(string.IsNullOrEmpty(touchDataJson) ? "EMPTY" : touchDataJson.Substring(0, Math.Min(50, touchDataJson.Length)))}...");

        if (string.IsNullOrEmpty(touchDataJson) || !touchDataJson.StartsWith("{"))
            return "No touch";

        return JsonConvert.DeserializeObject<dynamic>(touchDataJson);
    }

    private object GetHighlightedContext()
    {
        // Only include navigation highlights (NOT gesture highlights)
        // Gesture highlights should appear in touchdata instead
        var navPoint = _rtdUpdater.GetHighlightedPoint();
        bool isFromNavigation = _rtdUpdater.IsHighlightFromNavigation();

        if (!navPoint.HasValue || !isFromNavigation)
            return "No highlight";

        var navNodes = _graphVisualizer.GetMatchingNodes(new HashSet<Vector2Int> { navPoint.Value });
        if (navNodes.Count == 0)
            return "No highlight";

        var node = navNodes[0];
        return new
        {
            node_count = 1,
            nodes = new Dictionary<string, object>
            {
                [node.id] = new
                {
                    node_xy = new[] { navPoint.Value.x, navPoint.Value.y },
                    node_values = node.values,
                    probability = 1.0,
                    source = "navigation"
                }
            }
        };
    }

    private object ExtractTranscriptData(JObject parsedTranscript)
    {
        return new
        {
            text_transcript = parsedTranscript["transcript"]?.ToString() ?? "",
            confidence = parsedTranscript["confidence"]?.ToObject<float>() ?? 0f,
            words = parsedTranscript["words"]?.ToObject<List<dynamic>>()
        };
    }

    private void HandleSkippedMessage()
    {
        UnityEngine.Debug.Log("[SKIP] Skipping message send due to 'skip' transcript.");
        _speechToText.SetSkipStatus(false);
    }

    private void PublishAndReset(string messageJson)
    {
        _mqttManager.PublishInteraction(messageJson);
        // Don't reset touch data here - let it persist for follow-up questions
        // It will be reset when a new gesture occurs (HandleDoubleTapResponse)
    }

    private void OnMQTTMessage(string topic, string payload)
    {
        if (topic == "agent_out")
        {
            HandleAgentOutput(payload);
        }
    }

    private void HandleAgentOutput(string payload)
    {
        try
        {
            _lastAgentResponseJson = JObject.Parse(payload);
            _rtdUpdater.StopPulsePins();
            UnityEngine.Debug.Log("Blinking command sent to stop cursor");

            string responseText = ExtractResponseText(_lastAgentResponseJson);

            string rtdCommand = _lastAgentResponseJson["agent_response_for_user"]?["rtd_command"]?.ToString()?.Trim() ?? "0";
            bool followupStage = _lastAgentResponseJson["agent_response_for_user"]?["followup_stage"]?.ToObject<bool>() ?? false;
            UnityEngine.Debug.Log("RTD Command " + rtdCommand);
            UnityEngine.Debug.Log("Response_Text " + responseText);

            if (!string.IsNullOrEmpty(responseText))
            {
                if (followupStage && _buttonGUI.GetFollowUpMode())
                {
                    UnityEngine.Debug.Log("Follow-up detected - delaying agent reinvoke until TTS completes...");
                    HandleTextToSpeech(responseText, () =>
                    {
                        UnityEngine.Debug.Log("TTS finished. Starting follow-up recording...");
                        OnWakeWordDetected();
                        HandleRTDCommand(rtdCommand, _lastAgentResponseJson);  // reset AFTER recording starts
                    });
                }
                else
                {
                    HandleTextToSpeech(responseText);
                    HandleRTDCommand(rtdCommand, _lastAgentResponseJson);
                }
            }
            else
            {
                HandleMissingResponseText();
            }
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogWarning($"JSON Parsing Error: {ex.Message}\nPayload: {payload}");
        }
    }

    private string ExtractResponseText(JObject json)
    {
        string responseText = json["agent_response_for_user"]?["response_text"]?.ToString() ?? "";
        // Handle double-encoded JSON
        if (responseText.StartsWith("{"))
        {
            try
            {
                var asJson = JObject.Parse(responseText);
                return asJson["response_text"]?.ToString() ?? responseText;
            }
            catch
            {
                /* ignore parse errors, use raw string */
            }
        }
        return responseText;
    }

    public void ToggleChunkMode()
    {
        _chunkingController.ToggleChunkMode();
    }

    public bool IsChunkModeEnabled()
    {
        return _chunkingController.IsChunkModeEnabled();
    }

    private void HandleTextToSpeech(string responseText, Action onComplete = null)
    {
        UnityEngine.Debug.Log($"[TTS] Extracted response_text: {responseText}");

        if (_buttonGUI.GetWaitToneMode())
            _audioToneManager.StopWaitTone();

        _chunkingController.ProcessText(_lastAgentResponseJson, responseText, onComplete);
    }

    private void HandleRTDCommand(string rtdCommand, JObject json)
    {
        if (rtdCommand.Contains("-"))
        {
            UnityEngine.Debug.Log("[CHART] Chart command detected, delegating to ButtonGUI");
            _buttonGUI.HandleChartCommand(rtdCommand);
        }
        else
        {
            ResetTouchData();
        }
    }

    private void ResetTouchData()
    {
        leftPositionReport?.ResetLastTouchData();
        rightPositionReport?.ResetLastTouchData();
    }

    private void HandleNodePulsing(JObject json, string text, bool strictToChunk)
    {
        var nodes = json["agent_response_for_user"]?["nodes"] as JObject;
        var matchedCoords = new List<Vector2Int>();

        if (nodes == null || string.IsNullOrEmpty(text))
        {
            UnityEngine.Debug.LogWarning("No nodes or reference text to match against.");
            return;
        }

        string normalizedRef = NormalizeForMatch(text);

        foreach (var node in nodes)
        {
            var nodeObj = (JObject)node.Value;

            var xValue = nodeObj["x"]?.ToString();
            var yValue = nodeObj["y"]?.ToString();
            var zValue = nodeObj["z"]?.ToString();

            UnityEngine.Debug.Log($"[NODE] Agent node: x={xValue ?? "null"}, y={yValue ?? "null"}, z={zValue ?? "null"}");

            // Need at least one field to match
            if (xValue == null && yValue == null && zValue == null) continue;

            // In chunk mode, skip if none of the provided values appear in the chunk text
            if (strictToChunk)
            {
                var termsList = new[] { xValue, yValue, zValue }.Where(v => v != null).Select(v => NormalizeForMatch(v));
                bool mentionedInChunk = termsList.Any(t => normalizedRef.Contains(t));
                if (!mentionedInChunk)
                {
                    UnityEngine.Debug.Log($"[NODE] Skipping node x={xValue}, y={yValue}, z={zValue} - not in chunk");
                    continue;
                }
            }

            // Search all data-point nodes -- match by whatever fields the agent provided
            var allNodes = _graphVisualizer.GetNodes();
            bool matchedAny = false;

            foreach (var kvp in allNodes)
            {
                var nc = kvp.Value.GetComponent<NodeComponent>();
                // Future: support "type": "axis" in agent node to match axis ticks instead
                if (nc == null || !nc.id.StartsWith("data-point") || nc.values == null) continue;

                bool matches = true;
                int fieldsMatched = 0;

                // x: match against any string value in the node
                if (xValue != null)
                {
                    bool xFound = nc.values.Any(v => v.Value?.ToString() == xValue);
                    if (!xFound) { matches = false; }
                    else fieldsMatched++;
                }

                // y: match as numeric (float comparison) against any numeric value
                if (yValue != null && matches)
                {
                    if (float.TryParse(yValue, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out float yNum))
                    {
                        bool yFound = nc.values.Any(v =>
                        {
                            if (v.Value == null) return false;
                            if (float.TryParse(v.Value.ToString(), System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out float nv))
                                return Mathf.Approximately(nv, yNum);
                            return false;
                        });
                        if (!yFound) { matches = false; }
                        else fieldsMatched++;
                    }
                    else
                    {
                        // y is a string -- match as string
                        bool yFound = nc.values.Any(v => v.Value?.ToString() == yValue);
                        if (!yFound) { matches = false; }
                        else fieldsMatched++;
                    }
                }

                // z: match against any string value (typically series/color field)
                if (zValue != null && matches)
                {
                    bool zFound = nc.values.Any(v => v.Value?.ToString() == zValue);
                    if (!zFound) { matches = false; }
                    else fieldsMatched++;
                }

                if (matches && fieldsMatched > 0 && nc.xy != null && nc.xy.Length == 2)
                {
                    matchedCoords.Add(new Vector2Int(nc.xy[0], nc.xy[1]));
                    UnityEngine.Debug.Log($"[NODE] Matched {nc.id}: x={xValue}, y={yValue}, z={zValue} -> ({nc.xy[0]},{nc.xy[1]})");
                    matchedAny = true;
                }
            }

            if (!matchedAny)
            {
                UnityEngine.Debug.LogWarning($"[NODE] No match for agent node: x={xValue}, y={yValue}, z={zValue}");
            }
        }

        // Highlight the resulting coords
        if (matchedCoords.Count > 0)
        {
            var duration = IsChunkModeEnabled() ? -1f : _buttonGUI.GetBlinkDuration();
            _rtdUpdater.ShowTouchHighlights(matchedCoords.Distinct().ToList(), HighlightShape.Box, duration, "agent");
        }
        else
        {
            UnityEngine.Debug.LogWarning("[NODE] No pins matched for pulsing.");
        }
    }

    private string NormalizeForMatch(string text)
    {
        var norm = new string((text ?? string.Empty).Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c == '.' || c == ',' || c == '%').ToArray()).ToLowerInvariant();
        return string.Join(" ", norm.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
    }

    public void HandleBlinkResponse(Vector2Int coord)
    {
        var duration = IsChunkModeEnabled() ? -1f : _buttonGUI.GetBlinkDuration();
        _rtdUpdater.ShowShape(coord.x, coord.y, HighlightShape.Box, duration, "agent");
    }

    private void HandleMissingResponseText()
    {
        UnityEngine.Debug.LogWarning("response_text not found in the received JSON.");
        _textToSpeech.ConvertTextToSpeech(FALLBACK_SPEECH, speechSettings, null);
    }

    public void AdvanceToNextChunk()
    {
        _chunkingController.AdvanceToNextChunk();
    }

    public void StepBackInChunk()
    {
        _chunkingController.StepBackInChunk();
    }
}
