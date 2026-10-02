using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// Discovers chart files automatically by scanning the project structure.
/// Each chart JSON must contain a top-level "usermeta" block with dataName,
/// chartType, and displayName. The filename itself is only an identifier.
/// </summary>
public class ChartDiscoveryService
{
    // Loose pattern - middle part is an identifier only. Metadata lives in the JSON.
    private const string CHART_JSON_PATTERN = "compiled-vl-*.json";

    private List<DiscoveredChart> _discoveredCharts = new List<DiscoveredChart>();

    public List<DiscoveredChart> DiscoverCharts()
    {
        _discoveredCharts.Clear();

        string streamingAssetsPath = Application.streamingAssetsPath;
        if (!Directory.Exists(streamingAssetsPath))
        {
            Debug.LogError($"StreamingAssets folder not found: {streamingAssetsPath}");
            return _discoveredCharts;
        }

        string[] jsonFiles = Directory.GetFiles(streamingAssetsPath, CHART_JSON_PATTERN);
        AppLog.Detail(LogArea.Chart, $"Found {jsonFiles.Length} candidate Vega-Lite JSON files");

        int chartId = 1;
        foreach (string jsonFilePath in jsonFiles)
        {
            string filename = Path.GetFileName(jsonFilePath);
            var chart = TryLoadChart(jsonFilePath, filename, chartId);
            if (chart == null) continue;

            _discoveredCharts.Add(chart);
            chartId++;
            AppLog.Detail(LogArea.Chart, $"Discovered chart {chart.id}: {chart.DisplayName} (field={chart.field}, columns={chart.columns.Count})");
        }

        AppLog.Info(LogArea.Chart, $"Total charts discovered: {_discoveredCharts.Count}");
        return _discoveredCharts;
    }

    /// <summary>
    /// Parse one spec file and build a DiscoveredChart, or return null if the file
    /// is empty, malformed, or missing required metadata.
    /// </summary>
    private DiscoveredChart TryLoadChart(string jsonFilePath, string filename, int chartId)
    {
        string jsonContent;
        try
        {
            jsonContent = File.ReadAllText(jsonFilePath);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Skipping {filename}: failed to read file ({ex.Message})");
            return null;
        }

        if (string.IsNullOrWhiteSpace(jsonContent))
        {
            Debug.LogWarning($"Skipping {filename}: file is empty");
            return null;
        }

        JObject spec;
        try
        {
            spec = JObject.Parse(jsonContent);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Skipping {filename}: invalid JSON ({ex.Message})");
            return null;
        }

        var metaToken = spec["usermeta"];
        if (metaToken == null || metaToken.Type != JTokenType.Object)
        {
            Debug.LogWarning($"Skipping {filename}: missing required 'usermeta' block");
            return null;
        }

        ChartMetadata metadata;
        try
        {
            metadata = metaToken.ToObject<ChartMetadata>();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Skipping {filename}: malformed metadata ({ex.Message})");
            return null;
        }

        if (!metadata.IsValid())
        {
            Debug.LogWarning($"Skipping {filename}: usermeta must include dataName, chartType, and displayName");
            return null;
        }

        string field = spec["encoding"]?["y"]?["field"]?.ToString()
            ?? (spec["layer"]?.FirstOrDefault()?["encoding"]?["y"]?["field"]?.ToString());
        string chartName = metadata.DisplayName;
        List<string> columns = ExtractColumns(spec);

        string pngFilename = ResolvePreviewImage(metadata);
        var (imageBase64, imageFormat) = TryLoadPng(pngFilename);

        return new DiscoveredChart
        {
            id = chartId,
            dataName = metadata.DataName,
            chartType = metadata.ChartType,
            variant = metadata.Variant,
            dataset = null,
            field = field ?? metadata.DataName,
            chartName = chartName,
            jsonFilePath = filename,
            pngFilePath = pngFilename,
            columns = columns,
            schemaJson = jsonContent,
            imageBase64 = imageBase64,
            imageFormat = imageFormat
        };
    }

    public DiscoveredChart GetChartById(int id) =>
        _discoveredCharts.FirstOrDefault(c => c.id == id);

