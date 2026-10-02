using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public interface InterfaceRTDUpdater
{
    event Action<byte[]> ButtonPacketReceived;
    void ClearScreen();
    void FillScreen();
    void RefreshScreen(bool brailleTitle = true);
    void SetFileOption(int file);
    /// <summary>Does nothing; the chart is already loaded.</summary>
    void SetFileOptionWithoutReload(int file);
    void DisplayImage(int[,] image);
    void DisplayImageInUnityFromBase();
    void DisplayBrailleLabel(string text);
    void DisplayBrailleLabel(string text, BrailleLineOwner owner);
    BrailleLineOwner LineOwner { get; }
    BrailleLineOwner LastTextSequence { get; }
    /// <summary>Next braille line; false when the text has no more.</summary>
    bool TryNextBraillePage();
    bool TryPrevBraillePage();
    /// <summary>Re-send the current braille text (e.g. after the braille mode changes).</summary>
    void RefreshBrailleLabel();
    bool SendTextLineToDot(string hexBraille);
    void SetPixel(int y, int x, bool raised);
    void RefreshPin(Vector2Int coord);
    void SetHover(Vector2Int coord, bool isHovered);
    void PulsePins(IEnumerable<Vector2Int> coords, float interval, float duration = -1f, string hand = "agent");
    void PulseShape(int x, int y, HighlightShape shape, float interval = 1f, float duration = -1f, string hand = "agent", bool clearPrevious = false);
    void PulseLoadingBar(float duration = -1f, float displayDuration = 2f, float wipeDuration = 1f);
    void ShowShape(int x, int y, HighlightShape shape, float duration = -1f, string hand = "agent");
    void ShowTouchHighlights(List<Vector2Int> coords, HighlightShape shape, float duration, string hand);
    void StopPulsePins();
    void StopAgentHighlights();
    void NextBraillePage();
    void PrevBraillePage();
    /// <summary>Next presentation layer; false at the last one.</summary>
    bool NextOverviewLayer();
    void ResetOverviewLayer();
    /// <summary>Start the presentation once current speech has finished.</summary>
    void StartOverviewPresentation();
    /// <summary>Start the presentation straight away.</summary>
    void StartOverviewPresentationNow();
    void CancelPendingPresentation();
    void ClearOverviewModeForLoad();
    void EndPresentation(bool announce);
    bool IsPresentationActive { get; }
    bool PrevOverviewLayer();
    void SpeakNotice(string text);
    void EnableDataPointNavigation(bool enable, bool navigateToFirst);
    void ResetNavigationToStart();
    /// <summary>Navigate to the touched pin: highlight it and speak its values.</summary>
    void SetNavigationToTouchedPoint(Vector2Int coord, List<NodeComponent> matchingNodes = null, List<float> probabilities = null);
    /// <summary>Move the navigation cursor to this pin without highlighting or speaking.</summary>
    void SetNavigationIndexOnly(Vector2Int coord);
    void NavigateNextDataPoint();
    void NavigatePrevDataPoint();
    void NavigateToDataPoint(int centerPoint);
    void NavigateToDataPointByValue(string xField, object xValue, string yField, object yValue);
    Vector2Int? GetHighlightedPoint();
    int? GetHighlightedPointIndex();
    string FormatValuesForTTS(List<NodeComponent> nodes, List<float> probabilities);
    /// <summary>Highlight the latest touched pins again.</summary>
    void HighlightMostRecentTouch();

    /// <summary>Gesture highlights currently shown, by hand.</summary>
    Dictionary<string, List<Vector2Int>> GetActiveGestureHighlights();
    void ClearHighlights(string hand);
    void SetChartType(string chartType);
    // Units for spoken and brailled values, from the chart's axis titles
    void SetAxisUnits(string xField, string xTitle, string yField, string yTitle);
    void SetInterleavedNavigation(bool value);
    void SetUseSeriesSymbols(bool value);
    /// <summary>Symbol per series index; Default falls back to the renderer's rotation.</summary>
    void SetSeriesSymbolOverrides(RTDGridConstants.SymbolType[] overrides);
    void SetHighlightConfigs(HighlightConfig gesture, HighlightConfig agent, HighlightConfig nav);
    void SetChartTitle(string chartTitle);
    /// <summary>Whether the current highlight came from navigation rather than a gesture.</summary>
    bool IsHighlightFromNavigation();
    long GetHighlightAnchoredAtMs();
    NodeComponent GetHighlightedNode();
}
