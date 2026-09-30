using UnityEditor;
using UnityEngine;
using System.Linq;

/// <summary>
/// Custom editor for VegaChartLoader: adds a refresh button in play mode.
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

            // Draw all other properties normally
            EditorGUILayout.PropertyField(prop, true);
        }

        // Manual refresh button in play mode
        if (Application.isPlaying)
        {
            EditorGUILayout.Space(5);
            if (GUILayout.Button("Refresh Chart Display"))
            {
                loader.RefreshChartDisplay();
            }
        }

        serializedObject.ApplyModifiedProperties();
    }
}