    public List<DiscoveredChart> GetAllCharts() => _discoveredCharts;

    /// <summary>
    /// Resolve preview PNG filename: explicit usermeta.previewImage wins;
    /// otherwise fall back to the legacy chart-{chartType}-{dataName}[-{variant}]-new.png convention.
    /// </summary>
    private string ResolvePreviewImage(ChartMetadata metadata)
    {
        if (!string.IsNullOrEmpty(metadata.PreviewImage))
            return metadata.PreviewImage;

        string variantSuffix = string.IsNullOrEmpty(metadata.Variant) ? "" : $"-{metadata.Variant}";
        string candidate = $"chart-{metadata.ChartType}-{metadata.DataName}{variantSuffix}-new.png";
        string fullPath = Path.Combine(Application.streamingAssetsPath, candidate);
        if (File.Exists(fullPath)) return candidate;

        // Fall back to variant-less filename
        string baseline = $"chart-{metadata.ChartType}-{metadata.DataName}-new.png";
        return File.Exists(Path.Combine(Application.streamingAssetsPath, baseline)) ? baseline : null;
    }

    private (string base64, string format) TryLoadPng(string pngFilename)
    {
        if (string.IsNullOrEmpty(pngFilename)) return (null, null);

        string fullPngPath = Path.Combine(Application.streamingAssetsPath, pngFilename);
        if (!File.Exists(fullPngPath)) return (null, null);

        try
        {
            byte[] imageBytes = File.ReadAllBytes(fullPngPath);
            return (Convert.ToBase64String(imageBytes), "png");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to load preview {pngFilename}: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// Extract all column names from a Vega-Lite spec.
    /// Gets column names from data values (first row keys), plus lookup transform data for maps.
    /// </summary>
    private List<string> ExtractColumns(JObject spec)
    {
        HashSet<string> columns = new HashSet<string>();

        try
        {
            var dataValues = spec["data"]?["values"] as JArray;
            AddRowKeys(dataValues, columns);

            var layers = spec["layer"] as JArray;
            if (layers != null)
            {
                foreach (var layer in layers)
                {
                    if (layer.Type != JTokenType.Object) continue;

                    var transforms = layer["transform"] as JArray;
                    if (transforms == null) continue;

                    foreach (var transform in transforms)
                    {
                        if (transform.Type != JTokenType.Object) continue;
                        if (transform["lookup"]?.Type != JTokenType.String) continue;

                        var lookupData = transform["from"]?["data"]?["values"] as JArray;
                        AddRowKeys(lookupData, columns);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to extract columns: {ex.Message}");
        }

        return columns.ToList();
    }

    private static void AddRowKeys(JArray values, HashSet<string> columns)
    {
        if (values == null || values.Count == 0) return;
        if (!(values[0] is JObject firstRow)) return;
        foreach (var prop in firstRow.Properties()) columns.Add(prop.Name);
    }
}

/// <summary>
/// Represents a discovered chart with all its associated files and metadata.
/// </summary>
[Serializable]
public class DiscoveredChart
{
    public int id;
    public string dataName;
    public string chartType;
    public string variant;         // Optional variant (e.g., "daily", "weekly")
    public string dataset;
    public string field;
    public string jsonFilePath;
    public string pngFilePath;

    public string chartName;
    public List<string> columns;
    public string schemaJson;
    public string imageBase64;
    public string imageFormat;

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrEmpty(chartName)) return chartName;
            string variantSuffix = string.IsNullOrEmpty(variant) ? "" : $" ({variant})";
            return $"{Capitalize(chartType)} - {Capitalize(dataName)}{variantSuffix}";
        }
    }

    public bool IsComplete => !string.IsNullOrEmpty(jsonFilePath);

    public string GetFullJsonPath() => string.IsNullOrEmpty(jsonFilePath)
        ? null
        : Path.Combine(Application.streamingAssetsPath, jsonFilePath);

    public string GetFullPngPath() => string.IsNullOrEmpty(pngFilePath)
        ? null
        : Path.Combine(Application.streamingAssetsPath, pngFilePath);

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);
}
