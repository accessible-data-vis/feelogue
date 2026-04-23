using System;
using System.IO.Ports;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class RTDUpdater : MonoBehaviour, InterfaceRTDUpdater
{
    // ===== Constants =====
    // Command queue timing
    private const float COMMAND_ACK_TIMEOUT = 0.5f;

    // ===== Serialized Fields (Inspector) =====
    [SerializeField] private MonoBehaviour mqttManagerService;
    private InterfaceMQTTManager _mqttManager;
    [SerializeField] private MonoBehaviour textToSpeechService;
    private InterfaceTextToSpeech _textToSpeech;
    [SerializeField] private MonoBehaviour buttonGUIService;
    private InterfaceButtonGUI _buttonGUI;
    [SerializeField] private MonoBehaviour agentResponseHandlerService;
    private AgentResponseHandler _agentResponseHandler;
    [SerializeField] private MonoBehaviour graphVisualizerService;
    private InterfaceGraphVisualizer _graphVisualizer;
    [SerializeField] private VegaChartLoader graphLoader;
    [SerializeField] private bool waitForMqttBeforeSerial = true;
    [Header("RTD Device Settings")]
    [Tooltip("Uncheck this if the physical RTD device is not connected to skip serial connection attempts. Can be changed at runtime.")]
    public bool rtdDeviceConnected = true;
    [SerializeField] private Transform rtdRoot;
    [SerializeField] private SpeechSettings speechSettings;


    // Serial connection settings
    [SerializeField] private string serialPortPath = "";
    [SerializeField] private int baudRate = 115200;
    [SerializeField] private int dataBits = 8;
    [SerializeField] private Parity parity = Parity.None;
    [SerializeField] private StopBits stopBits = StopBits.One;
    [SerializeField] private int timeout = 5000;

    // ACK retry settings
    [SerializeField] private int maxSendAttempts = 3;
    [SerializeField] private float ackTimeoutSeconds = 0.3f;

    // Tail settings
    [SerializeField] private bool enableTail = true;
    [SerializeField] private float tailDuration = 3f;
    [SerializeField] private float tailLineDelay = 0.02f;
    [SerializeField] private float tailCycleDelay = 0.25f;
    [SerializeField] private float lineTailDuration = 3f;
    [SerializeField] private float lineTailLineDelay = 0.05f;
    [SerializeField] private float lineTailCycleDelay = 0.12f;

    // ===== Serial Communication =====
    private RTDSerialController _serialController;

    // ===== Buffer Manager =====
    private RTDBufferManager _bufferManager;

    // ===== Unity Visualizer =====
    private RTDUnityVisualizer _unityVisualizer;

    // ===== Streaming Controller =====
    private RTDStreamingController _streamingController;

    // ===== Navigation Controller =====
    private RTDNavigationController _navigationController;

    // ===== Highlight Manager =====
    private RTDHighlightManager _highlightManager;

    // ===== Command Queue =====
    private RTDCommandQueue _commandQueue = new RTDCommandQueue();
    private Coroutine _commandTimeoutCoroutine = null;

    // ===== Data Formatter =====
    private RTDDataFormatter _dataFormatter;

    // ===== Tail State =====
    private Coroutine _tailCoroutine;
    private Coroutine _lineTailCoroutine;
    private bool _inTail;
    private Coroutine _pulseLoadingBarCoroutine;
    private Action _pendingInteractive;
    private string _pendingBrailleHex;

    // ===== Braille & UI State =====
    // ===== Events =====
    public event Action<byte[]> ButtonPacketReceived = delegate { };

    void Awake()
    {
        _mqttManager = mqttManagerService as InterfaceMQTTManager ?? throw new InvalidOperationException("mqttManagerService not assigned!");
        _textToSpeech = textToSpeechService as InterfaceTextToSpeech ?? throw new InvalidOperationException("textToSpeechService not assigned!");
        _buttonGUI = buttonGUIService as InterfaceButtonGUI ?? throw new InvalidOperationException("buttonGUI not assigned!");
        _agentResponseHandler = agentResponseHandlerService as AgentResponseHandler ?? throw new InvalidOperationException("AgentResponseHandler not assigned!");
        _graphVisualizer = graphVisualizerService as GraphVisualizer ?? throw new InvalidOperationException("GraphVisualizer not assigned!");

        if (string.IsNullOrEmpty(serialPortPath))
            serialPortPath = EnvLoader.Get("SERIAL_PORT_PATH");

        // Initialize serial controller
        _serialController = new RTDSerialController
        {
            PortPath = serialPortPath,
            BaudRate = baudRate,
            DataBits = dataBits,
            Parity = parity,
            StopBits = stopBits,
            Timeout = timeout,
            RequireArmBeforeConnect = waitForMqttBeforeSerial
        };

        // Wire up events
        _serialController.LineAckReceived += HandleLineAck;
        _serialController.ButtonPacketReceived += (pkt) => ButtonPacketReceived(pkt);

        // Initialize buffer manager
        _bufferManager = new RTDBufferManager();

        // Initialize streaming controller
        _streamingController = new RTDStreamingController(
            this,
            SendData,
            () => rtdDeviceConnected && _serialController.IsConnected,
            BuildGraphicLineCommand,
            ackTimeoutSeconds
        );

        // Wire up streaming events
        _streamingController.StreamingCompleted += OnStreamingCompleted;
        _streamingController.StreamingFailed += OnStreamingFailed;

        // Initialize navigation controller
        _navigationController = new RTDNavigationController(_graphVisualizer);

        // Wire up navigation events
        _navigationController.NavigationChanged += OnNavigationChanged;

        // Initialize highlight manager
        _highlightManager = new RTDHighlightManager(_bufferManager, _buttonGUI, _graphVisualizer, this);

        // Initialize data formatter
        _dataFormatter = new RTDDataFormatter();

        // Wire up highlight callbacks
        _highlightManager.OnSetOverlay = SetOverlay;
        _highlightManager.OnRefreshCell = SendCellFromView;
        _highlightManager.OnSendLinesFromView = SendLinesFromView;
        _highlightManager.OnBuildLineBytesFromView = BuildLineBytesFromView;
        _highlightManager.OnQueueLineCommand = (line, start, bytes) => QueueGraphicLineCommand(line, start, bytes);
        _highlightManager.OnCancelTail = CancelTail;
        _highlightManager.OnCancelUnifiedTailOnly = CancelUnifiedTailOnly;
        _highlightManager.OnStartLineTail = StartLineTail;
        _highlightManager.OnIsStreaming = () => _streamingController.IsStreaming;
        _highlightManager.OnSetPendingInteractive = (action) => _pendingInteractive = action;
        _highlightManager.OnNavigationClearNavigation = () => _navigationController.ClearNavigation();
        _highlightManager.OnStopLineTailCoroutine = () => {
            if (_lineTailCoroutine != null)
            {
                StopCoroutine(_lineTailCoroutine);
                _lineTailCoroutine = null;
            }
        };
        _highlightManager.OnSpeakText = (text) => _textToSpeech.ConvertTextToSpeech(text, speechSettings, null);
        _highlightManager.OnCancelStreamingForInteractive = () =>
        {
            _pendingInteractive = null;
            bool wasStreaming = _streamingController.IsStreaming;
            _streamingController.CancelStreaming();
            if (wasStreaming)
                StartUnifiedTail(includeBraille: false);
        };
        _highlightManager.OnFlushCommandQueue = () =>
        {
            if (_commandTimeoutCoroutine != null)
            {
                StopCoroutine(_commandTimeoutCoroutine);
                _commandTimeoutCoroutine = null;
            }
            _commandQueue.Clear();
        };
    }

    void OnEnable()
    {
        _mqttManager.MQTTConnected += HandleMqttConnected;
    }

    void OnDisable()
    {
        _mqttManager.MQTTConnected -= HandleMqttConnected;
    }

    void Start()
    {
        // Initialize Unity visualizer
        _unityVisualizer = new RTDUnityVisualizer(rtdRoot, _bufferManager, _highlightManager.ActiveHighlightPoints, _graphVisualizer);
        _unityVisualizer.Initialize();

        var active = _mqttManager.GetActiveMQTTInstance();
        if (waitForMqttBeforeSerial && active != null && active.isConnected)
        {
            ArmSerialAndSync();
        }
    }

    private void HandleMqttConnected()
    {
        if (!waitForMqttBeforeSerial) return;
        var active = _mqttManager.GetActiveMQTTInstance();
        if (active != null && active.isConnected && !_serialController.IsArmed)
            ArmSerialAndSync();
    }

    private void ArmSerialAndSync()
    {
        // Skip serial connection if RTD device is not connected
        if (!rtdDeviceConnected)
        {
            UnityEngine.Debug.Log("RTD device not connected - skipping serial connection");
            return;
        }

        _serialController.Arm();
        DisplayImageStreamed(_bufferManager.BaseImage);
        if (!string.IsNullOrEmpty(_bufferManager.LastBraille))
            SendBrailleNow(_bufferManager.LastBraille);
    }

    private void DisarmSerial()
    {
        _streamingController.CancelStreaming();
        CancelTail();
        StopPulsePins();
        _serialController.Disarm();
    }

    void OnDestroy()
    {
        _serialController?.Disarm();

    }
    void OnApplicationQuit()
    {
        _serialController?.Disarm();
    }

    void Update()
    {
        // Handle runtime changes to rtdDeviceConnected
        if (rtdDeviceConnected)
        {
            // If RTD is now connected but not armed, re-arm it
            if (!_serialController.IsArmed)
            {
                var active = _mqttManager.GetActiveMQTTInstance();
                if (!waitForMqttBeforeSerial || (active != null && active.isConnected))
                {
                    UnityEngine.Debug.Log("RTD device reconnected - attempting to establish serial connection");
                    ArmSerialAndSync();
                }
            }

            _serialController.Update();
        }
        else if (_serialController.IsArmed)
        {
            // If RTD was disconnected at runtime, disarm the serial controller
            UnityEngine.Debug.Log("RTD device disconnected - closing serial connection");
            DisarmSerial();
        }

    }

    public void DisplayImage(int[,] image)
    {
        _bufferManager.SetImage(image);
        DisplayImageStreamed(image);
    }

    /// <summary>
    /// Load a chart by ID. Delegates to VegaChartLoader (modern Vega-based pipeline).
    /// </summary>
    public void SetFileOption(int file) => graphLoader.LoadChart(file);

    // Called after LoadChart to sync the file index without triggering another reload.
    // VegaChartLoader manages its own state; no RTD action needed here.
    public void SetFileOptionWithoutReload(int file) { }

    private void SendData(byte[] data)
    {
        // Check if RTD device is configured as not connected
        if (!rtdDeviceConnected)
        {
            // Silently skip - device is intentionally not connected
            return;
        }

        if (!_serialController.IsConnected)
        {
            if (_serialController.HasGivenUp)
                UnityEngine.Debug.LogWarning("Cannot send data: Serial connection failed during startup.");
            else if (waitForMqttBeforeSerial && !_serialController.IsArmed)
                UnityEngine.Debug.LogWarning("Cannot send data: Waiting for MQTT connection.");
            else
                UnityEngine.Debug.LogWarning("Cannot send data: Serial port not open.");
            return;
        }

        _serialController.SendBytes(data);
    }

    public void FillScreen()
    {

        // 1) Fill master array
        for (int y = 0; y < RTDConstants.PIXEL_ROWS; y++)
            for (int x = 0; x < RTDConstants.PIXEL_COLS; x++)
                _bufferManager.BaseImage[y, x] = 1;

        // 2) Fill text line
        string rawHexText = string.Concat(Enumerable.Repeat("FF", 20));

        // 3) Send them all at once
        DisplayImage(_bufferManager.BaseImage);
        DisplayImageInUnityFromBase();
        SendTextLineToDot(rawHexText);
    }

    public void ClearScreen()
    {
        _navigationController.ClearNavigation();
        // 1) Zero out master array
        Array.Clear(_bufferManager.BaseImage, 0, _bufferManager.BaseImage.Length);
        // 2) Clear text line
        string filledText = new string(' ', RTDConstants.TEXT_COLS * 2);
        // 3) Send them all at once
        DisplayImage(_bufferManager.BaseImage);
        DisplayImageInUnityFromBase();
        DisplayBrailleLabel(filledText);
    }

    public void RefreshScreen()
    {
        if (_bufferManager.OriginalImage == null) return;

        StopPulsePins();
        _bufferManager.RestoreFromOriginal();
        Array.Clear(_bufferManager.Overlay, 0, _bufferManager.Overlay.Length);
        DisplayImage(_bufferManager.BaseImage);
        DisplayImageInUnityFromBase();
        DisplayBrailleLabel(_bufferManager.BaseTitle);

        _navigationController.ClearNavigation();
    }

    public Vector2Int? GetHighlightedPoint()
    {
        return _navigationController.CurrentlyHighlightedPoint;
    }

    public bool IsHighlightFromNavigation()
    {
        return _navigationController.IsHighlightFromNavigation;
    }

    public int? GetHighlightedPointIndex()
    {
        return _navigationController.CurrentDataPointIndex;
    }

    private void QueueGraphicLineCommand(int lineNumber, int startCell, byte[] dataBytes, Action onComplete = null, Action onFailure = null, bool isTailCommand = false)
    {
        // Skip queueing if RTD device is not connected
        if (!rtdDeviceConnected)
        {
            return;
        }

        var packet = BuildGraphicLineCommand(lineNumber, startCell, dataBytes);
        _commandQueue.Enqueue(packet, lineNumber, maxSendAttempts, onComplete, onFailure, isTailCommand);

        if (!_commandQueue.IsWaitingForAck)
            ProcessNextCommand();
    }

    private void SendTextCommand(byte[] dataBytes, Action onComplete = null, Action onFailure = null)
    {
        // Skip if RTD device is not connected
        if (!rtdDeviceConnected)
        {
            return;
        }

        var packet = BuildLineCommand(0, 0, dataBytes, 0x80);
        SendData(packet);
    }

    private void ProcessNextCommand()
    {
        if (_commandQueue.TryGetNextCommand(out byte[] packet, out int lineNumber))
        {
            SendData(packet);

            if (_commandTimeoutCoroutine != null)
                StopCoroutine(_commandTimeoutCoroutine);
            _commandTimeoutCoroutine = StartCoroutine(CommandAckTimeout());
        }
    }

    private IEnumerator CommandAckTimeout()
    {
        yield return new WaitForSeconds(COMMAND_ACK_TIMEOUT);

        if (_commandQueue.HandleTimeout(out byte[] retryPacket, out int retryLine))
        {
            // Retry
            SendData(retryPacket);
            _commandTimeoutCoroutine = StartCoroutine(CommandAckTimeout());
        }
        else
        {
            // Failed or no retry needed
            ProcessNextCommand();
        }
    }

    private void HandleLineAck(int ackedLine)
    {
        // Handle streaming mode (full frame updates)
        if (_streamingController.IsStreaming && ackedLine == _streamingController.WaitingForAckLine)
        {
            _streamingController.HandleStreamingAck(ackedLine);
            return;
        }

        // Handle queue mode (individual commands)
        if (_commandQueue.IsWaitingForAck)
        {
            if (_commandTimeoutCoroutine != null)
            {
                StopCoroutine(_commandTimeoutCoroutine);
                _commandTimeoutCoroutine = null;
            }

            _commandQueue.HandleAck(ackedLine);
            ProcessNextCommand();
            return;
        }

        // Unexpected ACK - let streaming controller track if it's streaming
        if (_streamingController.IsStreaming)
            _streamingController.HandleStreamingAck(ackedLine);
    }

    // ===== Navigation Delegates =====
    public void SetInterleavedNavigation(bool value)
    {
        _navigationController.SetInterleavedNavigation(value);
    }

    public void EnableDataPointNavigation(bool enable, bool navigateToFirst = true)
    {
        _navigationController.EnableDataPointNavigation(enable, navigateToFirst);
    }

    public void ResetNavigationToStart()
    {
        _navigationController.ResetNavigationToStart();
    }

    public void SetNavigationIndexOnly(Vector2Int coord)
    {
        _navigationController.SetNavigationIndexOnly(coord);
    }

    public void SetNavigationToTouchedPoint(Vector2Int coord, List<NodeComponent> matchingNodes = null, List<float> probabilities = null)
    {
        _navigationController.SetNavigationToTouchedPoint(coord, matchingNodes, probabilities);
    }

    public void NavigateNextDataPoint()
    {
        _navigationController.NavigateNextDataPoint();
    }

    public void NavigatePrevDataPoint()
    {
        _navigationController.NavigatePrevDataPoint();
    }

    public void NavigateToDataPoint(int index)
    {
        _navigationController.NavigateToDataPointByIndex(index);
    }

    public void NavigateToDataPointByValue(string xField, object xValue, string yField, object yValue)
    {
        _navigationController.NavigateToDataPointByValue(xField, xValue, yField, yValue);
    }

    // ===== Navigation Event Handler =====
    private void OnNavigationChanged(NodeComponent node, List<NodeComponent> nodes, List<float> probabilities)
    {
        int x = node.xy[0];
        int y = node.xy[1];
        var coord = new Vector2Int(x, y);

        ClearHighlights("navigation");

        // Check if gesture highlight already exists at this position
        var activeGestureHighlights = _highlightManager.GetActiveGestureHighlights();
        bool hasGestureHighlight = false;

        foreach (var hand in new[] { "left", "right" })
        {
            if (activeGestureHighlights.TryGetValue(hand, out var points))
            {
                if (points.Contains(coord))
                {
                    hasGestureHighlight = true;
                    UnityEngine.Debug.Log($"Gesture highlight already exists at ({x},{y}), skipping navigation highlight");
                    break;
                }
            }
        }

        // Only highlight if no gesture exists at this position
        if (!hasGestureHighlight)
        {
            _highlightManager.ShowTouchHighlights(new List<Vector2Int> { coord }, HighlightShape.Box, -1f, "navigation");
        }
        else
        {
            UnityEngine.Debug.Log($"Skipped navigation highlight - gesture highlight present at ({x},{y})");
        }

        // Display braille and TTS
        string label = FormatValuesForTTS(nodes, probabilities);
        DisplayBrailleLabel(label);
        _textToSpeech.ConvertTextToSpeech(label, speechSettings, null);
    }

    private byte[] BuildLineCommand(byte destId, int startCell, byte[] dataBytes, byte displayMode)
    {
        var packet = new List<byte>
        {
            0xAA, 0x55,                // Sync
            0x00, (byte)(dataBytes.Length + 6),  // Length
            destId,                    // Line number
            0x02, 0x00,                // Command type
            displayMode,               // Graphic or text mode
            (byte)startCell            // Offset
        };
        packet.AddRange(dataBytes);

        // Checksum over bytes 4..end
        byte cs = 0xA5;
        for (int i = 4; i < packet.Count; i++)
            cs ^= packet[i];
        packet.Add(cs);

        return packet.ToArray();
    }

    private byte[] BuildGraphicLineCommand(int destId, int startCell, byte[] dataBytes)
    {
        return BuildLineCommand((byte)destId, startCell, dataBytes, 0x00);
    }

    public void DisplayImageStreamed(int[,] image)
    {
        _commandQueue.Clear();
        if (_commandTimeoutCoroutine != null)
        {
            StopCoroutine(_commandTimeoutCoroutine);
            _commandTimeoutCoroutine = null;
        }

        CancelTail();
        Array.Clear(_bufferManager.Overlay, 0, _bufferManager.Overlay.Length);

        // Skip streaming if RTD device is not connected
        if (!rtdDeviceConnected)
        {
            return;
        }

        _streamingController.StartStreaming(image);
    }

    private void OnStreamingFailed()
    {
        Debug.LogError("[RTD] Streaming failed: exceeded max retries or lost serial connection.");
    }

    private void OnStreamingCompleted()
    {
        // Handle pending braille
        if (!string.IsNullOrEmpty(_pendingBrailleHex))
        {
            var hb = _pendingBrailleHex;
            _pendingBrailleHex = null;
            SendBrailleNow(hb);
        }

        // Handle pending interactive action
        if (_pendingInteractive != null)
        {
            var act = _pendingInteractive;
            _pendingInteractive = null;
            CancelTail();
            act.Invoke();
        }
        else if (enableTail)
        {
            StartUnifiedTail(includeBraille: true);
        }
    }

    public void SetPixel(int y, int x, bool raised)
    {
        _bufferManager.SetPixel(y, x, raised);
        SendCellFromView(y, x);
    }

    public void RefreshPin(Vector2Int coord)
    {
        SendCellFromView(coord.y, coord.x);
    }
    public void NextBraillePage()
    {
        if (_bufferManager.NextBraillePage())
        {
            SendTextLineToDot(_bufferManager.CurrentBrailleHex);
        }
    }

    public void NextOverviewLayer()
    {
        if (_buttonGUI.GetOverviewMode())
        {
            int maxLayers = graphLoader.GetOverviewLayerCount();
            if (_bufferManager.CurrentOverviewLayer >= maxLayers - 1)
            {
                Debug.Log("Already at last overview layer.");
                return;
            }
            _bufferManager.NextOverviewLayer(maxLayers);
            ShowOverviewLayer(_bufferManager.CurrentOverviewLayer, maxLayers);
        }
    }

    public void PrevOverviewLayer()
    {
        if (_buttonGUI.GetOverviewMode())
        {
            int maxLayers = graphLoader.GetOverviewLayerCount();
            if (_bufferManager.CurrentOverviewLayer <= 0)
            {
                Debug.Log("Already at first overview layer.");
                return;
            }
            _bufferManager.PrevOverviewLayer(maxLayers);
            ShowOverviewLayer(_bufferManager.CurrentOverviewLayer, maxLayers);
        }
    }

    private void ShowOverviewLayer(int index, int maxLayers)
    {
        var (description, found) = graphLoader.SetOverviewLayer(index);
        DisplayBrailleLabel(description);
        if (!found) return;

        bool isMultiLayer = maxLayers > 1;
        bool isFirst = index == 0;
        bool isLast = index == maxLayers - 1;

        string spokenText = isMultiLayer && isFirst ? $"First. {description}" : description;

        if (isMultiLayer && isLast)
        {
            _textToSpeech.ConvertTextToSpeech(spokenText, speechSettings, () =>
                _textToSpeech.ConvertTextToSpeech("End.", speechSettings, null));
        }
        else
        {
            _textToSpeech.ConvertTextToSpeech(spokenText, speechSettings, null);
        }
    }

    public void PrevBraillePage()
    {
        if (_bufferManager.PrevBraillePage())
        {
            SendTextLineToDot(_bufferManager.CurrentBrailleHex);
        }
    }
    private void CancelUnifiedTailOnly()
    {
        if (_tailCoroutine != null)
        {
            StopCoroutine(_tailCoroutine);
            _tailCoroutine = null;
        }
        _inTail = (_lineTailCoroutine != null);
    }

    public void StopPulsePins()
    {
        _highlightManager.StopPulsePins();
    }

    public void StopPulsePins(IEnumerable<Vector2Int> coords, float interval)
    {
        _highlightManager.StopPulsePins(coords, interval);
    }

    public void DisplayLoadingBar(float progress)
    {
        // Clamp progress between 0 and 1
        progress = Mathf.Clamp01(progress);

        int filledCells = Mathf.RoundToInt(progress * RTDConstants.TEXT_COLS);
        int emptyCells = RTDConstants.TEXT_COLS - filledCells;

        // FF = fully raised braille cell, 00 = empty
        string filled = string.Concat(Enumerable.Repeat("FF", filledCells));
        string empty = string.Concat(Enumerable.Repeat("00", emptyCells));

        string fullLine = filled + empty;

        SendTextLineToDot(fullLine);
    }

    public void PulseLoadingBar(float duration = -1f, float displayDuration = 2f, float wipeDuration = 1f)
    {
        if (_pulseLoadingBarCoroutine != null)
        {
            StopCoroutine(_pulseLoadingBarCoroutine);
            _pulseLoadingBarCoroutine = null;
        }
        _pulseLoadingBarCoroutine = StartCoroutine(PulseLoadingBarCoroutine(duration, displayDuration, wipeDuration));
    }

    private IEnumerator PulseLoadingBarCoroutine(float duration = -1f, float displayDuration = 2f, float wipeDuration = 1f)
    {
        string fullBar = string.Concat(Enumerable.Repeat("FF", RTDConstants.TEXT_COLS));
        string emptyBar = string.Concat(Enumerable.Repeat("00", RTDConstants.TEXT_COLS));

        float elapsed = 0f;

        while (duration < 0f || elapsed < duration)
        {
            SendTextLineToDot(fullBar);
            yield return new WaitForSeconds(displayDuration);
            elapsed += displayDuration;

            SendTextLineToDot(emptyBar);
            yield return new WaitForSeconds(wipeDuration);
            elapsed += wipeDuration;
        }

        // Final wipe
        SendTextLineToDot(emptyBar);
    }


    public void ShowShape(int x, int y, HighlightShape shape, float duration = -1f, string hand = "agent")
    {
        _highlightManager.ShowShape(x, y, shape, duration, hand);
    }

    public void ShowTouchHighlights(List<Vector2Int> coords, HighlightShape shape, float duration, string hand)
    {
        _highlightManager.ShowTouchHighlights(coords, shape, duration, hand);
    }

    public void ClearHighlights(string hand)
    {
        _highlightManager.ClearHighlights(hand);
    }

    // ===== Gesture Highlight Persistence =====

    public void StoreAllGestureHighlights(Func<Vector2Int, (object xValue, object yValue)?> getNodeValues)
    {
        _highlightManager.StoreAllGestureHighlights(getNodeValues);
    }

    public void RestoreAllGestureHighlights()
    {
        _highlightManager.RestoreAllGestureHighlights();
    }

    public void ClearStoredGestureValues()
    {
        _highlightManager.ClearStoredGestureValues();
    }

    public void PulseShape(int x, int y, HighlightShape shape, float interval = 1f, float duration = -1f, string hand = "agent", bool clearPrevious = true)
    {
        _highlightManager.PulseShape(x, y, shape, interval, duration, hand, clearPrevious);
    }

    // ===== PulsePins (entry) =====
    public void PulsePins(IEnumerable<Vector2Int> coords, float interval, float duration = -1f, string hand = "agent")
    {
        _highlightManager.PulsePins(coords, interval, duration, hand);
    }

    public void SetChartType(string chartType)
    {
        _highlightManager.SetChartType(chartType);
        _navigationController.SetChartType(chartType);
    }

    public void SetUseSeriesSymbols(bool value)
    {
        _highlightManager.SetUseSeriesSymbols(value);
    }

    public void SetSeriesSymbolOverrides(RTDGridConstants.SymbolType[] overrides)
    {
        _highlightManager.SetSeriesSymbolOverrides(overrides);
    }

    public void SetHighlightConfigs(HighlightConfig gesture, HighlightConfig agent, HighlightConfig nav)
    {
        _highlightManager.SetHighlightConfigs(gesture, agent, nav);
    }

    public void SetChartTitle(string title)
    {
        _bufferManager.SetTitle(title);
    }

    public void DisplayBrailleLabel(string text)
    {
        _bufferManager.SetBrailleText(text);

        if (_bufferManager.TotalBraillePages == 0)
        {
            UnityEngine.Debug.LogWarning("No braille to display. Input text was empty or invalid.");
            return;
        }

        if (BrailleTranslator.Mode == BrailleTranslator.BrailleMode.RawDotBytes)
        {
            string unicode = new string(
                System.Linq.Enumerable.Range(0, _bufferManager.CurrentBrailleHex.Length / 2)
                    .Select(i => (char)(0x2800 + Convert.ToByte(_bufferManager.CurrentBrailleHex.Substring(i * 2, 2), 16)))
                    .ToArray()).TrimEnd('⠀');
            UnityEngine.Debug.Log($"[Braille] {text}\n  Unicode: {unicode}");
        }

        // Send first page
        SendTextLineToDot(_bufferManager.CurrentBrailleHex);
    }

    public void RefreshBrailleLabel()
    {
        if (!_bufferManager.RefreshBrailleText()) return;
        if (_bufferManager.TotalBraillePages == 0) return;
        SendTextLineToDot(_bufferManager.CurrentBrailleHex);
    }

    public bool SendTextLineToDot(string hexBraille)
    {
        // 1) validate length
        if (hexBraille == null || hexBraille.Length != RTDConstants.TEXT_COLS * 2)
        {
            UnityEngine.Debug.LogError($"SendTextLineToDot: expected {RTDConstants.TEXT_COLS * 2} hex chars, got {hexBraille?.Length}");
            return false;
        }

        SendBrailleNow(hexBraille);
        return true;
    }

    private void SendBrailleNow(string hexBraille)
    {
        // Skip if RTD device is not connected
        if (!rtdDeviceConnected)
        {
            return;
        }

        try
        {
            var dataBytes = RTDBufferManager.ParseBrailleHex(hexBraille);
            var packet = BuildLineCommand(0, 0, dataBytes, 0x80);
            SendData(packet);  // Direct send
            _bufferManager.SetBrailleHex(hexBraille);
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError($"SendBrailleNow failed: {ex}");
        }
    }

    // --- Unity renderers ---
    // Ground truth (what Refresh should show)
    public void DisplayImageInUnityFromBase()
    {
        _unityVisualizer.RefreshFromBase();
    }

    private void CancelTail()
    {
        if (_tailCoroutine != null)
        {
            StopCoroutine(_tailCoroutine);
            _tailCoroutine = null;
        }

        if (_lineTailCoroutine != null)
        {
            StopCoroutine(_lineTailCoroutine);
            _lineTailCoroutine = null;
        }
        _inTail = false;
        _commandQueue.ClearTailCommands(); // flush stale tail writes that would overwrite new highlights
    }

    private void StartUnifiedTail(bool includeBraille)
    {
        if (!enableTail || _streamingController.IsStreaming)
            return;
        CancelTail();                            // only one tail at a time
        UnityEngine.Debug.Log("[Tail] start");
        _tailCoroutine = StartCoroutine(UnifiedTailCoroutine(includeBraille));
    }

    private void StartLineTail(HashSet<int> lineIndices, bool includeBraille = false)  // usually false for ShowShape
    {
        if (!enableTail || _streamingController.IsStreaming || lineIndices == null || lineIndices.Count == 0)
            return;
        if (_lineTailCoroutine != null)
            StopCoroutine(_lineTailCoroutine);
        _lineTailCoroutine = StartCoroutine(LineTailCoroutine(lineIndices, lineTailDuration, lineTailLineDelay, lineTailCycleDelay, includeBraille));
    }

    private IEnumerator LineTailCoroutine(HashSet<int> lineIndices, float duration, float perLineDelay, float cycleDelay, bool includeBraille)
    {
        _inTail = true;

        byte[] brailleBytes = null;
        if (includeBraille && !string.IsNullOrEmpty(_bufferManager.LastBraille))
            brailleBytes = RTDBufferManager.ParseBrailleHex(_bufferManager.LastBraille);

        float endAt = Time.time + duration;

        while (Time.time < endAt)
        {
            if (_streamingController.IsStreaming) break;

            foreach (var line1 in lineIndices)
            {
                if (_streamingController.IsStreaming) break;
                var bytes = BuildLineBytesFromView(line1);
                QueueGraphicLineCommand(line1, 0, bytes);
                yield return new WaitForSeconds(perLineDelay);
            }

            if (!_streamingController.IsStreaming && brailleBytes != null)
                SendTextCommand(brailleBytes);

            if (_streamingController.IsStreaming) break;
            yield return new WaitForSeconds(cycleDelay);
        }

        _inTail = false;
        _lineTailCoroutine = null;
    }

    private IEnumerator UnifiedTailCoroutine(bool includeBraille)
    {
        _inTail = true;

        // Prepare braille bytes once (if available)
        byte[] brailleBytes = null;
        if (includeBraille && !string.IsNullOrEmpty(_bufferManager.LastBraille))
            brailleBytes = RTDBufferManager.ParseBrailleHex(_bufferManager.LastBraille);

        float endAt = Time.time + tailDuration;

        while (Time.time < endAt)
        {
            if (_streamingController.IsStreaming) 
                break; // abort if a new frame starts

            // Graphics: resend all lines from current base_image
            for (int line1 = 1; line1 <= RTDConstants.NUM_LINES; line1++)
            {
                if (_streamingController.IsStreaming) break;
                var bytes = BuildLineBytesFromView(line1);
                QueueGraphicLineCommand(line1, 0, bytes, isTailCommand: true);
                yield return new WaitForSeconds(tailLineDelay);
            }

            // Braille: once per cycle
            if (!_streamingController.IsStreaming && brailleBytes != null)
                SendTextCommand(brailleBytes);

            if (_streamingController.IsStreaming) 
                break;
            yield return new WaitForSeconds(tailCycleDelay);
        }

        // Clear any remaining tail commands from queue
        _commandQueue.ClearTailCommands();

        _inTail = false;
        _tailCoroutine = null;
        UnityEngine.Debug.Log("[Tail] stop");
    }

    private byte PackCellFromView(int blockRow, int blockCol)
    {
        return _bufferManager.PackCellFromView(blockRow, blockCol);
    }

    private byte[] BuildLineBytesFromView(int line1Based)
    {
        return _bufferManager.BuildLineBytesFromView(line1Based);
    }

    private void SendCellFromView(int y, int x)
    {
        int br = y / RTDConstants.CELL_HEIGHT;
        int bc = x / RTDConstants.CELL_WIDTH;

        byte packed = PackCellFromView(br, bc);
        QueueGraphicLineCommand(br + 1, bc, new byte[] { packed });

        // Update just this 4x2 block in Unity
        int baseY = br * RTDConstants.CELL_HEIGHT;
        int baseX = bc * RTDConstants.CELL_WIDTH;
        for (int r = 0; r < RTDConstants.CELL_HEIGHT; r++)
        {
            for (int c = 0; c < RTDConstants.CELL_WIDTH; c++)
            {
                int py = baseY + r, px = baseX + c;
                _unityVisualizer.RefreshPin(new Vector2Int(px, py));
            }
        }
    }

    /// <summary>
    /// Refreshes the Unity pins for every pixel in each line, then queues a single
    /// full-line graphic command per line.  Replaces up to 30 individual cell commands
    /// with one ACK-gated line command — roughly 10× fewer round-trips for thick bars.
    /// </summary>
    private void SendLinesFromView(HashSet<int> lines)
    {
        foreach (var line1 in lines)
        {
            int baseY = (line1 - 1) * RTDConstants.CELL_HEIGHT;
            for (int c = 0; c < RTDConstants.CELLS_PER_LINE; c++)
            {
                int baseX = c * RTDConstants.CELL_WIDTH;
                for (int r = 0; r < RTDConstants.CELL_HEIGHT; r++)
                    for (int cc = 0; cc < RTDConstants.CELL_WIDTH; cc++)
                        _unityVisualizer.RefreshPin(new Vector2Int(baseX + cc, baseY + r));
            }
            var bytes = BuildLineBytesFromView(line1);
            QueueGraphicLineCommand(line1, 0, bytes);
        }
    }

    public void HighlightMostRecentTouch()
    {
        _highlightManager.HighlightMostRecentTouch();
    }

    public Dictionary<string, List<Vector2Int>> GetActiveGestureHighlights()
    {
        return _highlightManager.GetActiveGestureHighlights();
    }

    public void SetHover(Vector2Int coord, bool isHovered)
    {
        _unityVisualizer.SetHover(coord, isHovered);
        SendCellFromView(coord.y, coord.x);
    }

    private void SetOverlay(int y, int x, sbyte state) // -1,0,+1
    {
       _bufferManager.Overlay[y, x] = state;
        SendCellFromView(y, x);
    }

    public string FormatValuesForTTS(List<NodeComponent> nodes, List<float> probabilities)
    {
        return _dataFormatter.FormatValuesForTTS(nodes, probabilities);
    }

}
