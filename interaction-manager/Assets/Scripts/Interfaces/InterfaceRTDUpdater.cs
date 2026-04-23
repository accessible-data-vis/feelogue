using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public interface InterfaceRTDUpdater
{
    event Action<byte[]> ButtonPacketReceived;
    void ClearScreen();
    void FillScreen();
    void RefreshScreen();
    void SetFileOption(int file);
    void SetFileOptionWithoutReload(int file);
    void DisplayImage(int[,] image);
    void DisplayImageInUnityFromBase();
    void DisplayBrailleLabel(string text);
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
    void NextBraillePage();
    void PrevBraillePage();
    void NextOverviewLayer();
    void PrevOverviewLayer();
    void EnableDataPointNavigation(bool enable, bool navigateToFirst);
    void ResetNavigationToStart();
    void SetNavigationToTouchedPoint(Vector2Int coord, List<NodeComponent> matchingNodes = null, List<float> probabilities = null);
    void SetNavigationIndexOnly(Vector2Int coord);
    void NavigateNextDataPoint();
    void NavigatePrevDataPoint();
    void NavigateToDataPoint(int centerPoint);
    void NavigateToDataPointByValue(string xField, object xValue, string yField, object yValue);
    Vector2Int? GetHighlightedPoint();
    int? GetHighlightedPointIndex();
    string FormatValuesForTTS(List<NodeComponent> nodes, List<float> probabilities);
    void HighlightMostRecentTouch();

    Dictionary<string, List<Vector2Int>> GetActiveGestureHighlights();
    void ClearHighlights(string hand);
    void SetChartType(string chartType);
    void SetInterleavedNavigation(bool value);
    void SetUseSeriesSymbols(bool value);
    void SetSeriesSymbolOverrides(RTDGridConstants.SymbolType[] overrides);
    void SetHighlightConfigs(HighlightConfig gesture, HighlightConfig agent, HighlightConfig nav);
    void SetChartTitle(string chartTitle);
    void RestoreAllGestureHighlights();
    void StoreAllGestureHighlights(Func<Vector2Int, (object xValue, object yValue)?> getNodeValues);
    void ClearStoredGestureValues();
    bool IsHighlightFromNavigation();
}
