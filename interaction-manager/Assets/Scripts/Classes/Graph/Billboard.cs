using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Keeps a label facing the camera.</summary>
public class Billboard : MonoBehaviour
{
    void LateUpdate()
    {
        if (Camera.main != null)
            transform.rotation = Camera.main.transform.rotation;
    }
}