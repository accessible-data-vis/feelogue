using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

/// <summary>
/// Generates PNG preview images from Vega-Lite JSON specifications.
/// Uses Python vl-convert library to render high-quality chart previews.
/// </summary>
public class RTDPreviewGenerator : MonoBehaviour
{
    private string pythonPath;
    private string scriptPath;

    void Awake()
    {
        pythonPath = EnvLoader.Get("PYTHON_PATH", "python3");
        scriptPath = Path.Combine(Application.dataPath, "StreamingAssets", "Tools", "generate_chart_preview.py");
    }

    private string GetScriptPath()
    {
        return scriptPath;
    }

    /// <summary>
    /// Generate PNG preview for a chart if it doesn't already exist.
    /// </summary>
    /// <param name="chart">The discovered chart to generate preview for</param>
    /// <returns>True if preview exists or was successfully generated</returns>
    public bool EnsurePreviewExists(DiscoveredChart chart)
    {
        if (chart == null)
        {
            UnityEngine.Debug.LogError(" Cannot generate preview: chart is null");
            return false;
        }

        // Check if PNG already exists
        string pngPath = chart.GetFullPngPath();
        if (!string.IsNullOrEmpty(pngPath) && File.Exists(pngPath))
        {
            UnityEngine.Debug.Log($"PNG preview already exists: {chart.pngFilePath}");
            return true;
        }

        // Generate new preview
        return GeneratePreview(chart);
    }

    /// <summary>
    /// Generate PNG preview from Vega-Lite JSON specification.
    /// </summary>
    /// <param name="chart">The chart to generate preview for</param>
    /// <param name="scaleFactor">PNG scale factor for resolution (default: 2.0)</param>
    /// <returns>True if generation succeeded</returns>
    public bool GeneratePreview(DiscoveredChart chart, float scaleFactor = 2.0f)
    {
        if (chart == null)
        {
            UnityEngine.Debug.LogError(" Cannot generate preview: chart is null");
            return false;
        }

        string jsonPath = chart.GetFullJsonPath();
        if (string.IsNullOrEmpty(jsonPath) || !File.Exists(jsonPath))
        {
            UnityEngine.Debug.LogError($"Cannot generate preview: JSON file not found at {jsonPath}");
            return false;
        }

        // Construct output PNG path
        string outputFilename = $"chart-{chart.chartType}-{chart.dataName}-new.png";
        string outputPath = Path.Combine(Application.streamingAssetsPath, outputFilename);

        UnityEngine.Debug.Log($"Generating PNG preview: {outputFilename}");

        try
        {
            string resolvedScriptPath = GetScriptPath();

            // Validate Python and script exist
            if (!File.Exists(pythonPath))
            {
                UnityEngine.Debug.LogError($"Python not found at: {pythonPath}");
                return false;
            }

            if (!File.Exists(resolvedScriptPath))
            {
                UnityEngine.Debug.LogError($"Preview generation script not found at: {resolvedScriptPath}");
                return false;
            }

            // Build Python command
            string arguments = $"\"{resolvedScriptPath}\" --json-path \"{jsonPath}\" --png-path \"{outputPath}\" --scale {scaleFactor}";

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            UnityEngine.Debug.Log($"Running: {pythonPath} {arguments}");

            // Execute Python script
            Process process = Process.Start(psi);

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            // Log output
            if (!string.IsNullOrEmpty(output))
            {
                UnityEngine.Debug.Log($"Python output: {output}");
            }

            // Check for errors
            if (process.ExitCode != 0)
            {
                UnityEngine.Debug.LogError($"Preview generation failed (exit code {process.ExitCode}):");
                if (!string.IsNullOrEmpty(error))
                {
                    UnityEngine.Debug.LogError(error);
                }
                return false;
            }

            // Verify PNG was created
            if (File.Exists(outputPath))
            {
                chart.pngFilePath = outputFilename;
                UnityEngine.Debug.Log($"Preview generated successfully: {outputFilename}");
                return true;
            }
            else
            {
                UnityEngine.Debug.LogError($"Preview generation completed but file not found: {outputPath}");
                return false;
            }
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError($"Exception generating preview: {ex.Message}");
            UnityEngine.Debug.LogError(ex.StackTrace);
            return false;
        }
    }

    /// <summary>
    /// Batch generate previews for all charts missing PNG files.
    /// </summary>
    /// <param name="charts">List of discovered charts</param>
    /// <returns>Number of previews successfully generated</returns>
    public int GenerateMissingPreviews(System.Collections.Generic.List<DiscoveredChart> charts)
    {
        if (charts == null || charts.Count == 0)
        {
            UnityEngine.Debug.LogWarning(" No charts provided for preview generation");
            return 0;
        }

        int generated = 0;
        int skipped = 0;

        UnityEngine.Debug.Log($"Checking {charts.Count} charts for missing previews...");

        foreach (var chart in charts)
        {
            // Skip if preview already exists
            string pngPath = chart.GetFullPngPath();
            if (!string.IsNullOrEmpty(pngPath) && File.Exists(pngPath))
            {
                skipped++;
                continue;
            }

            // Generate preview
            if (GeneratePreview(chart))
            {
                generated++;
            }
        }

        UnityEngine.Debug.Log($"Preview generation complete: {generated} generated, {skipped} skipped");
        return generated;
    }
}
