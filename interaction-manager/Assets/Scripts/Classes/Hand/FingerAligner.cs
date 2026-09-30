using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Detaches a fingertip collider from the hand so FingerSnapper can position it.</summary>
public class FingerAligner : MonoBehaviour
{
    void Start()
    {
        // Detach from parent but don't preserve world position
        // (FingerSnapper will handle positioning)
        transform.SetParent(null, false);
        
        AppLog.Detail(LogArea.Device, $"{name} detached from parent hand");
    }
}
