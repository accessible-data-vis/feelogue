using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class FingerAligner : MonoBehaviour
{
    void Start()
    {
        // Detach from parent but don't preserve world position
        // (FingerSnapper will handle positioning)
        transform.SetParent(null, false);
        
        Debug.Log($"{name} detached from parent hand");
    }
}
