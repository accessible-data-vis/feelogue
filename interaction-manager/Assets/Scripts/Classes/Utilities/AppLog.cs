using System;
using UnityEngine;

public enum LogArea { Agent, Presentation, Speech, Buttons, Touch, Chart, Render, Braille, Device, Setup }

/// <summary>
/// Console logging by area. Info is one line per meaningful event and always shows;
/// Detail (per pin, per node, per packet) shows only for areas switched on in LogSettings.
/// </summary>
public static class AppLog
{
    private static readonly bool[] _detail = new bool[Enum.GetValues(typeof(LogArea)).Length];

    public static void SetDetail(LogArea area, bool on) => _detail[(int)area] = on;

    public static bool DetailOn(LogArea area) => _detail[(int)area];

    public static void Info(LogArea area, string message) => Debug.Log($"[{area}] {message}");

    public static void Detail(LogArea area, string message)
    {
        if (_detail[(int)area]) Debug.Log($"[{area}] {message}");
    }
}
