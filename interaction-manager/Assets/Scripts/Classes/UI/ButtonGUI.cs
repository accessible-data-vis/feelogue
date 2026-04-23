using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

public class ButtonGUI : MonoBehaviour, InterfaceButtonGUI
{
    // interface classes
    [SerializeField] private MonoBehaviour agentWakeWordService;
    private InterfaceAgentWakeWord _agentWakeWord;
    [SerializeField] private MonoBehaviour mqttManagerService;
    private InterfaceMQTTManager _mqttManager;
    [SerializeField] private MonoBehaviour textToSpeechService;
    private InterfaceTextToSpeech _textToSpeech;
    [SerializeField] private MonoBehaviour rtdUpdaterService;
    private InterfaceRTDUpdater _rtdUpdater;
    [SerializeField] private MonoBehaviour graphVisualizerService;
    private InterfaceGraphVisualizer _graphVisualizer;
    [SerializeField] private AgentResponseHandler _agentResponseHandler;


    // core system components for use
    [SerializeField] private VegaChartLoader graphLoader;
    [SerializeField] private VegaSpecFileImporter fileImporter;

    // system controls

    [SerializeField] private Button stopAudioButton;
    [SerializeField] private Button clearRTDButton;
    [SerializeField] private Button fillRTDButton;

    // system controls - buttons with text input
    [SerializeField] private Button toggleMQTTButton;
    [SerializeField] private TextMeshProUGUI MQTTLabel;

    [SerializeField] private Button toggleDoubleTapButton;
    [SerializeField] private TextMeshProUGUI DoubleTapLabel;
    public static bool DoubleTapState = true;

    [SerializeField] private Button toggleValueAudioButton;
    [SerializeField] private TextMeshProUGUI valueAudioModeLabel;
    public static bool ValueAudioState = true;

    [SerializeField] private Button toggleValueBrailleButton;
    [SerializeField] private TextMeshProUGUI valueBrailleModeLabel;
    public static bool ValueBrailleState = true;

    [SerializeField] private Button toggleTouchSenseButton;
    [SerializeField] private TextMeshProUGUI touchSenseModeLabel;
    public static bool TouchSenseState = true;

    public static bool BlinkTapState = true;  // blink tap always on; no runtime toggle

    // controls for double tap config
    [SerializeField] private TMP_InputField tapMinLabel;
    public static float TapMinDuration = 0.3f; //0.2f;
    [SerializeField] private TMP_InputField tapMaxLabel;
    public static float TapMaxDuration = 0.6f;
    [SerializeField] private TMP_InputField doubleTapTimeLabel;
    public static float DoubleTapTimeWindow = 1.5f;
    [SerializeField] private TMP_InputField tapCoolDownLabel;
    public static float TapCoolDown = 0.2f;

    // inputs for system config
    [SerializeField] private TMP_InputField labelXYInput;
    [SerializeField] private TMP_InputField agentXYInput;
    [SerializeField] private TMP_InputField agentReqInput;
    [SerializeField] private TMP_InputField textToBrailleInput;
    [SerializeField] private TMP_InputField leftTouchInput;
    [SerializeField] private TMP_InputField rightTouchInput;
    private string _leftTouchValue = "No left touch";
    private string _rightTouchValue = "No right touch";

    [SerializeField] private TMP_Dropdown chartDropdown;
    private List<DiscoveredChart> _chartDropdownMapping; // Maps dropdown index to actual chart

    [SerializeField] private TMP_InputField blinkDurationInput;
    public static float BlinkDuration = 10f;
    [SerializeField] private Button waitToneButton;
    [SerializeField] private TextMeshProUGUI waitToneModeLabel;
    private bool waitToneMode = false;

    [SerializeField] private Button localIsolationButton;
    [SerializeField] private TextMeshProUGUI localIsolationModeLabel;
    private bool localIsolationMode = false;

