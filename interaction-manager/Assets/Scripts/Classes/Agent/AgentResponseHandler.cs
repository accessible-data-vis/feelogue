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

    // ===== Speech input =====

    private void OnWakeWordDetected()
    {
        AppLog.Info(LogArea.Agent, "Detected Wake Word!");
        _rtdUpdater.CancelPendingPresentation();   // starting to ask counts as input
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
        AppLog.Info(LogArea.Agent, $"Speech Recognized: {transcript}");

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
        AppLog.Detail(LogArea.Agent, $"Final Combined Message: {combinedMessageJson}");

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
        AppLog.Detail(LogArea.Agent, "Blinking command sent to start cursor");
    }

    private void HandleCancelledTranscript()
    {
        UnityEngine.Debug.LogWarning("Skipping further processing due to cancelled transcript.");
        _speechToText.SetSkipStatus(false);
    }

    private void OnRecognitionComplete()
    {
        AppLog.Detail(LogArea.Speech, "Speech-to-Text Finished.");
        _agentWakeWord.ResumeWakeWord();
    }

    private string ComposeCombinedMessage(JObject parsedTranscript)
    {
        // Touches and the navigation highlight all go to the agent, each with a
        // timestamp: "this" means the newest, "these" means all of them.
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
                highlighted_context = GetHighlightedContext(),
                // So a question that names no series is about the layer on the display.
                presentation = vegaChartLoader != null ? vegaChartLoader.CurrentPresentationLayer() : null
            }
        };
        return JsonConvert.SerializeObject(combinedMessage, Formatting.Indented);
    }

    private object GetTouchData(PositionReport positionReport)
    {
        if (positionReport == null)
        {
            AppLog.Detail(LogArea.Agent, "GetTouchData: positionReport is null");
            return "No touch";
        }

        var touchDataJson = positionReport.GetLastTouchData();
        AppLog.Detail(LogArea.Agent, $"GetTouchData: touchDataJson = {(string.IsNullOrEmpty(touchDataJson) ? "EMPTY" : touchDataJson.Substring(0, Math.Min(50, touchDataJson.Length)))}...");

        if (string.IsNullOrEmpty(touchDataJson) || !touchDataJson.StartsWith("{"))
            return "No touch";

        return JsonConvert.DeserializeObject<dynamic>(touchDataJson);
    }

    private object GetHighlightedContext()
    {
        // Navigation highlights only (gesture highlights go in touchdata)
        // Gesture highlights should appear in touchdata instead
        var navPoint = _rtdUpdater.GetHighlightedPoint();
        bool isFromNavigation = _rtdUpdater.IsHighlightFromNavigation();

        if (!navPoint.HasValue || !isFromNavigation)
            return "No highlight";

        // Use the node itself: an axis tick or another series can share its pin.
        var node = _rtdUpdater.GetHighlightedNode();
        if (node == null)
            return "No highlight";
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
            },
            // Like touch_timestamp, so the agent can rank referents by recency.
            highlight_timestamp = _rtdUpdater.GetHighlightAnchoredAtMs()
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
        AppLog.Info(LogArea.Agent, "Skipping message send due to 'skip' transcript.");
        _speechToText.SetSkipStatus(false);
    }

    private void PublishAndReset(string messageJson)
    {
        _mqttManager.PublishInteraction(messageJson);
        // Touch data is kept here so a follow-up can refer to the same point. It is
        // cleared after the reply (FinishRTDCommand), on a chart switch, or by the
        // next double tap.
    }

    // ===== Agent replies =====

    private void OnMQTTMessage(string topic, string payload)
    {
        if (topic == "agent_out")
        {
            // Generated layer text for the presentation, not a reply.
            if (payload.Contains("\"chart_overview_for_rtd\""))
            {
                HandleGeneratedOverview(payload);
                return;
            }
            HandleAgentOutput(payload);
        }
    }

    private void HandleGeneratedOverview(string payload)
    {
        try
        {
            var msg = JObject.Parse(payload)["chart_overview_for_rtd"];
            string dataName = msg?["data_name"]?.ToString();
            string chartType = msg?["chart_type"]?.ToString();
            // Null means the agent found nothing to describe.
            var overviewToken = msg?["overview"];
            var overview = overviewToken == null || overviewToken.Type == JTokenType.Null
                ? null : overviewToken.ToObject<Dictionary<string, string>>();
            if (vegaChartLoader != null)
                vegaChartLoader.SetGeneratedOverview(dataName, chartType, overview);
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogWarning($"[Overview] Could not read generated overview: {ex.Message}");
        }
    }

    private void HandleAgentOutput(string payload)
    {
        try
        {
            _lastAgentResponseJson = JObject.Parse(payload);
            _rtdUpdater.StopAgentHighlights();   // thinking blink and the previous answer's boxes
            AppLog.Detail(LogArea.Agent, "Blinking command sent to stop cursor");
            // A reply cancels a presentation still waiting to start. A load below
            // can request a new one.
            _rtdUpdater.CancelPendingPresentation();

            var reply = _lastAgentResponseJson["agent_response_for_user"];
            string responseText = ExtractResponseText(_lastAgentResponseJson);

            string rtdCommand = reply?["rtd_command"]?.ToString()?.Trim() ?? "0";
            bool followupStage = reply?["followup_stage"]?.ToObject<bool>() ?? false;
            string presentation = reply?["presentation"]?.ToString();
            AppLog.Detail(LogArea.Agent, "RTD Command " + rtdCommand);
            AppLog.Info(LogArea.Agent, "Reply: " + responseText);

            // "skip": the rest of the request follows as its own reply, so this load
            // doesn't start the presentation.
            if (presentation == "skip" && vegaChartLoader != null)
                vegaChartLoader.SuppressNextAutoStart();

            // Display changes first, since a redraw clears highlights.
            ApplyDisplayCommand(rtdCommand);
            // If the load didn't happen, the suppression mustn't carry over to the next one.
            if (presentation == "skip" && vegaChartLoader != null)
                vegaChartLoader.AllowAutoStart();
            RevealAnswerSeries(reply?["nodes"] as JObject);

            if (!string.IsNullOrEmpty(responseText))
            {
                if (followupStage && _buttonGUI.GetFollowUpMode())
                {
                    AppLog.Info(LogArea.Agent, "Follow-up detected - delaying agent reinvoke until TTS completes...");
                    HandleTextToSpeech(responseText, () =>
                    {
                        AppLog.Info(LogArea.Agent, "TTS finished. Starting follow-up recording...");
                        OnWakeWordDetected();
                        // Keep the touched point for the follow-up question.
                        FinishRTDCommand(rtdCommand, preserveTouchContext: true);
                    });
                }
                else
                {
                    HandleTextToSpeech(responseText);
                    FinishRTDCommand(rtdCommand);
                }
                // Starts once this reply has been spoken.
                if (presentation == "start" && vegaChartLoader != null)
                    vegaChartLoader.RequestPresentation();
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
        AppLog.Detail(LogArea.Agent, $"Extracted response_text: {responseText}");

        if (_buttonGUI.GetWaitToneMode())
            _audioToneManager.StopWaitTone();

        _chunkingController.ProcessText(_lastAgentResponseJson, responseText, onComplete);
    }

    private static JObject ParseCommand(string rtdCommand)
    {
        if (!rtdCommand.StartsWith("{")) return null;
        try { return JObject.Parse(rtdCommand); } catch (Exception) { return null; }
    }

    /// <summary>Apply a filter or chart load from the reply.</summary>
    private void ApplyDisplayCommand(string rtdCommand)
    {
        if (rtdCommand.StartsWith("{"))
        {
            if (ParseCommand(rtdCommand)?["filter"] is JObject filter && vegaChartLoader != null)
            {
                vegaChartLoader.SetAgentFilter(filter["hidden_series"]?.ToObject<List<string>>() ?? new List<string>());
                // Ending the presentation shows the new filter.
                if (_buttonGUI.GetOverviewMode()) _buttonGUI.SetOverviewMode(false);
                else vegaChartLoader.ShowAgentFilter();
            }
            return;
        }
        if (rtdCommand.Contains("-"))
        {
            AppLog.Detail(LogArea.Agent, "Chart command detected, delegating to ButtonGUI");
            // A load clears touch data itself (SelectGraphOption); an unknown chart
            // leaves the old one up, so its touch data stays valid.
            _buttonGUI.HandleChartCommand(rtdCommand);
        }
    }

    /// <summary>Clear the stored touch data after a reply, unless it should be kept.</summary>
    private void FinishRTDCommand(string rtdCommand, bool preserveTouchContext = false)
    {
        if (rtdCommand.StartsWith("{"))
        {
            // A filter keeps the touched referent; any other command clears it.
            if (!(ParseCommand(rtdCommand)?["filter"] is JObject))
                ResetTouchData();
            return;
        }
        if (rtdCommand.Contains("-")) return;   // see ApplyDisplayCommand
        if (!preserveTouchContext)
            ResetTouchData();
    }

    /// <summary>During the presentation, show the series an answer points at.</summary>
    private void RevealAnswerSeries(JObject nodes)
    {
        if (nodes == null || vegaChartLoader == null || !_rtdUpdater.IsPresentationActive) return;
        var ids = new List<string>();
        var series = new List<string>();
        foreach (var node in nodes.Properties())
        {
            if (node.Value is not JObject nodeObj) continue;
            string id = nodeObj["id"]?.ToString();
            if (!string.IsNullOrEmpty(id)) ids.Add(id);
            // Nodes without an id can still name their series.
            foreach (var prop in nodeObj.Properties())
            {
                string value = prop.Value?.ToString();
                if (!string.IsNullOrEmpty(value) && vegaChartLoader.availableSeries.Contains(value))
                    series.Add(value);
            }
        }
        vegaChartLoader.RevealSeriesFor(ids, series);
    }

    /// <summary>Clear both hands' stored touch data (also done on a chart switch).</summary>
    public void ResetTouchData()
    {
        leftPositionReport?.ResetLastTouchData();
        rightPositionReport?.ResetLastTouchData();
    }

    // ===== Highlights =====

    private void HandleNodePulsing(JObject json, string text, int chunkIndex)
    {
        var nodes = json["agent_response_for_user"]?["nodes"] as JObject;
        if (nodes == null)
        {
            // Normal for replies that highlight nothing (e.g. confirming a chart load).
            AppLog.Detail(LogArea.Agent, "Reply has no points to highlight.");
            return;
        }

        // Only drawn pins: off-screen and hidden nodes have no pin position.
        var dataPins = _graphVisualizer.GetNodes().Values
            .Select(go => go != null ? go.GetComponent<NodeComponent>() : null)
            .Where(nc => nc != null && nc.type == "data-point" && nc.visibility
                         && nc.values != null && nc.xy != null && nc.xy.Length == 2)
            .ToList();
        var matchedCoords = new List<Vector2Int>();

        foreach (var node in nodes)
        {
            if (node.Value is not JObject nodeObj)
            {
                UnityEngine.Debug.LogWarning($"[NODE] Node '{node.Key}' is not an object - skipped");
                continue;
            }

            string nodeDesc = string.Join(", ", nodeObj.Properties().Select(p => $"{p.Name}={p.Value?.ToString() ?? "null"}"));
            AppLog.Detail(LogArea.Agent, $"Agent node: {nodeDesc}");

            // Each node belongs to the chunk that mentions it. Nodes without one stay
            // lit for the whole answer; chunkIndex -1 means no chunks.
            if (chunkIndex >= 0)
            {
                var chunkToken = nodeObj["chunk"];
                if (chunkToken != null && chunkToken.Type == JTokenType.Integer && (int)chunkToken != chunkIndex)
                {
                    AppLog.Detail(LogArea.Agent, $"Skipping node {nodeDesc} - assigned to chunk {(int)chunkToken}, playing {chunkIndex}");
                    continue;
                }
            }

            // Match by row id when there is one, otherwise by value.
            string id = nodeObj["id"]?.ToString();
            var hits = !string.IsNullOrEmpty(id)
                ? dataPins.Where(nc => nc.values.TryGetValue(VegaChartLoader.RowIdField, out var v) && v?.ToString() == id).ToList()
                : dataPins.Where(nc => MatchesNodeValues(nc, nodeObj)).ToList();

            if (hits.Count == 0)
            {
                UnityEngine.Debug.LogWarning($"[NODE] No visible pin for agent node: {nodeDesc}");
                continue;
            }
            foreach (var nc in hits)
            {
                matchedCoords.Add(new Vector2Int(nc.xy[0], nc.xy[1]));
                AppLog.Detail(LogArea.Agent, $"Matched {nc.id} -> ({nc.xy[0]},{nc.xy[1]})");
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

    /// <summary>
    /// Match a node without a row id by value. "x" and "y" can match any pin value;
    /// other keys must match that field.
    /// </summary>
    private static bool MatchesNodeValues(NodeComponent nc, JObject nodeObj)
    {
        int compared = 0;
        foreach (var prop in nodeObj.Properties())
        {
            if (prop.Name == "chunk" || prop.Name == "id") continue;
            string want = prop.Value?.ToString();
            if (string.IsNullOrEmpty(want)) continue;

            bool found = (prop.Name == "x" || prop.Name == "y" || !nc.values.ContainsKey(prop.Name))
                ? nc.values.Values.Any(v => ValueEquals(v, want))
                : ValueEquals(nc.values[prop.Name], want);
            if (!found) return false;
            compared++;
        }
        return compared > 0;
    }

    private static bool ValueEquals(object have, string want)
    {
        if (have == null) return false;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // Dates arrive from the spec as DateTime; the agent sends ISO text.
        if (have is DateTime dt)
            return want.StartsWith(dt.ToString("yyyy-MM-dd", inv));
        string text = Convert.ToString(have, inv);
        if (double.TryParse(text, System.Globalization.NumberStyles.Any, inv, out double a) &&
            double.TryParse(want, System.Globalization.NumberStyles.Any, inv, out double b))
            return Math.Abs(a - b) < 1e-6 * Math.Max(1.0, Math.Abs(b));
        return text == want;
    }

    public void HandleBlinkResponse(Vector2Int coord)
    {
        var duration = IsChunkModeEnabled() ? -1f : _buttonGUI.GetBlinkDuration();
        _rtdUpdater.ShowShape(coord.x, coord.y, HighlightShape.Box, duration, "agent");
    }

    private void HandleMissingResponseText()
    {
        UnityEngine.Debug.LogWarning("response_text not found in the received JSON.");
        // HandleTextToSpeech isn't called here, so stop the wait tone.
        if (_buttonGUI.GetWaitToneMode())
            _audioToneManager.StopWaitTone();
        _textToSpeech.ConvertTextToSpeech(FALLBACK_SPEECH, speechSettings, null);
    }

    /// <summary>Play the next answer chunk; false at the last one.</summary>
    public bool AdvanceToNextChunk() => _chunkingController.AdvanceToNextChunk();

    /// <summary>Play the previous answer chunk; false at the first one.</summary>
    public bool StepBackInChunk() => _chunkingController.StepBackInChunk();
}
