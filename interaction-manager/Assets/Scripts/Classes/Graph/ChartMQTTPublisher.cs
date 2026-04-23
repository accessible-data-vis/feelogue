using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Static helper for all MQTT publishing of chart data to the agent.
/// Extracted from VegaChartLoader for single-responsibility.
/// </summary>
public static class ChartMQTTPublisher
{
    /// <summary>
    /// Publish lightweight chart metadata index to the agent via MQTT.
    /// Called when MQTT connects (on startup or when switching local/remote).
    /// Only sends essential info for discovery (ids, names, types, fields).
    /// Full schema and images are sent on-demand when agent requests a specific chart.
    /// </summary>
    public static void PublishChartMetadataIndex(InterfaceMQTTManager mqttManager, List<DiscoveredChart> availableCharts)
    {
        if (mqttManager == null)
        {
            Debug.LogError(" Cannot publish chart metadata: MQTT Manager not initialized");
            return;
        }

        if (availableCharts == null || availableCharts.Count == 0)
        {
            Debug.LogWarning(" No charts available to publish metadata");
            return;
        }

        Debug.Log($"Publishing lightweight metadata for {availableCharts.Count} charts to agent...");

        // Build lightweight chart metadata array (NO schema or images)
        var chartMetadataList = new List<object>();

        foreach (var chart in availableCharts)
        {
            var metadata = new
            {
                chart_id = chart.id,
                data_name = chart.dataName,
                chart_type = chart.chartType,
                columns = chart.columns,
                chart_name = chart.chartName
                // NO vega_schema, image_data, or image_format - these come on-demand
            };

            chartMetadataList.Add(metadata);
        }

        // Wrap in message format with nested structure
        var message = new
        {
            chart_metadata_index = new
            {
                chart_count = availableCharts.Count,
                charts = chartMetadataList
            }
        };

        PublishChartMessage(mqttManager, message, $"Published lightweight metadata for {availableCharts.Count} charts", "Failed to publish chart metadata");
    }

    /// <summary>
    /// Publish full chart details (schema + image) for a specific chart.
    /// Called when agent requests details for a chart by ID, dataset, or data_name.
    /// </summary>
    public static void PublishChartDetails(InterfaceMQTTManager mqttManager, List<DiscoveredChart> availableCharts, int chartId)
    {
        DiscoveredChart chart = availableCharts.FirstOrDefault(c => c.id == chartId);
        if (chart == null)
        {
            Debug.LogError($"Cannot publish chart details: Chart ID {chartId} not found");
            return;
        }

        Debug.Log($"Publishing full details for chart {chartId}: {chart.DisplayName}");

        var message = new
        {
            message_type = "chart_details",
            chart_id = chart.id,
            data_name = chart.dataName,
            chart_type = chart.chartType,
            field = chart.field,
            columns = chart.columns,
            chart_name = chart.chartName,
            vega_schema = chart.schemaJson,
            image_data = chart.imageBase64,
            image_format = chart.imageFormat
        };

        PublishChartMessage(mqttManager, message, $"Published full details for chart {chartId}", "Failed to publish chart details");
    }

