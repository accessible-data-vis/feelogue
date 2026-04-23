using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;


public class RTDButtonParser : MonoBehaviour, InterfaceRTDButtonParser
{
    // single button press events
    public event Action PanNextPressed = delegate { };
    public event Action PanNextReleased = delegate { };
    public event Action PanPrevPressed = delegate { };
    public event Action PanPrevReleased = delegate { };
    public event Action Function1Pressed = delegate { };
    public event Action Function1Released = delegate { };
    public event Action Function2Pressed = delegate { };
    public event Action Function3Pressed = delegate { };
    public event Action Function4Pressed = delegate { };

    // combination events
    public event Action<PanningAction, FunctionAction> CombinationPressed = delegate { };
    public event Action<PanningAction, FunctionAction> CombinationReleased = delegate { };
    public event Action BothPanButtonsPressed = delegate { };

    // immediate press/release events
    public event Action PanNextPressedImmediate = delegate { };
    public event Action PanPrevPressedImmediate = delegate { };

    // state tracking
    private HashSet<PanningAction> _pressedPanButtons = new HashSet<PanningAction>();
    private HashSet<FunctionAction> _pressedFunctionButtons = new HashSet<FunctionAction>();
    private HashSet<PanningAction> _panButtonsInCombination = new HashSet<PanningAction>();
    private HashSet<FunctionAction> _functionButtonsInCombination = new HashSet<FunctionAction>();

    // per-button coroutine tracking
    private Dictionary<PanningAction, Coroutine> _delayedPanActions = new Dictionary<PanningAction, Coroutine>();
    private Dictionary<FunctionAction, Coroutine> _delayedFunctionActions = new Dictionary<FunctionAction, Coroutine>();
    private bool _bothPanButtonsTriggered = false;

    private const float COMBINATION_DELAY = 0.1f;

    // Action lookup maps for press/release events
    private Dictionary<PanningAction, (string log, System.Action pressAction, System.Action releaseAction)> _panActionMap;
    private Dictionary<FunctionAction, (string log, System.Action pressAction, System.Action releaseAction)> _functionActionMap;
    
    // Raw key constants
    private const byte PAN_CODE = 0x12;
    private const byte FUNC_CODE = 0x32;

    void Awake()
    {
        // Initialize action lookup maps
        _panActionMap = new Dictionary<PanningAction, (string, System.Action, System.Action)>
        {
            [PanningAction.Next] = ("NEXT", () => PanNextPressed(), () => PanNextReleased()),
            [PanningAction.Prev] = ("PREV", () => PanPrevPressed(), () => PanPrevReleased())
        };

        _functionActionMap = new Dictionary<FunctionAction, (string, System.Action, System.Action)>
        {
            [FunctionAction.F1] = ("FUNCTION #1", () => Function1Pressed(), () => Function1Released()),
            [FunctionAction.F2] = ("FUNCTION #2", () => Function2Pressed(), () => {}),
            [FunctionAction.F3] = ("FUNCTION #3", () => Function3Pressed(), () => {}),
            [FunctionAction.F4] = ("FUNCTION #4", () => Function4Pressed(), () => {})
        };
    }

    public void ProcessButtonPacket(byte[] packet)
    {
        if (packet == null || packet.Length < 10)
        {
            Debug.LogWarning($"[ButtonParser] Packet too short ({packet?.Length ?? 0} bytes); ignoring.");
            return;
        }

        byte keyCode = packet[6];
        byte action = (keyCode == FUNC_CODE) ? packet[8] : packet[9];

        if (keyCode == PAN_CODE)
        {
            ProcessPanningAction((PanningAction)action);
        }
        else if (keyCode == FUNC_CODE)
        {
            ProcessFunctionAction((FunctionAction)action);
        }
        else
        {
            Debug.LogWarning($"Unknown keyCode 0x{keyCode:X2}");
        }
    }

    private void ProcessPanningAction(PanningAction action)
    {
        if (action == PanningAction.Release)
        {
            // Handle releases for all currently pressed pan buttons
            var toRelease = new List<PanningAction>(_pressedPanButtons);
            foreach (var pressed in toRelease)
            {
                HandlePanRelease(pressed);
            }
            _pressedPanButtons.Clear();
            _bothPanButtonsTriggered = false;
        }
        else if (!_pressedPanButtons.Contains(action))
        {
            // New button press
            _pressedPanButtons.Add(action);

            // Check if both pan buttons are now held simultaneously
            if (_pressedPanButtons.Count == 2)
            {
                Debug.Log("BOTH PAN BUTTONS HELD - Toggle chunk mode");
                _bothPanButtonsTriggered = true;
                BothPanButtonsPressed();

                // Cancel any delayed actions to prevent them from firing
                CancelDelayedPanAction(PanningAction.Next);
                CancelDelayedPanAction(PanningAction.Prev);

                // Don't call HandlePanPress to avoid triggering individual actions
                return;
            }

            HandlePanPress(action);
        }
    }

    private void ProcessFunctionAction(FunctionAction action)
    {
        if (action == FunctionAction.Release)
        {
            // Handle releases for all currently pressed function buttons
            var toRelease = new List<FunctionAction>(_pressedFunctionButtons);
            foreach (var pressed in toRelease)
            {
                HandleFunctionRelease(pressed);
            }
            _pressedFunctionButtons.Clear();
        }
        else if (!_pressedFunctionButtons.Contains(action))
        {
            // New button press
            _pressedFunctionButtons.Add(action);
            HandleFunctionPress(action);
        }
    }