    [SerializeField] private Button followUpButton;
    [SerializeField] private TextMeshProUGUI followUpModeLabel;
    private bool followUpMode = false;

    private bool toggleMode = false;

    [SerializeField] private Button overviewButton;
    [SerializeField] private TextMeshProUGUI overviewModeLabel;
    private bool overviewMode = false;

    [SerializeField] private TMP_Dropdown processingDropDown;
    public ProcessingMode SelectedProcessingMode { get; private set; }

    void Awake()
    {
        // Populate chart dropdown dynamically from discovered charts
        PopulateChartDropdown();

        _agentWakeWord = agentWakeWordService as InterfaceAgentWakeWord ?? throw new InvalidOperationException("agentWakeWordService not assigned!");
        _mqttManager = mqttManagerService as InterfaceMQTTManager ?? throw new InvalidOperationException("mqttManagerService not assigned!");
        _textToSpeech = textToSpeechService as InterfaceTextToSpeech ?? throw new InvalidOperationException("textToSpeechService not assigned!");
        _rtdUpdater = rtdUpdaterService as InterfaceRTDUpdater ?? throw new InvalidOperationException("rtdUpdaterService not assigned!");
        _graphVisualizer = graphVisualizerService as InterfaceGraphVisualizer ?? throw new InvalidOperationException("graphVisualizerService not assigned!");

        var processingOptions = new List<TMP_Dropdown.OptionData>
        {
            new TMP_Dropdown.OptionData("Pulse Pins"),
            new TMP_Dropdown.OptionData("Pulse Shape"),
            new TMP_Dropdown.OptionData("Pulse Loading Bar")
        };

        processingDropDown.ClearOptions();
        processingDropDown.AddOptions(processingOptions);
        processingDropDown.onValueChanged.AddListener(i =>
            SelectedProcessingMode = (ProcessingMode)i
        );
    }

    void Start()
    {
        // Assign listeners
        // Chart buttons
        // System buttons
        stopAudioButton.onClick.AddListener(StopAudio);
        clearRTDButton.onClick.AddListener(ClearScreen);
        fillRTDButton.onClick.AddListener(FillScreen);

        // Toggle buttons & labels
        toggleMQTTButton.onClick.AddListener(ToggleMqttModeFromButton);
        toggleDoubleTapButton.onClick.AddListener(ToggleDoubleTapMode);
        toggleValueAudioButton.onClick.AddListener(ToggleValueAudioMode);
        toggleValueBrailleButton.onClick.AddListener(ToggleValueBrailleMode);
        toggleTouchSenseButton.onClick.AddListener(ToggleTouchSenseMode);
        waitToneButton.onClick.AddListener(ToggleWaitToneMode);
        localIsolationButton.onClick.AddListener(ToggleLocalIsolationMode);
        followUpButton.onClick.AddListener(ToggleFollowUpMode);
        overviewButton.onClick.AddListener(ToggleOverviewMode);

        MQTTLabel.text = "MQTT-R";
        DoubleTapLabel.text = "Double Tap On";
        valueAudioModeLabel.text = "Audio Label On";
        valueBrailleModeLabel.text = "Braille Label On";
        touchSenseModeLabel.text = "Touch Sense On";
        waitToneModeLabel.text = "Wait Tone Off";
        localIsolationModeLabel.text = "Local Isolate Off";
        followUpModeLabel.text = "Follow Up Off";
        overviewModeLabel.text = "Ov.view Off";

        // Config labels
        tapMinLabel.text = TapMinDuration.ToString("F2");
        tapMaxLabel.text = TapMaxDuration.ToString("F2");
        doubleTapTimeLabel.text = DoubleTapTimeWindow.ToString("F2");
        tapCoolDownLabel.text = TapCoolDown.ToString("F2");
        blinkDurationInput.text = BlinkDuration.ToString("F0");

        // Config input listeners
        tapMinLabel.onEndEdit.AddListener(SetTapMinDuration);
        tapMaxLabel.onEndEdit.AddListener(SetTapMaxDuration);
        doubleTapTimeLabel.onEndEdit.AddListener(SetDoubleTapTime);
        tapCoolDownLabel.onEndEdit.AddListener(SetTapCoolDown);
        blinkDurationInput.onEndEdit.AddListener(OnBlinkDurationChanged);

        // Other input handlers
        labelXYInput.onEndEdit.AddListener(OnLabelXYEntered);
        agentXYInput.onEndEdit.AddListener(OnAgentXYEntered);
        agentReqInput.onEndEdit.AddListener(OnAgentReqEntered);
        textToBrailleInput.onEndEdit.AddListener(OnTextToBrailleInputChanged);
        leftTouchInput.onEndEdit.AddListener(OnLeftTouchEntered);
        rightTouchInput.onEndEdit.AddListener(OnRightTouchEntered);
    }

