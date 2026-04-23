using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using TMPro;

public class NodeComponent : MonoBehaviour
{
    [SerializeField] public string id;
    [SerializeField] public string type;
    [SerializeField] public bool visibility;
    [SerializeField] public string series;
    [SerializeField] public string symbol;
    [SerializeField] public int[] xy;
    [System.Serializable] public class KeyValuePairStringFloat
    {
        public string key;
        public float value;
    }

    [System.Serializable] public class KeyValuePairStringObject
    {
        public string key;
        public string value;  // Store as string for serialization
    }

    [System.Serializable] public struct Coordinate
    {
        public int x;
        public int y;

        public Coordinate(int x, int y)
        {
            this.x = x;
            this.y = y;
        }
    }
    
    [SerializeField] public List<Coordinate> barCoordinates = new List<Coordinate>();
    public List<KeyValuePairStringObject> serializedValues = new List<KeyValuePairStringObject>();
    public Dictionary<string, object> values = new Dictionary<string, object>();
    [HideInInspector] public GameObject labelPrefab;
    [HideInInspector] public float labelOffsetY;
    [HideInInspector] public string nodeValuesText;

    public void SetValues(Dictionary<string, object> nodeValues)
    {
        values = nodeValues ?? new Dictionary<string, object>();
        serializedValues.Clear();
        foreach (var pair in values)
        {
            serializedValues.Add(new KeyValuePairStringObject { key = pair.Key, value = pair.Value?.ToString() ?? "" });
        }
    }

    public void PopulateBarCoordinates(int[] nodeCoordinates, string chart_type, string data_type)
    {
        if (xy == null || xy.Length < 2)
        {
            Debug.LogWarning($"PopulateBarCoordinates() skipped for {id}: xy is null or invalid.");
            return;
        }

        int x = xy[0];
        int y = xy[1];
        int zeroRow = RTDGridConstants.X_AXIS_ROW / 2; // Approximate zero-line row
        int axisRow = RTDGridConstants.X_AXIS_ROW;

        barCoordinates.Clear();

        if (type.Contains("data-mark"))
        {
            if (chart_type == "bar" && data_type == "value")
            {
                if (y >= zeroRow)
                {
                    for (int yPos = y; yPos >= zeroRow; yPos--)
                        barCoordinates.Add(new Coordinate(x, yPos));
                }
                else
                {
                    for (int yPos = y; yPos <= zeroRow; yPos++)
                        barCoordinates.Add(new Coordinate(x, yPos));
                }
            }
            else if (chart_type == "bar" && data_type != "value")
            {
                for (int yPos = y; yPos <= axisRow; yPos++)
                    barCoordinates.Add(new Coordinate(x, yPos));
            }
            else
            {
                barCoordinates.Add(new Coordinate(x, y));
            }
        }
        else if (type.Contains("x-axis-tick"))  
        {
            //  X-Axis Tick (From x, y down to x, 36)
            for (int yPos = y; yPos >= 36; yPos--)  
            {
                barCoordinates.Add(new Coordinate(x, yPos));
            }
        }
        else if (type.Contains("y-axis-tick"))  
        {
            //  Y-Axis Tick (From x, y extending to x+2, y)
            for (int xPos = x; xPos <= x + 2; xPos++)  
            {
                barCoordinates.Add(new Coordinate(xPos, y));
            }
        }

    }

    public void CreateLabel(GameObject labelPrefab, float labelHeight, string valuesText)
    {
        // 1) Guard against bad input
        if (labelPrefab == null)
        {
            Debug.LogWarning($"[{id}] CreateLabel: no prefab assigned.");
            return;
        }
        if (string.IsNullOrEmpty(valuesText))
        {
            Debug.LogWarning($"[{id}] CreateLabel: no valuesText provided.");
            return;
        }

        Debug.Log($"Spawning label for node {id}");

        // 2) Parent it to this node
        var lbl = Instantiate(labelPrefab, transform);
        lbl.name = $"Label {id}";

        // 3) Float it up by the passed-in height
        lbl.transform.localPosition = new Vector3(0f, labelHeight, 0f);

        // 4) Write the text onto whichever text component you have
        if (lbl.TryGetComponent<TMP_Text>(out var tmp))
        {
            tmp.text = valuesText;
            tmp.alignment = TextAlignmentOptions.Center;
        }
        else if (lbl.TryGetComponent<TextMesh>(out var tm))
        {
            tm.text = valuesText;
            tm.anchor = TextAnchor.LowerCenter;
        }

        // 5) (Optional) always face the camera
        lbl.AddComponent<Billboard>();
    }

    public void Initialize(string nodeId, string nodeType, bool nodeVisibility, int[] nodeXy, int[] nodeCoordinates, Dictionary<string, object> nodeValues)
    {
        id = nodeId;
        type = nodeType;
        visibility = nodeVisibility;
        xy = nodeXy;
        SetValues(nodeValues);
    }
}
