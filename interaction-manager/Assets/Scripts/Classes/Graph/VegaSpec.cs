using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// Data classes for deserializing Vega-Lite JSON specifications.
/// Data classes for deserializing Vega-Lite JSON specifications.
/// </summary>

[Serializable]
public class VegaSpec
{
    [JsonProperty("data")]
    public VegaData Data { get; set; }

    [JsonProperty("encoding")]
    public VegaEncoding Encoding { get; set; }

    [JsonProperty("mark")]
    public JToken Mark { get; set; }  // Can be string or object

    [JsonProperty("layer")]
    public List<VegaLayer> Layer { get; set; }

    [JsonProperty("transform")]
    public List<JToken> Transform { get; set; }


    [JsonProperty("overview")]
    public Dictionary<string, string> Overview { get; set; }

    public string GetMarkType()
    {
        if (Mark == null) return "point";

        if (Mark.Type == JTokenType.String)
            return Mark.ToString();

        if (Mark.Type == JTokenType.Object)
            return Mark["type"]?.ToString() ?? "point";

        return "point";
    }
}

[Serializable]
public class VegaData
{
    [JsonProperty("url")]
    public string Url { get; set; }

    [JsonProperty("format")]
    public VegaDataFormat Format { get; set; }

    [JsonProperty("values")]
    public List<Dictionary<string, object>> Values { get; set; }
}

[Serializable]
public class VegaEncoding
{
    [JsonProperty("x")]
    public VegaChannel X { get; set; }

    [JsonProperty("y")]
    public VegaChannel Y { get; set; }

    [JsonProperty("color")]
    public JToken Color { get; set; }

    [JsonProperty("opacity")]
    public JToken Opacity { get; set; }  // Can be {"value": 1} or complex conditional

    public string GetColorField()
    {
        if (Color == null) return null;
        if (Color.Type == JTokenType.Object && Color["field"] != null)
            return Color["field"].ToString();
        return null;
    }

    /// <summary>Returns the explicit string domain from color.scale.domain, or null if not specified.</summary>
    public List<string> GetColorStringDomain()
    {
        if (Color == null || Color.Type != JTokenType.Object) return null;
        var scale = Color["scale"];
        if (scale == null || scale.Type != JTokenType.Object) return null;
        var domain = scale["domain"];
        if (domain == null || domain.Type != JTokenType.Array) return null;
        try { return domain.ToObject<List<string>>(); } catch { return null; }
    }
}

[Serializable]
public class VegaChannel
{
    [JsonProperty("field")]
    public string Field { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }  // "quantitative", "nominal", "ordinal", "temporal"

    [JsonProperty("scale")]
    public VegaScale Scale { get; set; }

    [JsonProperty("axis")]
    public VegaAxis Axis { get; set; }

    public bool IsCategorical()
    {
        return Type == "nominal" || Type == "ordinal";
    }
}

[Serializable]
public class VegaScale
{
    [JsonProperty("domain")]
    public JToken Domain { get; set; }  // Can be array of numbers or strings

    public (float min, float max) GetNumericDomain()
    {
        if (Domain == null || Domain.Type != JTokenType.Array)
            return (0f, 0f);

        var arr = Domain.ToObject<float[]>();
        if (arr.Length >= 2)
            return (arr[0], arr[1]);

        return (0f, 0f);
    }

    public List<string> GetStringDomain()
    {
        if (Domain == null || Domain.Type != JTokenType.Array) return null;
        try { return Domain.ToObject<List<string>>(); } catch { return null; }
    }
}

[Serializable]
public class VegaAxis
{
    [JsonProperty("tickCount")]
    public int TickCount { get; set; } = 5;

    [JsonProperty("values")]
    public JToken Values { get; set; }  // Array of tick values

    public float[] GetNumericValues()
    {
        if (Values == null || Values.Type != JTokenType.Array)
            return null;

        return Values.ToObject<float[]>();
    }
}

[Serializable]
public class VegaLayer
{
    [JsonProperty("name")]
    public string Name { get; set; }  // Layer identifier (e.g., "yearly", "quarterly")

    [JsonProperty("data")]
    public VegaLayerData Data { get; set; }

    [JsonProperty("mark")]
    public JToken Mark { get; set; }

    [JsonProperty("encoding")]
    public VegaEncoding Encoding { get; set; }

    [JsonProperty("transform")]
    public List<JToken> Transform { get; set; }
}

[Serializable]
public class VegaLayerData
{
    [JsonProperty("url")]
    public string Url { get; set; }

    [JsonProperty("format")]
    public VegaDataFormat Format { get; set; }

    [JsonProperty("values")]
    public List<Dictionary<string, object>> Values { get; set; }
}

[Serializable]
public class VegaDataFormat
{
    [JsonProperty("type")]
    public string Type { get; set; }  // "json", "topojson", "csv"

    [JsonProperty("property")]
    public string Property { get; set; }  // For TopoJSON/GeoJSON
}
