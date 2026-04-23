using UnityEngine;
using Leap.Unity;
using Leap;

/// <summary>
/// Controls hand visibility based on tracking confidence
/// Attach to CapsuleHandEdit objects
/// </summary>
[RequireComponent(typeof(CapsuleHandEdit))]
public class HandConfidenceController : MonoBehaviour
{
    [Header("Confidence Settings")]
    [SerializeField, Range(0f, 1f)] 
    private float minConfidenceThreshold = 0.1f;
    
    [Header("What to Control")]
    [SerializeField] private bool hideVisualComponents = true;
    [SerializeField] private bool disableColliders = true;
    [SerializeField] private bool disableChildScripts = true;
    
    private CapsuleHandEdit capsuleHand;
    private bool wasVisible = true;
    
    // Cache components for performance
    private MeshRenderer[] meshRenderers;
    private Collider[] colliders;
    private MonoBehaviour[] childScripts;
    
    void Start()
    {
        capsuleHand = GetComponent<CapsuleHandEdit>();
        
        // Cache components
        meshRenderers = GetComponentsInChildren<MeshRenderer>();
        colliders = GetComponentsInChildren<Collider>();
        childScripts = GetComponentsInChildren<MonoBehaviour>();
    }
    
    void Update()
    {
        if (capsuleHand == null) return;
        
        Hand leapHand = capsuleHand.GetLeapHand();
        bool shouldBeVisible = leapHand != null && leapHand.Confidence > minConfidenceThreshold;
        
        // Only change state if it actually changed
        if (shouldBeVisible != wasVisible)
        {
            SetHandVisible(shouldBeVisible);
            wasVisible = shouldBeVisible;
            
            float confidence = leapHand?.Confidence ?? 0f;
            //Debug.Log($"[{name}] Hand visibility: {shouldBeVisible} (confidence: {confidence:F3})");
        }
    }
    
    private void SetHandVisible(bool visible)
    {
        // Control visual rendering
        if (hideVisualComponents && meshRenderers != null)
        {
            foreach (var renderer in meshRenderers)
            {
                if (renderer != null)
                    renderer.enabled = visible;
            }
        }
        
        // Control collision detection
        if (disableColliders && colliders != null)
        {
            foreach (var collider in colliders)
            {
                if (collider != null)
                    collider.enabled = visible;
            }
        }
        
        // Control child scripts (like PositionReportNew, FingerSnapper)
        if (disableChildScripts && childScripts != null)
        {
            foreach (var script in childScripts)
            {
                // Don't disable this script itself
                if (script != null && script != this)
                    script.enabled = visible;
            }
        }
    }
    
    public float GetCurrentConfidence()
    {
        if (capsuleHand == null) return 0f;
        Hand leapHand = capsuleHand.GetLeapHand();
        return leapHand?.Confidence ?? 0f;
    }
    
    public bool IsCurrentlyVisible()
    {
        return wasVisible;
    }
}