    /// <summary>
    /// Publish the current layer's data to the agent via MQTT.
    /// Called after applying a layer so the agent has the same data being displayed on the RTD.
    /// Sends layer-specific field names so agent knows which fields to use.
    /// Supports single-series, multi-series (color field), and layered (semantic zoom) charts.
    /// </summary>
    public static void PublishCurrentLayerData(
        InterfaceMQTTManager mqttManager,
        VegaSpec currentVegaSpec,
        string layerName,
        int windowStart, int windowSize,
        float windowYMin, float windowYMax,
        HashSet<string> hiddenSeries = null)
    {
        if (mqttManager == null)
        {
            Debug.LogWarning(" Cannot publish layer data: MQTT Manager not initialized");
            return;
        }

        if (currentVegaSpec?.Data?.Values == null)
        {
            Debug.LogWarning(" Cannot publish layer data: No data in current spec");
            return;
        }

        // Extract field names from current encoding
        string xField = currentVegaSpec.Encoding?.X?.Field ?? "unknown";
        string yField = currentVegaSpec.Encoding?.Y?.Field ?? "unknown";
        string colorField = currentVegaSpec.Encoding?.GetColorField();
        string chartType = currentVegaSpec.GetMarkType();

        // Determine the base temporal field (remove _rtd_index suffix if present)
        string baseXField = xField.EndsWith("_rtd_index")
            ? xField.Substring(0, xField.Length - "_rtd_index".Length)
            : xField;

        Debug.Log($"Publishing layer '{layerName}' data to agent ({currentVegaSpec.Data.Values.Count} rows, x='{baseXField}', y='{yField}', series='{colorField ?? "none"}')...");

        List<Dictionary<string, object>> filteredData;

        if (colorField != null)
        {
            // Multi-series: build unique X index map for windowing
            var uniqueXValues = new List<object>();
            var xIndexMap = new Dictionary<string, int>(); // X value string → unique index

            foreach (var row in currentVegaSpec.Data.Values)
            {
                if (row.ContainsKey(baseXField))
                {
                    string xKey = row[baseXField]?.ToString() ?? "";
                    if (!xIndexMap.ContainsKey(xKey))
                    {
                        xIndexMap[xKey] = uniqueXValues.Count;
                        uniqueXValues.Add(row[baseXField]);
                    }
                }
            }

            filteredData = currentVegaSpec.Data.Values.Select(row => {
                var filtered = new Dictionary<string, object>();

                if (row.ContainsKey(baseXField))
                    filtered[baseXField] = row[baseXField];

                if (row.ContainsKey(yField))
                    filtered[yField] = row[yField];

                if (row.ContainsKey(colorField))
                    filtered[colorField] = row[colorField];

                // Visibility: X window is based on unique X index
                string xKey = row.ContainsKey(baseXField) ? (row[baseXField]?.ToString() ?? "") : "";
                int xIndex = xIndexMap.ContainsKey(xKey) ? xIndexMap[xKey] : -1;
                bool isInXWindow = xIndex >= windowStart && xIndex < (windowStart + windowSize);

                bool isInYWindow = true;
                if (row.ContainsKey(yField))
                {
                    try
                    {
                        float yValue = Convert.ToSingle(row[yField]);
                        isInYWindow = yValue >= windowYMin && yValue <= windowYMax;
                    }
                    catch { isInYWindow = true; }
                }
                bool isHiddenSeries = hiddenSeries != null && row.ContainsKey(colorField) && hiddenSeries.Contains(row[colorField]?.ToString() ?? "");
                filtered["visible"] = !isHiddenSeries && isInXWindow && isInYWindow;

                return filtered;
            }).ToList();
        }
        else
        {
            // Single-series: index directly into rows
            filteredData = currentVegaSpec.Data.Values.Select((row, index) => {
                var filtered = new Dictionary<string, object>();

                if (row.ContainsKey(baseXField))
                    filtered[baseXField] = row[baseXField];

                if (row.ContainsKey(yField))
                    filtered[yField] = row[yField];

                bool isInXWindow = index >= windowStart && index < (windowStart + windowSize);
                bool isInYWindow = true;
                if (row.ContainsKey(yField))
                {
                    try
                    {
                        float yValue = Convert.ToSingle(row[yField]);
                        isInYWindow = yValue >= windowYMin && yValue <= windowYMax;
                    }
                    catch { isInYWindow = true; }
                }
                filtered["visible"] = isInXWindow && isInYWindow;

                return filtered;
            }).ToList();
        }

        // Build message — include series_field and chart_type when applicable
        var messageDict = new Dictionary<string, object>
        {
            { "message_type", "layer_data_update" },
            { "layer_name", layerName },
            { "chart_type", chartType },
            { "x_field", baseXField },
            { "y_field", yField },
            { "data_count", filteredData.Count },
            { "data", filteredData }
        };

        if (colorField != null)
        {
            messageDict["series_field"] = colorField;
        }

        PublishChartMessage(mqttManager, messageDict, $"Published layer '{layerName}' data", "Failed to publish layer data");
    }


    /// <summary>
    /// Publish chart data to MQTT for display in agent UI.
    /// Sends chart metadata + Vega-Lite spec + base64-encoded PNG image.
    /// </summary>
    public static void PublishChartToMQTT(InterfaceMQTTManager mqttManager, DiscoveredChart chart)
    {
        if (chart == null)
            return;

        try
        {
            // Read Vega-Lite JSON spec
            string jsonFullPath = chart.GetFullJsonPath();
            string vegaJson = null;
            if (!string.IsNullOrEmpty(jsonFullPath) && File.Exists(jsonFullPath))
            {
                vegaJson = File.ReadAllText(jsonFullPath);
            }

            // Get PNG path and convert to base64 (optional)
            string pngPath = chart.GetFullPngPath();
            string base64Image = null;
            if (!string.IsNullOrEmpty(pngPath) && File.Exists(pngPath))
            {
                base64Image = ConvertImageToBase64(pngPath);
            }

            // Create JSON payload with chart info + Vega spec + image
            var chartData = new
            {
                rtd_data_for_agent = new
                {
                chart_type = chart.chartType,
                data_name = chart.dataName,
                schema = vegaJson != null ? JToken.Parse(vegaJson) : null,
                image_base64 = base64Image,
                image_format = base64Image != null ? "png" : null
                }
            };

            // Serialize with indentation for readability
            string json = JsonConvert.SerializeObject(chartData, Formatting.Indented);

            Debug.Log($"Publishing chart data via MQTT: {chart.DisplayName} (Vega: {vegaJson != null}, PNG: {base64Image != null})");
            mqttManager.PublishChart(json);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to publish chart data: {ex.Message}");
        }
    }


    /// <summary>
    /// Convert an image file to a base64 string.
    /// </summary>
    public static string ConvertImageToBase64(string imagePath)
    {
        try
        {
            byte[] imageBytes = File.ReadAllBytes(imagePath);
            return Convert.ToBase64String(imageBytes);
        }
        catch (IOException e)
        {
            Debug.LogError($"Failed to read image file: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Serialize a message to JSON, publish via MQTT, and log success/failure.
    /// </summary>
    public static void PublishChartMessage(InterfaceMQTTManager mqttManager, object message, string successLabel, string errorLabel)
    {
        string json = JsonConvert.SerializeObject(message, Formatting.Indented);
        try
        {
            mqttManager.PublishChart(json);
            Debug.Log($"{successLabel} ({json.Length} bytes)");
        }
        catch (Exception ex)
        {
            Debug.LogError($"{errorLabel}: {ex.Message}");
        }
    }
}
