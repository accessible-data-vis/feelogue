using UnityEditor;
using UnityEngine;
using System.Linq;

/// <summary>
/// Custom editor for VegaChartLoader that shows a dropdown for layer selection
/// and checkboxes for series visibility filtering.
/// </summary>
[CustomEditor(typeof(VegaChartLoader))]
public class VegaChartLoaderEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        VegaChartLoader loader = (VegaChartLoader)target;

        // Draw all properties manually to control visibility
        SerializedProperty prop = serializedObject.GetIterator();
        bool enterChildren = true;

        while (prop.NextVisible(enterChildren))
        {
            enterChildren = false;

            // Skip the script field
            if (prop.name == "m_Script")
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.PropertyField(prop);
                }
                continue;
            }

            // Skip manualLayerName - we'll show a dropdown instead
            if (prop.name == "manualLayerName")
            {
                continue;
            }

            // Skip hiddenSeries - we'll show checkboxes instead
            if (prop.name == "hiddenSeries")
            {
                DrawSeriesCheckboxes(loader);
                continue;
            }

            // Draw all other properties normally
            EditorGUILayout.PropertyField(prop, true);
        }

        // Show custom dropdown for layer selection
        if (loader.useManualLayerSelection)
        {
            EditorGUILayout.Space(5);

            if (loader.availableLayerNames != null && loader.availableLayerNames.Count > 0)
            {
                // Create dropdown options
                string[] options = loader.availableLayerNames.ToArray();

                // Find current selection index
                int currentIndex = System.Array.IndexOf(options, loader.manualLayerName);
                if (currentIndex == -1) currentIndex = 0;

                // Show dropdown
                EditorGUI.BeginChangeCheck();
                int newIndex = EditorGUILayout.Popup("Layer", currentIndex, options);

                if (EditorGUI.EndChangeCheck())
                {
                    // Update the manual layer name
                    Undo.RecordObject(loader, "Change Manual Layer");
                    loader.manualLayerName = options[newIndex];
                    EditorUtility.SetDirty(loader);

                    // Apply the selected layer and re-render
                    if (Application.isPlaying)
                    {
                        loader.ApplyNamedLayer(options[newIndex]);
                    }
                }

                // Show manual refresh button in play mode
                if (Application.isPlaying)
                {
                    EditorGUILayout.Space(5);
                    if (GUILayout.Button("Refresh Chart Display"))
                    {
                        loader.RefreshChartDisplay();
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox("Layer will be applied when chart loads in Play mode", MessageType.Info);
                }
            }
            else
            {
                EditorGUILayout.HelpBox("Load a chart to populate available layers", MessageType.Info);

                // Show text field as fallback
                EditorGUI.BeginChangeCheck();
                string newName = EditorGUILayout.TextField("Manual Layer Name", loader.manualLayerName);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(loader, "Change Manual Layer Name");
                    loader.manualLayerName = newName;
                    EditorUtility.SetDirty(loader);
                }
            }
        }

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawSeriesCheckboxes(VegaChartLoader loader)
    {
        if (loader.availableSeries == null || loader.availableSeries.Count == 0)
        {
            EditorGUILayout.HelpBox("Load a chart to see available series", MessageType.Info);
            return;
        }

        EditorGUILayout.LabelField("Series Visibility", EditorStyles.boldLabel);

        var hiddenProp = serializedObject.FindProperty("hiddenSeries");

        foreach (string seriesName in loader.availableSeries)
        {
            // Check if this series is currently hidden
            bool isHidden = false;
            for (int i = 0; i < hiddenProp.arraySize; i++)
            {
                if (hiddenProp.GetArrayElementAtIndex(i).stringValue == seriesName)
                {
                    isHidden = true;
                    break;
                }
            }

            bool isVisible = !isHidden;
            EditorGUI.BeginChangeCheck();
            bool newVisible = EditorGUILayout.Toggle(seriesName, isVisible);

            if (EditorGUI.EndChangeCheck())
            {
                if (!newVisible && !isHidden)
                {
                    // Add to hidden list
                    hiddenProp.InsertArrayElementAtIndex(hiddenProp.arraySize);
                    hiddenProp.GetArrayElementAtIndex(hiddenProp.arraySize - 1).stringValue = seriesName;
                }
                else if (newVisible && isHidden)
                {
                    // Remove from hidden list
                    for (int i = 0; i < hiddenProp.arraySize; i++)
                    {
                        if (hiddenProp.GetArrayElementAtIndex(i).stringValue == seriesName)
                        {
                            hiddenProp.DeleteArrayElementAtIndex(i);
                            break;
                        }
                    }
                }
            }
        }
    }
}
