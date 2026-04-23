using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public interface InterfaceButtonGUI
{
    public void SelectGraphOption(int option);
    public void HandleChartCommand(string rtdCommand);
    public bool GetDoubleTapState();
    public bool GetTouchSenseState();
    public bool GetValueAudioState();
    public bool GetValueBrailleState();
    public bool GetBlinkTapState();
    public float GetTapCoolDown();
    public float GetTapMinDuration();
    public float GetTapMaxDuration();
    public float GetDoubleTapTimeWindow();
    public string GetNameLog();
    public float GetBlinkDuration();
    public ProcessingMode GetProcessingMode();
    public bool GetWaitToneMode();
    public bool GetLocalIsolationMode();
    public bool GetFollowUpMode();
    public bool GetOverviewMode();
    public bool GetToggleMode();
    public void UpdateHighlightModes(string chartType);
}
