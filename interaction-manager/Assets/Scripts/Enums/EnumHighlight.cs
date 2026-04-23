using System.Collections;
using System.Collections.Generic;
using UnityEngine;


public enum HighlightMarkShape
{
    Box,           // 8-surrounding pixels (all chart types)
    Mark,          // symbol pins if symbols on, else focus (±1 Y); bar → perimeter
    BarPerimeter,  // bar only: outer edge pins
    BarInterior,   // bar only: inner fill pins
}

public enum HighlightAnim
{
    Static,    // raise once, hold until cleared or duration expires
    Animated,  // alternate raise/lower at TOUCH_PULSE_INTERVAL
}

[System.Serializable]
public struct HighlightConfig
{
    public HighlightMarkShape Shape;
    public HighlightAnim Anim;
    public float Duration;       // -1 = infinite
    public bool UseBatchSend;    // false = per-cell sends (symbols/marks), true = full-line batch (bars)
}