    private void PopulateChartDropdown()
    {
        RebuildChartDropdown();
        chartDropdown.onValueChanged.AddListener(OnChartDropdownChanged);
    }

    public void RefreshChartDropdown() => RebuildChartDropdown();

    private void RebuildChartDropdown()
    {
        var options = new List<TMP_Dropdown.OptionData> { new TMP_Dropdown.OptionData("No Chart") };
        _chartDropdownMapping = new List<DiscoveredChart> { null };

        var charts = graphLoader.GetAvailableCharts();
        if (charts != null && charts.Count > 0)
        {
            foreach (var chart in charts)
            {
                options.Add(new TMP_Dropdown.OptionData(chart.DisplayName));
                _chartDropdownMapping.Add(chart);
            }
        }
        else
        {
            Debug.LogWarning("No charts available to populate dropdown");
        }

        chartDropdown.ClearOptions();
        chartDropdown.AddOptions(options);
    }

    private void OnChartDropdownChanged(int index)
    {
        if (index > 0 && _chartDropdownMapping != null && index < _chartDropdownMapping.Count)
        {
            var chart = _chartDropdownMapping[index];
            if (chart != null) SelectGraphOption(chart.id);
        }
    }

    public void UpdateHighlightModes(string chartType) { }

    public void ToggleMqttModeFromButton()
    {
        _mqttManager.ToggleMQTTSource();
        if (MQTTLabel != null)
        {
            MQTTLabel.text = _mqttManager.IsUsingRemote() ? "MQTT-R" : "MQTT-L";
        }
    }

    public void StopAudio()
    {
        _agentWakeWord.StopAudio();
    }

    /// <summary>
    /// Handle chart command from agent.
    /// Format: "dataset-field-chartType" (e.g., "housing-interest rate (%)-line")
    /// </summary>
    public void HandleChartCommand(string rtdCommand)
    {
        if (string.IsNullOrEmpty(rtdCommand))
        {
            Debug.LogWarning(" Empty chart command");
            return;
        }

        Debug.Log($"Parsing chart command: {rtdCommand}");

        // Parse command format: {dataName}-{chartType} (e.g., "tslastock-line")
        int lastHyphen = rtdCommand.LastIndexOf('-');
        if (lastHyphen == -1)
        {
            Debug.LogWarning($"Invalid chart command format: {rtdCommand}");
            return;
        }

        string dataName = rtdCommand.Substring(0, lastHyphen);
        string chartType = rtdCommand.Substring(lastHyphen + 1);

        Debug.Log($"Parsed: dataName='{dataName}', chartType='{chartType}'");

        // Find matching chart by dataName and chartType
        int? chartId = graphLoader.FindChartByDataNameAndType(dataName, chartType);

        if (chartId.HasValue)
        {
            Debug.Log($"Found matching chart ID: {chartId.Value}");

            // Load the chart in Unity
            // Note: Chart loading will automatically trigger layer selection and publish layer data to agent
            SelectGraphOption(chartId.Value);
        }
        else
        {
            Debug.LogWarning($"No chart found matching dataName='{dataName}', chartType='{chartType}'");
        }
    }

