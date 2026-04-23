using System.Collections.Generic;

/// <summary>
/// Represents a node (axis tick, data point, etc.) with its position and associated values.
/// Represents a node (axis tick, data point, etc.) with its position and associated values.
/// </summary>
public class ChartNode
{
    public string Id { get; set; }
    public string Type { get; set; }
    public List<(int x, int y)> Coordinates { get; set; }
    public Dictionary<string, object> Values { get; set; }
    public bool Visibility { get; set; }

    public string Series { get; set; }
    public string Symbol { get; set; }

    public ChartNode()
    {
        Coordinates = new List<(int, int)>();
        Values = new Dictionary<string, object>();
        Visibility = true;
    }

    public ChartNode(string id, string type)
    {
        Id = id;
        Type = type;
        Coordinates = new List<(int, int)>();
        Values = new Dictionary<string, object>();
        Visibility = true;
    }
}
