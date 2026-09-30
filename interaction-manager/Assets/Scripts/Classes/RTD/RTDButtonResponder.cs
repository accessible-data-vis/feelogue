using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RTDButtonResponder : MonoBehaviour, InterfaceRTDButtonResponder
{
    [SerializeField] private MonoBehaviour agentWakeWordService;
    private InterfaceAgentWakeWord _agentWakeWord;
    [SerializeField] private MonoBehaviour speechToTextService;
    private InterfaceSpeechToText _speechToText;
    [SerializeField] private MonoBehaviour textToSpeechService;
    private InterfaceTextToSpeech _textToSpeech;
    [SerializeField] private MonoBehaviour audioToneManagerService;
    private InterfaceAudioToneManager _audioToneManager;
    [SerializeField] private MonoBehaviour rtdButtonParserService;
    private InterfaceRTDButtonParser _rtdButtonParser;
    [SerializeField] private MonoBehaviour rtdUpdaterService;
    private InterfaceRTDUpdater _rtdUpdater;
    [SerializeField] private AgentResponseHandler _agentResponseHandler;
    [SerializeField] private MonoBehaviour buttonGUIService;
    private InterfaceButtonGUI _buttonGUI;

    // timing for pan modifier vs agent hold
    private const float PAN_MODIFIER_THRESHOLD = 0.5f;  // 500ms to enter modifier mode
    private const float AGENT_HOLD_TIME = 0.5f;         // 500ms to start recording

    // pan button state (3-state system: tap / hold-release / hold+function)
    private PanModifierState _panNextState = new PanModifierState();
    private PanModifierState _panPrevState = new PanModifierState();

    // agent button state (hold to record)
    private ButtonHoldState _agentHoldState = new ButtonHoldState();

    // Function button configuration
    private struct FunctionButtonConfig
    {
        public System.Action PanLeftAction;
        public string PanLeftLog;
        public System.Action PanRightAction;
        public string PanRightLog;
        public System.Action StandaloneAction;
        public string StandaloneLog;
        public bool HasAgentHold;  // Only F1 has this
    }

    private Dictionary<int, FunctionButtonConfig> _functionConfigs;

    // Pan taps and pan + F1 step through the braille text.
    private BrailleTextNavigator _textNav;

    private class PanModifierState
    {
        public bool IsPressed;
        public float PressTime;
        public bool ModifierModeActive;
        public bool CombinationUsed;
        public Coroutine ModifierCoroutine;
        
        public void Reset()
        {
            IsPressed = false;
            PressTime = 0f;
            ModifierModeActive = false;
            CombinationUsed = false;
            ModifierCoroutine = null;
        }
    }

    private class ButtonHoldState
    {
        public Coroutine Coroutine;
        public bool IsHeld;
        
        public void Reset()
        {
            Coroutine = null;
            IsHeld = false;
        }
    }

    void Awake()
    {
        _agentWakeWord = agentWakeWordService as InterfaceAgentWakeWord ?? throw new InvalidOperationException("agentWakeWordService not assigned!");
        _speechToText = speechToTextService as InterfaceSpeechToText ?? throw new InvalidOperationException("speechToTextService not assigned!");
        _textToSpeech = textToSpeechService as InterfaceTextToSpeech ?? throw new InvalidOperationException("textToSpeechService not assigned!");
        _rtdButtonParser = rtdButtonParserService as InterfaceRTDButtonParser ?? throw new InvalidOperationException("rtdButtonParserService not assigned!");
        _audioToneManager = audioToneManagerService as InterfaceAudioToneManager ?? throw new InvalidOperationException("audioToneManager not assigned!");
        _rtdUpdater = rtdUpdaterService as InterfaceRTDUpdater ?? throw new InvalidOperationException("rtdUpdaterService not set");
        _buttonGUI = buttonGUIService as InterfaceButtonGUI ?? throw new InvalidOperationException("buttonGUIService not assigned!");

        // Initialize function button configurations
        _functionConfigs = new Dictionary<int, FunctionButtonConfig>
        {
            [1] = new FunctionButtonConfig
            {
                HasAgentHold = true,
                // Pan held, then F1: skip a whole chunk or layer.
                PanLeftAction = () => _textNav.Skip(-1),
                PanLeftLog = "Pan Left + F1: previous chunk or layer",
                PanRightAction = () => _textNav.Skip(+1),
                PanRightLog = "Pan Right + F1: next chunk or layer"
            },
            [2] = new FunctionButtonConfig
            {
                StandaloneAction = () =>
                {
                    _speechToText.CancelSpeechRecognition();
                    _agentWakeWord.StopAudio();
                    _rtdUpdater.StopAgentHighlights();   // refresh (F4) clears the user's boxes too
                },
                StandaloneLog = "Stop all audio..."
            },
            [3] = new FunctionButtonConfig
            {
                StandaloneAction = () => _textToSpeech.RepeatLastAudio(),
                StandaloneLog = "Repeat previous output..."
            },
            [4] = new FunctionButtonConfig
            {
                StandaloneAction = () => _rtdUpdater.RefreshScreen(),
                StandaloneLog = "Refresh contents of DotPad..."
            }
        };
    }

    void Start()
    {
        _textNav = new BrailleTextNavigator(_rtdUpdater, _agentResponseHandler);
    }

    void OnEnable()
    {
        _rtdUpdater.ButtonPacketReceived += _rtdButtonParser.ProcessButtonPacket;

        _rtdButtonParser.PanNextPressed += PanNextStart;
        _rtdButtonParser.PanNextReleased += PanNextStop;
        _rtdButtonParser.PanPrevPressed += PanPrevStart;
        _rtdButtonParser.PanPrevReleased += PanPrevStop;


        _rtdButtonParser.Function1Pressed += OnFunction1Press;
        _rtdButtonParser.Function1Released += OnFunction1Release;
        _rtdButtonParser.Function2Pressed += OnFunction2Press;
        _rtdButtonParser.Function3Pressed += OnFunction3Press;
        _rtdButtonParser.Function4Pressed += OnFunction4Press;
    }

    void OnDisable()
    {
        _rtdUpdater.ButtonPacketReceived -= _rtdButtonParser.ProcessButtonPacket;
        _rtdButtonParser.PanNextPressed -= PanNextStart;
        _rtdButtonParser.PanNextReleased -= PanNextStop;
        _rtdButtonParser.PanPrevPressed -= PanPrevStart;
        _rtdButtonParser.PanPrevReleased -= PanPrevStop;

        _rtdButtonParser.Function1Pressed -= OnFunction1Press;
        _rtdButtonParser.Function1Released -= OnFunction1Release;
        _rtdButtonParser.Function2Pressed -= OnFunction2Press;
        _rtdButtonParser.Function3Pressed -= OnFunction3Press;
        _rtdButtonParser.Function4Pressed -= OnFunction4Press;
    }

    private void PanNextStart()
    {
        _rtdUpdater.CancelPendingPresentation();
        if (TrySkipAfterF1(_panNextState, +1)) return;
        if (_panPrevState.IsPressed)
        {
            HandlePanChord(_panPrevState, "Pan Left", forward: true);
            return;
        }

        _panNextState.IsPressed = true;
        _panNextState.PressTime = Time.time;
        _panNextState.ModifierModeActive = false;
        _panNextState.CombinationUsed = false;

        if (_panNextState.ModifierCoroutine != null)
            StopCoroutine(_panNextState.ModifierCoroutine);
        _panNextState.ModifierCoroutine = StartCoroutine(ActivatePanModifier(_panNextState, "Pan Right"));
    }

    private void PanNextStop()
    {
        if (_panNextState.ModifierCoroutine != null)
        {
            StopCoroutine(_panNextState.ModifierCoroutine);
            _panNextState.ModifierCoroutine = null; 
        }


        // Skip processing if state wasn't properly initialized (e.g., after chunk toggle)
        if (_panNextState.PressTime == 0 || _panNextState.CombinationUsed)
        {
            _panNextState.Reset();
            return;
        }
        
        float holdDuration = Time.time - _panNextState.PressTime;
        AppLog.Detail(LogArea.Buttons, $"Pan Right hold duration: {holdDuration:F3}s (threshold: {PAN_MODIFIER_THRESHOLD}s)");
        
        if (holdDuration < PAN_MODIFIER_THRESHOLD)
        {
            // STATE 1: Quick tap → next line, chunk or layer
            AppLog.Info(LogArea.Buttons, "Pan Right tapped - reading on");
            _textNav.Next();
        }
        else if (!_panNextState.CombinationUsed)
        {
            // STATE 2: Hold without function -> navigate to next data point
            AppLog.Info(LogArea.Buttons, "Pan Right held and released - next data point");
            _rtdUpdater.NavigateNextDataPoint();
        }
        
        // STATE 3: Combination was used - action already triggered
        _panNextState.Reset();
    }

    private void PanPrevStart()
    {
        _rtdUpdater.CancelPendingPresentation();
        if (TrySkipAfterF1(_panPrevState, -1)) return;
        if (_panNextState.IsPressed)
        {
            HandlePanChord(_panNextState, "Pan Right", forward: false);
            return;
        }

        _panPrevState.IsPressed = true;
        _panPrevState.PressTime = Time.time;
        _panPrevState.ModifierModeActive = false;
        _panPrevState.CombinationUsed = false;

        if (_panPrevState.ModifierCoroutine != null)
            StopCoroutine(_panPrevState.ModifierCoroutine);
        _panPrevState.ModifierCoroutine = StartCoroutine(ActivatePanModifier(_panPrevState, "Pan Left"));
    }

    private void PanPrevStop()
    {
        if (_panPrevState.ModifierCoroutine != null)
        {
            StopCoroutine(_panPrevState.ModifierCoroutine);
            _panPrevState.ModifierCoroutine = null; 
        }

        if (_panPrevState.PressTime == 0f || _panPrevState.CombinationUsed)
        {
            _panPrevState.Reset();
            return;
        }

        float holdDuration = Time.time - _panPrevState.PressTime;
        
        if (holdDuration < PAN_MODIFIER_THRESHOLD)
        {
            // STATE 1: Quick tap → previous line, chunk or layer
            AppLog.Info(LogArea.Buttons, "Pan Left tapped - reading back");
            _textNav.Prev();
        }
        else if (!_panPrevState.CombinationUsed)
        {
            // STATE 2: Hold without function -> navigate to previous data point
            AppLog.Info(LogArea.Buttons, "Pan Left held and released - prev data point");
            _rtdUpdater.NavigatePrevDataPoint();
        }
        // STATE 3: Combination was used - action already triggered
        
        _panPrevState.Reset();
    }

    private IEnumerator ActivatePanModifier(PanModifierState state, string panName)
    {
        yield return new WaitForSeconds(PAN_MODIFIER_THRESHOLD);
        state.ModifierModeActive = true;
        AppLog.Detail(LogArea.Buttons, $"{panName} modifier mode activated");
    }
    
    /// <summary>
    /// Both pans down: reserved, nothing fires. The held key is marked used so neither
    /// release pages braille or steps a data point.
    /// </summary>
    private void HandlePanChord(PanModifierState held, string heldName, bool forward)
    {
        // PromoteModifier also stops the held key's pending modifier timer, so
        // a stale timer can't flag a later press as a hold.
        PromoteModifier(held, heldName);
        AppLog.Info(LogArea.Buttons, "Both pans pressed: reserved, nothing fires");
    }

    /// <summary>
    /// F1 then a pan within the push-to-talk delay skips like pan + F1. Cancels the
    /// recording and uses up the pan press.
    /// </summary>
    private bool TrySkipAfterF1(PanModifierState pan, int dir)
    {
        if (_agentHoldState.Coroutine == null || _agentHoldState.IsHeld) return false;
        StopCoroutine(_agentHoldState.Coroutine);
        _agentHoldState.Reset();
        pan.IsPressed = true;
        pan.PressTime = Time.time;
        pan.CombinationUsed = true;
        AppLog.Info(LogArea.Buttons, dir > 0 ? "F1 + Pan Right: next chunk or layer" : "F1 + Pan Left: previous chunk or layer");
        _textNav.Skip(dir);
        return true;
    }

    private void HandleFunctionPress(int functionNumber)
    {
        _rtdUpdater.CancelPendingPresentation();
        var config = _functionConfigs[functionNumber];

        // A pan is down and this key has a pan action (F1: skip): run that instead.
        // Keys without one (F2 stop, F3 repeat, F4 refresh) always do their own job.
        if (_panPrevState.IsPressed && !_panPrevState.CombinationUsed && config.PanLeftAction != null)
        {
            PromoteModifier(_panPrevState, $"Pan Left (F{functionNumber})");
            if (!string.IsNullOrEmpty(config.PanLeftLog))
                AppLog.Info(LogArea.Buttons, config.PanLeftLog);
            config.PanLeftAction?.Invoke();
            _panPrevState.Reset();
            return;
        }

        if (_panNextState.IsPressed && !_panNextState.CombinationUsed && config.PanRightAction != null)
        {
            PromoteModifier(_panNextState, $"Pan Right (F{functionNumber})");
            if (!string.IsNullOrEmpty(config.PanRightLog))
                AppLog.Info(LogArea.Buttons, config.PanRightLog);
            config.PanRightAction?.Invoke();
            _panNextState.Reset();
            return;
        }

        // Standalone action
        if (config.HasAgentHold)
        {
            // F1 special case: agent hold
            _agentHoldState.IsHeld = false;
            if (_agentHoldState.Coroutine != null)
            {
                StopCoroutine(_agentHoldState.Coroutine);
                _agentHoldState.Coroutine = null;
            }
            _agentHoldState.Coroutine = StartCoroutine(AgentHoldCheck());
        }
        else
        {
            if (!string.IsNullOrEmpty(config.StandaloneLog))
                AppLog.Info(LogArea.Buttons, config.StandaloneLog);
            config.StandaloneAction?.Invoke();
        }
    }

    private IEnumerator AgentHoldCheck()
    {
        yield return new WaitForSeconds(AGENT_HOLD_TIME);
        _agentWakeWord.StopAudio();
        _agentHoldState.IsHeld = true;
        AppLog.Info(LogArea.Buttons, "Starting speech recognition...");
        
        _audioToneManager.PlayStartTone();
        _speechToText.StartSpeechRecognition(
            transcript => _agentResponseHandler.HandleButtonSpeech(transcript, false), true, "none", () => AppLog.Detail(LogArea.Speech, "STT complete")
        );
    }

    private void PromoteModifier(PanModifierState s, string who)
    {
        if (!s.ModifierModeActive)
        {
            s.ModifierModeActive = true;
            if (s.ModifierCoroutine != null)
            {
                StopCoroutine(s.ModifierCoroutine);
                s.ModifierCoroutine = null;
            }
            AppLog.Detail(LogArea.Buttons, $"{who}: modifier promoted");
        }
        s.CombinationUsed = true;
    }
    private void OnFunction1Release()
    {
        if (_agentHoldState.Coroutine != null)
            StopCoroutine(_agentHoldState.Coroutine);

        if (_agentHoldState.IsHeld)
        {
            AppLog.Detail(LogArea.Speech, "Transcribing STT...");
            _audioToneManager.PlayEndTone();
            _agentWakeWord.ResumeWakeWord();
            _agentResponseHandler.BlinkCursor();
        }

        _agentHoldState.Reset();
    }

    private void OnFunction1Press() => HandleFunctionPress(1);

    private void OnFunction2Press() => HandleFunctionPress(2);

    private void OnFunction3Press() => HandleFunctionPress(3);

    private void OnFunction4Press() => HandleFunctionPress(4);
};