    public void SelectGraphOption(int option)
    {
        Debug.Log($"Graph option {option} selected");
        graphLoader.LoadChart(option);
    }

    /// <summary>
    /// Opens file picker to import a new Vega-Lite spec.
    /// </summary>
    public void ImportVegaSpec()
    {
        if (fileImporter != null)
        {
            fileImporter.ImportVegaSpec();
        }
        else
        {
            Debug.LogError(" VegaSpecFileImporter not assigned in ButtonGUI!");
        }
    }

    // Generic toggle helper to reduce boilerplate
    private void Toggle(ref bool state, TextMeshProUGUI label, string name)
    {
        state = !state;
        label.text = $"{name} {(state ? "On" : "Off")}";
        Debug.Log($"{name} is now {(state ? "ON" : "OFF")}");
    }

    public void ToggleDoubleTapMode() => Toggle(ref DoubleTapState, DoubleTapLabel, "Double Tap");
    public bool GetDoubleTapState() => DoubleTapState;

    public void ToggleValueAudioMode() => Toggle(ref ValueAudioState, valueAudioModeLabel, "Audio Label");
    public bool GetValueAudioState() => ValueAudioState;

    public void ToggleValueBrailleMode() => Toggle(ref ValueBrailleState, valueBrailleModeLabel, "Braille Label");
    public bool GetValueBrailleState() => ValueBrailleState;

    public void ToggleTouchSenseMode() => Toggle(ref TouchSenseState, touchSenseModeLabel, "Touch Sense");
    public bool GetTouchSenseState() => TouchSenseState;

    public bool GetBlinkTapState() => BlinkTapState;

    public ProcessingMode GetProcessingMode() => SelectedProcessingMode;

    public void ToggleWaitToneMode() => Toggle(ref waitToneMode, waitToneModeLabel, "Wait Tone");
    public bool GetWaitToneMode() => waitToneMode;

    public void ToggleLocalIsolationMode() => Toggle(ref localIsolationMode, localIsolationModeLabel, "Local Isolate");
    public bool GetLocalIsolationMode() => localIsolationMode;

    public bool GetToggleMode() => toggleMode;

    public void ToggleFollowUpMode() => Toggle(ref followUpMode, followUpModeLabel, "Follow Up");
    public bool GetFollowUpMode() => followUpMode;

    public void ToggleOverviewMode() => Toggle(ref overviewMode, overviewModeLabel, "Ov.view");
    public bool GetOverviewMode() => overviewMode;

    public void ClearScreen()
    {
        _rtdUpdater.ClearScreen();
    }

    public void FillScreen()
    {
        _rtdUpdater.FillScreen();
    }

    public void SetTapMinDuration(string input)
    {
        if (float.TryParse(input, out var v)) TapMinDuration = Mathf.Max(0, v);
        tapMinLabel.text = TapMinDuration.ToString("F2");
        Debug.Log($"⏱ Tap Min Time set to {TapMinDuration} seconds.");
    }

    public float GetTapMinDuration()
    {
        return TapMinDuration;
    }

    public void SetTapMaxDuration(string input)
    {
        if (float.TryParse(input, out var v)) TapMaxDuration = Mathf.Max(TapMinDuration, v);
        tapMaxLabel.text = TapMaxDuration.ToString("F2");
        Debug.Log($"⏱ Tap Max Time set to {TapMaxDuration} seconds.");
    }

    public float GetTapMaxDuration()
    {
        return TapMaxDuration;
    }

    public void SetDoubleTapTime(string input)
    {
        if (float.TryParse(input, out var v)) DoubleTapTimeWindow = Mathf.Max(0, v);
        doubleTapTimeLabel.text = DoubleTapTimeWindow.ToString("F2");
        Debug.Log($"⏱ Double Tap Time set to {DoubleTapTimeWindow} seconds.");
    }

