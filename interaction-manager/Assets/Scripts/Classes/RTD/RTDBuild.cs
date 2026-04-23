using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

[ExecuteAlways]
public class RTDBuild : MonoBehaviour
{
    [Header("Anchor (plane owner)")]
    public Transform RTDAnchor;

    [Header("Pin prefab (root at identity)")]
    public GameObject prefab;

    [Header("Grid")]
    public int gridX = 60;
    public int gridY = 40;
    public float spacing = 0.0025f;
    public bool centerOnOrigin = false;
    public bool centerOnLeap = false;
    public Transform leapTransform;       // assign your ServiceProvider or Leap hand root

    [Header("When parent (ServiceProvider) uses Z=180°")]
    [Tooltip("Counter-rotate this grid so its local frame is ‘normal’ even if the ServiceProvider is mirrored.")]
    public bool counterRotateForLeap = true;       // <- bake the 180/180 here
    [Tooltip("Mirror columns (left/right) in local space without touching transforms.")]
    public bool mirrorColumns = false;
    [Tooltip("Mirror rows (near/far) in local space without touching transforms.")]
    public bool mirrorRows = false;

    void OnEnable()
    {
        EnsureAnchored();
    }
    
    void Reset()
    {
        spacing = 0.0025f;
        gridX = 60;
        gridY = 40;
    }

    public void EnsureAnchored()
    {
        if (RTDAnchor == null) return;

        if (transform.parent != RTDAnchor)
            transform.SetParent(RTDAnchor, worldPositionStays: false);

        // Inherit anchor position; neutralize local TRS
        transform.localPosition = Vector3.zero;
        transform.localScale    = Vector3.one;

        // If the ServiceProvider is at Z=180°, this restores a "normal" local frame for the grid:
        transform.localRotation = counterRotateForLeap ? Quaternion.Euler(0f, 180f, 180f)
                                                       : Quaternion.identity;
    }

    [ContextMenu("Rebuild Pins")]
    public void Rebuild()
    {
        if (!prefab) { Debug.LogError("[BuildRTD] Prefab not assigned."); return; }
        EnsureAnchored();

        // Clear existing pins
        #if UNITY_EDITOR
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);
        #else
        foreach (Transform c in transform) Destroy(c.gameObject);
        #endif

        float offX = 0f;
        float offZ = 0f;

        if (centerOnOrigin)
        {
            offX = -(gridX - 1) * 0.5f * spacing;
            offZ =  (gridY - 1) * 0.5f * spacing;
        }
        else if (centerOnLeap && leapTransform != null)
        {
            // Get Leap position in RTD local space
            Vector3 leapLocal = transform.InverseTransformPoint(leapTransform.position);

            // Shift so Leap's position is the "center"
            offX = -(gridX - 1) * 0.5f * spacing - leapLocal.x;
            offZ =  (gridY - 1) * 0.5f * spacing - leapLocal.z;
        }

        int pinsLayer = LayerMask.NameToLayer("Pins");

        for (int y = 0; y < gridY; y++)
        {
            for (int x = 0; x < gridX; x++)
            {
                // local positions in our chosen convention:
                float px = x * spacing + offX;
                float pz = -y * spacing + offZ;  // rows go away from user

                // logical naming can be mirrored without moving geometry:
                int nx = mirrorColumns ? (gridX - 1 - x) : x;
                int ny = mirrorRows    ? (gridY - 1 - y) : y;

                Vector3 localPos = new Vector3(px, 0f, pz);

                #if UNITY_EDITOR
                var dot = (GameObject)PrefabUtility.InstantiatePrefab(prefab, transform);
                #else
                var dot = Instantiate(prefab, transform);
                #endif

                dot.name = $"{nx},{ny}";

                var tf = dot.transform;
                tf.localPosition = localPos;         // flat bed; tilt comes from anchor
                tf.localRotation = Quaternion.identity;
                tf.localScale    = Vector3.one;

                if (pinsLayer >= 0) dot.layer = pinsLayer;
            }
        }
    }

    #if UNITY_EDITOR
    void OnValidate()
    {
        if (!isActiveAndEnabled || Application.isPlaying) return;
        EditorApplication.delayCall += () => { if (this) { EnsureAnchored(); Rebuild(); } };
    }

    [CustomEditor(typeof(RTDBuild))]
    class BuildRTDEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            if (GUILayout.Button("Rebuild Now")) ((RTDBuild)target).Rebuild();
        }
    }
    #endif
}
