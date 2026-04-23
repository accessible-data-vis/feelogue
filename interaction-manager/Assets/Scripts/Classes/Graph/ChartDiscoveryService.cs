using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using Newtonsoft.Json.Linq;

/// <summary>
/// Discovers chart files automatically by scanning the project structure.
/// Expects charts to follow naming convention: compiled-vl-{chartType}-{dataName}-new.json
/// Automatically finds matching PNG files.
/// </summary>
public class ChartDiscoveryService
{
    // ===== Constants =====
    private const string CHART_JSON_PATTERN = "compiled-vl-*-new.json";

    // Regex to extract dataName and chartType from filename: compiled-vl-{dataName}-{chartType}-new.json
    // Example: compiled-vl-tslastock-line-new.json → dataName: "tslastock", chartType: "line"
    private static readonly Regex ChartFilenameRegex = new Regex(
        @"^compiled-vl-(?<dataName>[^-]+)-(?<chartType>.+)-new\.json$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    // ===== Discovered Charts =====
    private List<DiscoveredChart> _discoveredCharts = new List<DiscoveredChart>();

    /// <summary>
    /// Scans the project for chart JSON files and returns discovered charts.
    /// </summary>
    public List<DiscoveredChart> DiscoverCharts()
    {
        _discoveredCharts.Clear();

        string streamingAssetsPath = Application.streamingAssetsPath;

        if (!Directory.Exists(streamingAssetsPath))
        {
            Debug.LogError($"StreamingAssets folder not found: {streamingAssetsPath}");
            return _discoveredCharts;
        }

        // Get all Vega-Lite JSON files
        string[] jsonFiles = Directory.GetFiles(streamingAssetsPath, CHART_JSON_PATTERN);
        Debug.Log($"Found {jsonFiles.Length} Vega-Lite JSON files");

        int chartId = 1;
        foreach (string jsonFilePath in jsonFiles)
        {
            string filename = Path.GetFileName(jsonFilePath);

            var match = ChartFilenameRegex.Match(filename);
            if (match.Success)
            {
                string chartType = match.Groups["chartType"].Value;
                string dataName = match.Groups["dataName"].Value;

                // Parse JSON to extract metadata
                var (chartName, field, columns, schemaJson) = ExtractChartMetadata(jsonFilePath);

                // Find and encode PNG preview
                string pngPath = FindPngFile(dataName, chartType);
                string imageBase64 = null;
                string imageFormat = null;

                if (!string.IsNullOrEmpty(pngPath))
                {
                    string fullPngPath = Path.Combine(Application.streamingAssetsPath, pngPath);
                    if (File.Exists(fullPngPath))
                    {
                        byte[] imageBytes = File.ReadAllBytes(fullPngPath);
                        imageBase64 = Convert.ToBase64String(imageBytes);
                        imageFormat = "png";
                    }
                }

                var chart = new DiscoveredChart
                {
                    id = chartId++,
                    dataName = dataName,
                    chartType = chartType,
                    dataset = null,
                    field = field ?? dataName,      // Fallback to dataName if not found
                    chartName = chartName,           // From Vega description field
                    jsonFilePath = filename,  // Store relative path
                    pngFilePath = pngPath,
                    columns = columns ?? new List<string>(),
                    schemaJson = schemaJson,
                    imageBase64 = imageBase64,
                    imageFormat = imageFormat
                };

                _discoveredCharts.Add(chart);
                Debug.Log($"Discovered chart {chart.id}: {chart.DisplayName} (name={chart.chartName}, field={chart.field}, columns={chart.columns.Count})");
            }
            else
            {
                Debug.LogWarning($"JSON file doesn't match naming convention: {filename}");
            }
        }

        Debug.Log($"Total charts discovered: {_discoveredCharts.Count}");
        return _discoveredCharts;
    }

    /// <summary>
    /// Get a discovered chart by its ID.
    /// </summary>
    public DiscoveredChart GetChartById(int id)
    {
        return _discoveredCharts.FirstOrDefault(c => c.id == id);
    }

    /// <summary>
    /// Get all discovered charts.
    /// </summary>
    public List<DiscoveredChart> GetAllCharts()
    {
        return _discoveredCharts;
    }

    // ===== Private Helper Methods =====

    /// <summary>
    /// Extract metadata from Vega-Lite JSON spec.
    /// Returns: (chartName, field, columns, schemaJson)
    /// </summary>
    private (string chartName, string field, List<string> columns, string schemaJson) ExtractChartMetadata(string jsonFilePath)
    {
        try
        {
            string jsonContent = File.ReadAllText(jsonFilePath);
            var spec = JObject.Parse(jsonContent);

            // Extract field from y-axis encoding
            string field = spec["encoding"]?["y"]?["field"]?.ToString();

            // Extract chart name from description field
            string chartName = spec["description"]?.ToString();

            // Extract all unique column names from the spec
            List<string> columns = ExtractColumns(spec);

            return (chartName, field, columns, jsonContent);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to extract metadata from {Path.GetFileName(jsonFilePath)}: {ex.Message}");
            return (null, null, null, null);
        }
    }

    /// <summary>
    /// Extract all column names from a Vega-Lite spec.
    /// Gets column names from the data values (first row keys).
    /// For maps, extracts from lookup transform data.
    /// </summary>
    private List<string> ExtractColumns(JObject spec)
    {
        HashSet<string> columns = new HashSet<string>();

        try
        {
            // Check top-level data values
            var dataToken = spec["data"];
            if (dataToken != null && dataToken.Type == JTokenType.Object)
            {
                var dataValues = dataToken["values"] as JArray;
                if (dataValues != null && dataValues.Count > 0)
                {
                    var firstRow = dataValues[0] as JObject;
                    if (firstRow != null)
                    {
                        foreach (var prop in firstRow.Properties())
                        {
                            columns.Add(prop.Name);
                        }
                    }
                }
            }

            // For maps: check lookup transform data in layers
            var layers = spec["layer"] as JArray;
            if (layers != null)
            {
                foreach (var layer in layers)
                {
                    if (layer.Type != JTokenType.Object)
                        continue;

                    var transforms = layer["transform"] as JArray;
                    if (transforms != null)
                    {
                        foreach (var transform in transforms)
                        {
                            if (transform.Type != JTokenType.Object)
                                continue;

                            // Check if this is a lookup transform
                            var lookupToken = transform["lookup"];
                            if (lookupToken == null || lookupToken.Type != JTokenType.String)
                                continue;

                            var fromToken = transform["from"];
                            if (fromToken == null || fromToken.Type != JTokenType.Object)
                                continue;

                            var dataObj = fromToken["data"];
                            if (dataObj == null || dataObj.Type != JTokenType.Object)
                                continue;

                            var lookupData = dataObj["values"] as JArray;
                            if (lookupData != null && lookupData.Count > 0)
                            {
                                var firstRow = lookupData[0] as JObject;
                                if (firstRow != null)
                                {
                                    foreach (var prop in firstRow.Properties())
                                    {
                                        columns.Add(prop.Name);
                                    }
                                }
                            }
                        }
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


    /// <summary>
    /// Find the PNG preview file matching the chart (optional).
    /// Expected: chart-{chartType}-{dataName}-new.png
    /// </summary>
    private string FindPngFile(string dataName, string chartType)
    {
        string expectedFilename = $"chart-{chartType}-{dataName}-new.png";
        string pngPath = Path.Combine(Application.streamingAssetsPath, expectedFilename);

        if (File.Exists(pngPath))
        {
            Debug.Log($"Found PNG: {expectedFilename}");
            return expectedFilename; // Return relative path for StreamingAssets
        }

        Debug.Log($"ℹ PNG preview not found: {expectedFilename} (optional)");
        return null;
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
    public string dataset;         // Dataset name (e.g., "housing", "australian")
    public string field;           // Field name (e.g., "interest rate (%)", "gdp")
    public string jsonFilePath;    // Relative path in StreamingAssets (required)
    public string pngFilePath;     // Relative path in StreamingAssets (optional preview)

    // Extended metadata for agent
    public string chartName;       // Chart name from Vega description field
    public List<string> columns;   // All columns/fields in the data
    public string schemaJson;      // Full Vega-Lite spec as JSON string
    public string imageBase64;     // Base64-encoded PNG preview
    public string imageFormat;     // Image format (e.g., "png")

    /// <summary>
    /// User-friendly display name for the chart.
    /// </summary>
    public string DisplayName => $"{char.ToUpper(chartType[0]) + chartType.Substring(1)} - {char.ToUpper(dataName[0]) + dataName.Substring(1)}";

    /// <summary>
    /// Check if all required files are present.
    /// </summary>
    public bool IsComplete => !string.IsNullOrEmpty(jsonFilePath);

    /// <summary>
    /// Get full path to JSON file.
    /// </summary>
    public string GetFullJsonPath() => string.IsNullOrEmpty(jsonFilePath)
        ? null
        : Path.Combine(Application.streamingAssetsPath, jsonFilePath);

    /// <summary>
    /// Get full path to PNG preview file (optional).
    /// </summary>
    public string GetFullPngPath() => string.IsNullOrEmpty(pngFilePath)
        ? null
        : Path.Combine(Application.streamingAssetsPath, pngFilePath);
}
