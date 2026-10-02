using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public interface InterfaceButtonGUI
{
    public void SelectGraphOption(int option);
    /// <summary>Load the chart named in the agent's "dataName-chartType" command.</summary>
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
    /// <summary>Whether highlights lower the pins around them.</summary>
    public bool GetLocalIsolationMode();
    public bool GetFollowUpMode();
    public bool GetOverviewMode();
    public void SetOverviewMode(bool on);
    public void ClearOverviewMode();
    public bool GetToggleMode();
}
