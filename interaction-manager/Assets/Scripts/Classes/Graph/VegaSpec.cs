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

    [JsonProperty("transform")]
    public List<JToken> Transform { get; set; }


    [JsonProperty("overview")]
    public Dictionary<string, string> Overview { get; set; }

    [JsonProperty("metadata")]
    public ChartMetadata Metadata { get; set; }

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

/// <summary>
/// Chart metadata block. Lives inside the Vega spec JSON under "metadata".
/// Replaces filename-based parsing - the filename is now just an identifier.
/// </summary>
[Serializable]
public class ChartMetadata
{
    [JsonProperty("dataName")]
    public string DataName { get; set; }

    [JsonProperty("chartType")]
    public string ChartType { get; set; }

    [JsonProperty("variant")]
    public string Variant { get; set; }

    [JsonProperty("displayName")]
    public string DisplayName { get; set; }

    [JsonProperty("previewImage")]
    public string PreviewImage { get; set; }

    public bool IsValid() =>
        !string.IsNullOrEmpty(DataName) &&
        !string.IsNullOrEmpty(ChartType) &&
        !string.IsNullOrEmpty(DisplayName);
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

    [JsonProperty("shape")]
    public JToken Shape { get; set; }  // {"field", "scale": {"domain": [...], "range": [...]}}

    [JsonProperty("tooltip")]
    public JToken Tooltip { get; set; }  // a field definition or a list of them

    /// <summary>Fields the tooltip channel names, in order. Empty when it names none.</summary>
    public List<string> GetTooltipFields()
    {
        var fields = new List<string>();
        if (Tooltip == null) return fields;
        IEnumerable<JToken> defs = Tooltip.Type == JTokenType.Array ? Tooltip.Children() : (IEnumerable<JToken>)new[] { Tooltip };
        foreach (var def in defs)
        {
            string field = def.Type == JTokenType.Object ? def["field"]?.ToString() : null;
            if (!string.IsNullOrEmpty(field)) fields.Add(field);
        }
        return fields;
    }

    /// <summary>
    /// Series name -> Vega-Lite shape name, from shape.scale.domain/range when the shape
    /// channel encodes the given (series) field. Empty when the spec declares none.
    /// </summary>
    public Dictionary<string, string> GetShapeMap(string seriesField)
    {
        var map = new Dictionary<string, string>();
        if (Shape == null || Shape.Type != JTokenType.Object || seriesField == null) return map;
        if (Shape["field"]?.ToString() != seriesField) return map;
        var domain = Shape["scale"]?["domain"] as JArray;
        var range = Shape["scale"]?["range"] as JArray;
        if (domain == null || range == null) return map;
        for (int i = 0; i < Math.Min(domain.Count, range.Count); i++)
            map[domain[i].ToString()] = range[i].ToString();
        return map;
    }

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
public class VegaDataFormat
{
    [JsonProperty("type")]
    public string Type { get; set; }  // "json", "topojson", "csv"

    [JsonProperty("property")]
    public string Property { get; set; }  // For TopoJSON/GeoJSON
}