    public float GetDoubleTapTimeWindow()
    {
        return DoubleTapTimeWindow;
    }

    public void SetTapCoolDown(string input)
    {
        if (float.TryParse(input, out var v)) TapCoolDown = Mathf.Max(0, v);
        tapCoolDownLabel.text = TapCoolDown.ToString("F2");
        Debug.Log($"⏱ Tap Cool Down set to {TapCoolDown} seconds.");
    }

    public float GetTapCoolDown()
    {
        return TapCoolDown;
    }

    private void OnBlinkDurationChanged(string input)
    {
        if (float.TryParse(input, out var parsed))
        {
            BlinkDuration = parsed;
            Debug.Log($"⏱ Duration set to {BlinkDuration} seconds.");
        }
        else
        {
            Debug.LogWarning(" Invalid duration input.");
        }
    }

    public float GetBlinkDuration()
    {
        return BlinkDuration;
    }

    public string GetNameLog() => "";

    private void OnTextToBrailleInputChanged(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            Debug.Log($"⠿ Converting to Braille: {text}");

            _rtdUpdater.DisplayBrailleLabel(text);

            textToBrailleInput.text = "";  // Clear input field
        }
    }

    public void OnLabelXYEntered(string command)
    {
        Debug.Log(command);
        if (string.IsNullOrWhiteSpace(command))
            return;

        var tokens = command.Split(',');
        var numericValues = new List<float>();
        var stringValues = new List<string>();
        var searchInputs = new List<object>();

        foreach (var raw in tokens)
        {
            var t = raw.Trim();
            if (float.TryParse(t, out var f))
            {
                numericValues.Add(f);
            }
            else if (!string.IsNullOrEmpty(t))
            {
                stringValues.Add(t);
            }
        }

        foreach (var value in numericValues)
        {
            searchInputs.Add(new List<float> { value });
        }
        foreach (var label in stringValues)
        {
            searchInputs.Add(label);
        }

        var nodeMessages = new List<string>();

        foreach (float value in numericValues)
        {
            List<NodeComponent> matchingNodes = _graphVisualizer.GetMatchingNodesBasedOnValue(new List<float> { value });


            foreach (NodeComponent node in matchingNodes)
            {
                if (node.values != null)
                {
                    var valueDescriptions = node.values.Select(kvp => $"{kvp.Key} {kvp.Value}");
                    string description = string.Join(", ", valueDescriptions);
                    nodeMessages.Add(description);
                }
            }
        }

        string finalMessage = string.Join("; ", nodeMessages);

        if (nodeMessages.Count > 0)
        {
            if (GetValueAudioState())
            {
                Debug.Log($"Final TTS Message: {finalMessage}");
                _textToSpeech.ConvertTextToSpeech(finalMessage);

            }

            if (GetValueBrailleState())
            {
                _rtdUpdater.DisplayBrailleLabel(finalMessage);
            }
        }
        else
        {
            _textToSpeech.ConvertTextToSpeech("No matching data found.");
        }

        labelXYInput.text = "";
    }

    public void OnAgentXYEntered(string command)
    {
        Debug.Log(command);
        if (string.IsNullOrWhiteSpace(command))
            return;

        var tokens = command.Split(',');
        var numericValues = new List<float>();

        foreach (var raw in tokens)
        {
            var t = raw.Trim();
            if (float.TryParse(t, out var f))
            {
                numericValues.Add(f);
            }
        }

        var nodeMap = new Dictionary<string, object>();
        int nodeCount = 0;

        foreach (float value in numericValues)
        {
            List<NodeComponent> matchingNodes = _graphVisualizer.GetMatchingNodesBasedOnValue(new List<float> { value });

            foreach (NodeComponent node in matchingNodes)
            {
                if (node.xy != null && node.xy.Length == 2 && node.values != null)
                {
                    nodeCount++;
                    nodeMap[node.id] = new
                    {
                        node_xy = node.xy,
                        node_values = node.values,
                        probability = 1f
                    };
                }
            }
        }

        var fullMessage = new
        {
            user_request_for_agent = new
            {
                transcript = new
                {
                    text_transcript = "What is the value here?",
                    confidence = 0.975993335,
                    words = new object[]
                    {
                        new {
                            word = "What",
                            start_time = new { seconds = (int?)null, nanos = (int?)null },
                            end_time = new { seconds = 0, nanos = 400000000 },
                            confidence = 0.9991642832756042
                        },
                        new {
                            word = "is",
                            start_time = new { seconds = 0, nanos = 400000000 },
                            end_time = new { seconds = 0, nanos = 500000000 },
                            confidence = 0.9990023374557495
                        },
                        new {
                            word = "the",
                            start_time = new { seconds = 0, nanos = 500000000 },
                            end_time = new { seconds = 0, nanos = 600000000 },
                            confidence = 0.9992474317550659
                        },
                        new {
                            word = "value",
                            start_time = new { seconds = 0, nanos = 600000000 },
                            end_time = new { seconds = 1, nanos = 100000000 },
                            confidence = 0.9477907419204712
                        },
                        new {
                            word = "here?",
                            start_time = new { seconds = 1, nanos = 500000000 },
                            end_time = new { seconds = 1, nanos = 700000000 },
                            confidence = 0.923429548740387
                        }
                    }
                },
                touchdata = new
                {
                    left_touch = "No left touch",
                    right_touch = new
                    {
                        node_count = nodeCount,
                        nodes = nodeMap
                    }
                }
            }
        };

        string jsonString = JsonConvert.SerializeObject(fullMessage, Formatting.Indented);
        _mqttManager.PublishInteraction(jsonString);
        agentXYInput.text = "";
    }

    public void OnAgentReqEntered(string command)
    {
        Debug.Log(command);

        var touchdata = new
        {
            left_touch = FormatTouchNode(_leftTouchValue, "left"),
            right_touch = FormatTouchNode(_rightTouchValue, "right")
        };

        var fullMessage = new
        {
            user_request_for_agent = new
            {
                transcript = new
                {
                    text_transcript = command,
                    confidence = 0.99,
                },
                touchdata
            }
        };

        string jsonString = JsonConvert.SerializeObject(fullMessage, Formatting.Indented);
        _mqttManager.PublishInteraction(jsonString);
        agentReqInput.text = "";
    }

    private void OnLeftTouchEntered(string val)
    {
        _leftTouchValue = string.IsNullOrWhiteSpace(val) ? "No left touch" : val;
        Debug.Log(_leftTouchValue);
    }
    private void OnRightTouchEntered(string val)
    {
        _rightTouchValue = string.IsNullOrWhiteSpace(val) ? "No right touch" : val;
        Debug.Log(_rightTouchValue);
    }

    private object FormatTouchNode(string input, string which)
    {
        input = input?.Trim();
        if (string.IsNullOrWhiteSpace(input))
            return $"No {which} touch";

        var nodesDict = new Dictionary<string, object>();
        int nodeCount = 0;

        if (float.TryParse(input, out var value))
        {
            var matchedNodes = _graphVisualizer.GetMatchingNodesBasedOnValue(new List<float> { value }, requireAll: false);

            foreach (NodeComponent node in matchedNodes)
            {
                if (node.xy != null && node.xy.Length == 2 && node.values != null)
                {
                    nodeCount++;
                    nodesDict[node.id] = new
                    {
                        node_xy = node.xy,
                        node_values = node.values,
                        probability = 1.0
                    };
                }
            }
        }

        if (nodeCount == 0)
            return $"No {which} touch";

        return new
        {
            node_count = nodeCount,
            nodes = nodesDict
        };
    }
}
