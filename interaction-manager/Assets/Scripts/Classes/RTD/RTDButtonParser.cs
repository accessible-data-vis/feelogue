using System;
using System.Collections.Generic;
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
    public event Action Function2Released = delegate { };
    public event Action Function3Pressed = delegate { };
    public event Action Function3Released = delegate { };
    public event Action Function4Pressed = delegate { };
    public event Action Function4Released = delegate { };

    // The device sends each button group's current state as a bitmask on every
    // change: pan packet[9] holds PanningAction bits (0x04 Prev, 0x02 Next), function
    // packet[8] holds FunctionAction bits (0x80 F1 .. 0x10 F4), 0x00 means nothing is
    // down. Diffing against the last state gives releases (falling) and presses (rising).
    private byte _panState;
    private byte _funcState;

    // Deterministic per-bit event order
    private static readonly PanningAction[] PAN_KEYS = { PanningAction.Prev, PanningAction.Next };
    private static readonly FunctionAction[] FUNC_KEYS = { FunctionAction.F1, FunctionAction.F2, FunctionAction.F3, FunctionAction.F4 };

    // Action lookup maps for press/release events
    private Dictionary<PanningAction, (string log, System.Action pressAction, System.Action releaseAction)> _panActionMap;
    private Dictionary<FunctionAction, (string log, System.Action pressAction, System.Action releaseAction)> _functionActionMap;

    // Raw key constants
    private const byte PAN_CODE = 0x12;
    private const byte FUNC_CODE = 0x32;
    private const byte PAN_BITS = 0x06;   // Prev | Next
    private const byte FUNC_BITS = 0xF0;  // F1 | F2 | F3 | F4

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
            [FunctionAction.F2] = ("FUNCTION #2", () => Function2Pressed(), () => Function2Released()),
            [FunctionAction.F3] = ("FUNCTION #3", () => Function3Pressed(), () => Function3Released()),
            [FunctionAction.F4] = ("FUNCTION #4", () => Function4Pressed(), () => Function4Released())
        };
    }

    public void ProcessButtonPacket(byte[] packet)
    {
        if (packet == null || packet.Length < 10)
        {
            Debug.LogWarning($"[ButtonParser] Packet too short ({packet?.Length ?? 0} bytes); ignoring.");
            return;
        }

        byte group = packet[6];
        if (group == PAN_CODE)
        {
            ProcessPanState(packet[9]);
        }
        else if (group == FUNC_CODE)
        {
            ProcessFunctionState(packet[8]);
        }
        else
        {
            Debug.LogWarning($"Unknown keyCode 0x{group:X2}");
        }
    }

    private void ProcessPanState(byte newState)
    {
        if ((newState & ~PAN_BITS) != 0)
            Debug.LogWarning($"[ButtonParser] Unknown pan state bits in 0x{newState:X2}; masking to known keys.");
        newState &= PAN_BITS;

        byte rising = (byte)(newState & ~_panState);
        byte falling = (byte)(_panState & ~newState);
        _panState = newState;

        // Releases before presses, so sliding from one key to the other (0x04 -> 0x02)
        // isn't a chord. Both keys down arrives as 0x06.
        foreach (var key in PAN_KEYS)
        {
            if ((falling & (byte)key) != 0 && _panActionMap.TryGetValue(key, out var actionInfo))
            {
                AppLog.Detail(LogArea.Buttons, $"RELEASED: {actionInfo.log}");
                actionInfo.releaseAction();
            }
        }
        foreach (var key in PAN_KEYS)
        {
            if ((rising & (byte)key) != 0 && _panActionMap.TryGetValue(key, out var actionInfo))
            {
                AppLog.Detail(LogArea.Buttons, $"PRESSED: {actionInfo.log}");
                actionInfo.pressAction();
            }
        }
    }

    private void ProcessFunctionState(byte newState)
    {
        if ((newState & ~FUNC_BITS) != 0)
            Debug.LogWarning($"[ButtonParser] Unknown function state bits in 0x{newState:X2}; masking to known keys.");
        newState &= FUNC_BITS;

        byte rising = (byte)(newState & ~_funcState);
        byte falling = (byte)(_funcState & ~newState);
        _funcState = newState;

        foreach (var key in FUNC_KEYS)
        {
            if ((falling & (byte)key) != 0 && _functionActionMap.TryGetValue(key, out var actionInfo))
            {
                AppLog.Detail(LogArea.Buttons, $"RELEASED: {actionInfo.log}");
                actionInfo.releaseAction();
            }
        }
        foreach (var key in FUNC_KEYS)
        {
            if ((rising & (byte)key) != 0 && _functionActionMap.TryGetValue(key, out var actionInfo))
            {
                AppLog.Detail(LogArea.Buttons, $"PRESSED: {actionInfo.log}");
                actionInfo.pressAction();
            }
        }
    }
}
