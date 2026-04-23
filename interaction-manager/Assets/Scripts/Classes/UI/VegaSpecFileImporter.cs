using System;
using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Handles importing Vega-Lite spec files into the project.
/// Provides a file picker to select .json files and copies them to StreamingAssets.
/// </summary>
public class VegaSpecFileImporter : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private VegaChartLoader chartLoader;
    [SerializeField] private ButtonGUI buttonGUI;

    /// <summary>
    /// Opens file picker to select a Vega-Lite JSON spec.
    /// Copies it to StreamingAssets and optionally loads it.
    /// NOTE: This only works in Unity Editor, not in builds.
    /// </summary>
    public void ImportVegaSpec()
    {
#if UNITY_EDITOR
        ImportVegaSpecEditor();
#else
        Debug.LogWarning("File import only works in Unity Editor");
#endif
    }

#if UNITY_EDITOR
    private void ImportVegaSpecEditor()
    {
        string path = EditorUtility.OpenFilePanel(
            "Select Vega-Lite Spec",
            "",
            "json"
        );

        if (string.IsNullOrEmpty(path))
        {
            Debug.Log("File selection cancelled");
            return;
        }

        Debug.Log($"Selected file: {path}");

        try
        {
            // Read and validate the JSON
            string jsonContent = File.ReadAllText(path);
            var spec = Newtonsoft.Json.Linq.JObject.Parse(jsonContent);

            // Validate it's a Vega-Lite spec
            if (spec["$schema"] == null || !spec["$schema"].ToString().Contains("vega-lite"))
            {
                Debug.LogError(" Selected file is not a valid Vega-Lite spec (missing $schema)");
                return;
            }

            // Get filename
            string fileName = Path.GetFileName(path);

            // Check if it follows naming convention
            if (!fileName.StartsWith("compiled-vl-") || !fileName.EndsWith("-new.json"))
            {
                Debug.LogWarning($"File doesn't follow naming convention: compiled-vl-{{chartType}}-{{dataName}}-new.json");
                Debug.LogWarning($"Current name: {fileName}");

                bool rename = EditorUtility.DisplayDialog(
                    "Rename File?",
                    $"The file '{fileName}' doesn't follow the naming convention.\n\n" +
                    "Expected: compiled-vl-{{chartType}}-{{dataName}}-new.json\n\n" +
                    "Would you like to rename it?",
                    "Yes, Rename",
                    "No, Use As-Is"
                );

                if (rename)
                {
                    fileName = PromptForFileName(spec);
                    if (string.IsNullOrEmpty(fileName))
                    {
                        Debug.Log("Import cancelled");
                        return;
                    }
                }
            }

            // Copy to StreamingAssets
            string destPath = Path.Combine(Application.streamingAssetsPath, fileName);

            if (File.Exists(destPath))
            {
                bool overwrite = EditorUtility.DisplayDialog(
                    "File Exists",
                    $"'{fileName}' already exists in StreamingAssets.\n\nOverwrite?",
                    "Yes",
                    "Cancel"
                );

                if (!overwrite)
                {
                    Debug.Log("Import cancelled");
                    return;
                }
            }

            File.Copy(path, destPath, overwrite: true);
            Debug.Log($"Imported to: {destPath}");

            // Refresh Unity's AssetDatabase
            AssetDatabase.Refresh();

            // Ask if user wants to load the chart now
            bool loadNow = EditorUtility.DisplayDialog(
                "Import Successful",
                $"'{fileName}' has been imported to StreamingAssets.\n\nLoad this chart now?",
                "Yes, Load",
                "No"
            );

            // Rediscover charts to include the new one
            if (chartLoader != null)
            {
                chartLoader.RediscoverCharts();
            }

            // Refresh the dropdown to show the new chart
            if (buttonGUI != null)
            {
                buttonGUI.RefreshChartDropdown();
            }

            if (loadNow && chartLoader != null)
            {
                // Chart discovery already done above, just need to load it
                Debug.Log($"Loading newly imported chart...");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to import file: {ex.Message}");
        }
#else
        Debug.LogWarning("File import only works in Unity Editor");
#endif
    }

    private string PromptForFileName(Newtonsoft.Json.Linq.JObject spec)
    {
#if UNITY_EDITOR
        // Try to suggest a name based on spec content
        string markType = spec["mark"]?["type"]?.ToString() ?? spec["mark"]?.ToString() ?? "chart";
        string description = spec["description"]?.ToString() ?? "";
        string dataName = description.Split(' ')[0].ToLower();

        if (string.IsNullOrEmpty(dataName))
            dataName = "data";

        string suggestedName = $"compiled-vl-{markType}-{dataName}-new.json";

        string newName = EditorUtility.SaveFilePanel(
            "Save As",
            Application.streamingAssetsPath,
            suggestedName,
            "json"
        );

        if (string.IsNullOrEmpty(newName))
            return null;

        return Path.GetFileName(newName);
#else
        return null;
#endif
    }
}
