using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

public interface InterfaceGraphVisualizer
{
    public void GenerateGraph(string json, string chartType, string dataType);
    public void GenerateGraph(List<ChartNode> chartNodes, string chartType, string dataType);
    public List<NodeComponent> GetMatchingNodes(HashSet<Vector2Int> coords);
    public List<NodeComponent> GetMatchingNodesBasedOnValue(List<float> searchValues, bool requireAll = true);
    Dictionary<string, GameObject> GetNodes();
    HashSet<Vector2Int> GetAxisCoordinates();
    void UpdateVisibleNodes(List<Dictionary<string, object>> visibleData, string xField, string yField, float? axisDomainYMin = null, float? axisDomainYMax = null, List<object> xViewportValues = null);
    void ShowAllNodes();
    void UpdateViewportOverlay(int windowStart, int windowSize, int totalDataPoints, float windowYMin, float windowYMax, float dataYMin, float dataYMax);
    NodeComponent GetMatchingNodeByXY(string xValue, float yValue);
}