    private void HandlePanPress(PanningAction panAction)
    {

        // Fire immediate event for timing purposes
        if (panAction == PanningAction.Next)
            PanNextPressedImmediate();
        else if (panAction == PanningAction.Prev)
            PanPrevPressedImmediate();

        // Check for combinations first
        if (_pressedFunctionButtons.Count > 0)
        {
            // Combination detected - cancel any delayed action
            CancelDelayedPanAction(panAction);
            _panButtonsInCombination.Add(panAction);

            foreach (var funcAction in _pressedFunctionButtons)
            {
                _functionButtonsInCombination.Add(funcAction);
                Debug.Log($"COMBINATION PRESS: {panAction} + {funcAction}");
                CombinationPressed(panAction, funcAction);
            }
        }
        else
        {
            // Cancel specific button's delayed action
            CancelDelayedPanAction(panAction);
            _delayedPanActions[panAction] = StartCoroutine(DelayedPanAction(panAction));
        }
    }

    private void HandleFunctionPress(FunctionAction funcAction)
    {
        // Check for combinations first
        if (_pressedPanButtons.Count > 0)
        {
            // Combination detected - cancel any delayed action
            CancelDelayedFunctionAction(funcAction);
            _functionButtonsInCombination.Add(funcAction);

            foreach (var panAction in _pressedPanButtons)
            {
                // Also mark the pan button as part of a combination
                _panButtonsInCombination.Add(panAction);
                Debug.Log($"COMBINATION PRESS: {panAction} + {funcAction}");
                CombinationPressed(panAction, funcAction);

                if (funcAction == FunctionAction.F1)
                    Function1Pressed();
                if (funcAction == FunctionAction.F2)
                    Function2Pressed();
                if (funcAction == FunctionAction.F3)
                    Function3Pressed();
                if (funcAction == FunctionAction.F4)
                    Function4Pressed();
            }
        }
        else
        {
            // Cancel specific button's delayed action
            CancelDelayedFunctionAction(funcAction);
            _delayedFunctionActions[funcAction] = StartCoroutine(DelayedFunctionAction(funcAction));
        }
    }

    private void HandlePanRelease(PanningAction panAction)
    {
        //Cancel delayed action on release
        CancelDelayedPanAction(panAction);

        // Skip individual release events if both pan buttons were triggered
        if (_bothPanButtonsTriggered)
            return;

        // Check if this was part of a combination
        if (_panButtonsInCombination.Contains(panAction))
        {
            // Release all combinations involving this button
            foreach (var funcAction in _pressedFunctionButtons)
            {
                Debug.Log($"COMBINATION RELEASE: {panAction} + {funcAction}");
                CombinationReleased(panAction, funcAction);
            }

            _panButtonsInCombination.Remove(panAction);
            // Don't trigger individual release event
        }
        else
        {
            // Regular single button release
            if (_panActionMap.TryGetValue(panAction, out var actionInfo))
            {
                Debug.Log($"RELEASED: {actionInfo.log}");
                actionInfo.releaseAction();
            }
        }
    }

    private void HandleFunctionRelease(FunctionAction funcAction)
    {
        // Cancel delayed action on release
        CancelDelayedFunctionAction(funcAction);

        // Check if this was part of a combination
        if (_functionButtonsInCombination.Contains(funcAction))
        {
            // Release all combinations involving this button
            foreach (var panAction in _pressedPanButtons)
            {
                Debug.Log($"COMBINATION RELEASE: {panAction} + {funcAction}");
                CombinationReleased(panAction, funcAction);
            }

            _functionButtonsInCombination.Remove(funcAction);
            // Don't trigger individual release event
        }
        else
        {
            // Regular single button release
            if (_functionActionMap.TryGetValue(funcAction, out var actionInfo))
            {
                Debug.Log($"RELEASED: {actionInfo.log}");
                actionInfo.releaseAction();
            }
        }
    }

    private IEnumerator DelayedPanAction(PanningAction panAction)
    {
        yield return new WaitForSeconds(COMBINATION_DELAY);

        // If no function buttons were pressed during the delay, it's a single action
        if (_pressedFunctionButtons.Count == 0 && !_panButtonsInCombination.Contains(panAction))
        {
            if (_panActionMap.TryGetValue(panAction, out var actionInfo))
            {
                Debug.Log($"PRESSED: {actionInfo.log}");
                actionInfo.pressAction();
            }
        }

        // Clean up
        if (_delayedPanActions.ContainsKey(panAction))
            _delayedPanActions.Remove(panAction);
    }

    private IEnumerator DelayedFunctionAction(FunctionAction funcAction)
    {
        yield return new WaitForSeconds(COMBINATION_DELAY);

        // If no pan buttons were pressed during the delay, it's a single action
        if (_pressedPanButtons.Count == 0 && !_functionButtonsInCombination.Contains(funcAction))
        {
            if (_functionActionMap.TryGetValue(funcAction, out var actionInfo))
            {
                Debug.Log($"PRESSED: {actionInfo.log}");
                actionInfo.pressAction();
            }
        }

        // Clean up
        if (_delayedFunctionActions.ContainsKey(funcAction))
            _delayedFunctionActions.Remove(funcAction);
    }

    private void CancelDelayedPanAction(PanningAction action)
    {
        if (_delayedPanActions.TryGetValue(action, out Coroutine coroutine))
        {
            if (coroutine != null)
                StopCoroutine(coroutine);
            _delayedPanActions.Remove(action);
        }
    }

    private void CancelDelayedFunctionAction(FunctionAction action)
    {
        if (_delayedFunctionActions.TryGetValue(action, out Coroutine coroutine))
        {
            if (coroutine != null)
                StopCoroutine(coroutine);
            _delayedFunctionActions.Remove(action);
        }
    }

}
