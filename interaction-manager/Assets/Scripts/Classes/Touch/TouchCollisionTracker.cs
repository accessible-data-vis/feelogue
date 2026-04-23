using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Tracks collision events with tactile pins, manages visual feedback,
/// and accumulates touched coordinates for spatial processing.
/// </summary>
public class TouchCollisionTracker
{
    // Collision state
    private Dictionary<GameObject, float> _enterTimes = new Dictionary<GameObject, float>();
    private Dictionary<GameObject, Color> _originalColors = new Dictionary<GameObject, Color>();
    private HashSet<GameObject> _collidingObjects = new HashSet<GameObject>();
    private HashSet<Vector2Int> _coords = new HashSet<Vector2Int>(10);

    // Timing
    private float _touchStartTime = -1f;
    private bool _inContact = false;

    // Visual feedback
    private Color _touchHighlightColor = new Color(1f, 0.5f, 0f); // Orange

    // Events
    public event System.Action OnContactStarted;
    public event System.Action OnContactEnded;
    public event System.Action<float> OnTouchCompleted; // Passes hold duration and enter time

    /// <summary>
    /// Handles collision enter events for tactile pins.
    /// </summary>
    /// <param name="collider">The collider that was entered</param>
    /// <returns>True if this is a valid pin collision, false otherwise</returns>
    public bool HandleEnter(Collider collider)
    {
        _inContact = true;
        OnContactStarted?.Invoke();

        // Parse pin coordinates from GameObject name
        Vector2Int? pinCoord = ParsePinCoordinate(collider);
        if (!pinCoord.HasValue)
            return false;

        // Apply visual feedback
        ApplyTouchHighlight(collider);

        // Track collision
        _collidingObjects.Add(collider.gameObject);

        if (_touchStartTime < 0f)
            _touchStartTime = Time.time;

        if (!_enterTimes.ContainsKey(collider.gameObject))
            _enterTimes[collider.gameObject] = Time.time;

        // Accumulate coordinates
        _coords.Add(pinCoord.Value);

        return true;
    }

    /// <summary>
    /// Handles collision exit events for tactile pins.
    /// </summary>
    /// <param name="collider">The collider that was exited</param>
    /// <param name="enterTime">Out parameter: when this specific pin was entered</param>
    /// <param name="holdDuration">Out parameter: calculated hold duration</param>
    /// <returns>True if touch is complete and should be processed, false if still touching other pins</returns>
    public bool HandleExit(Collider collider, out float enterTime, out float holdDuration)
    {
        enterTime = 0f;
        holdDuration = 0f;

        // Validate it's a pin
        Vector2Int? pinCoord = ParsePinCoordinate(collider);
        if (!pinCoord.HasValue)
            return false;

        // Restore original color
        RestoreTouchHighlight(collider);
        _collidingObjects.Remove(collider.gameObject);

        // Still touching other pins - don't process yet
        if (_collidingObjects.Count > 0)
            return false;

        // All pins cleared — contact is fully ended
        _inContact = false;
        OnContactEnded?.Invoke();

        // Get enter time for this specific pin
        if (!_enterTimes.TryGetValue(collider.gameObject, out enterTime))
            return false;

        _enterTimes.Remove(collider.gameObject);

        // Calculate hold duration from overall touch start
        holdDuration = _touchStartTime > 0f ? Time.time - _touchStartTime : Time.time - enterTime;

        // Fire completion event
        OnTouchCompleted?.Invoke(holdDuration);

        return true;
    }

    /// <summary>
    /// Gets the accumulated touched coordinates.
    /// </summary>
    public HashSet<Vector2Int> GetCoordinates() => _coords;

    /// <summary>
    /// Clears the accumulated coordinates.
    /// </summary>
    public void ClearCoordinates()
    {
        _coords.Clear();
    }

    /// <summary>
    /// Resets all collision tracking state.
    /// </summary>
    public void Reset()
    {
        _touchStartTime = -1f;
        _coords.Clear();
        _enterTimes.Clear();
        _collidingObjects.Clear();
        // Note: Don't clear _originalColors as they may still be needed for color restoration
    }

    /// <summary>
    /// Gets the touch start time.
    /// </summary>
    public float GetTouchStartTime() => _touchStartTime;

    /// <summary>
    /// Gets whether currently in contact with any pin.
    /// </summary>
    public bool IsInContact => _inContact;

    /// <summary>
    /// Gets the number of currently colliding objects.
    /// </summary>
    public int CollidingObjectCount => _collidingObjects.Count;

    // Private helpers

    private Vector2Int? ParsePinCoordinate(Collider collider)
    {
        string pinName = collider.gameObject.name;

        // Try parent if this GameObject doesn't have coordinates
        if (!pinName.Contains(',') && collider.transform.parent != null)
            pinName = collider.transform.parent.gameObject.name;

        if (!pinName.Contains(','))
            return null; // Not a pin

        var parts = pinName.Split(',');
        if (parts.Length < 2)
            return null;

        if (int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y))
            return new Vector2Int(x, y);

        return null;
    }

    private void ApplyTouchHighlight(Collider collider)
    {
        var rend = collider.GetComponentInChildren<MeshRenderer>();
        if (rend == null)
            return;

        if (!_originalColors.ContainsKey(collider.gameObject))
            _originalColors[collider.gameObject] = rend.material.color;

        rend.material.color = _touchHighlightColor;
    }

    private void RestoreTouchHighlight(Collider collider)
    {
        var rend = collider.GetComponentInChildren<MeshRenderer>();
        if (rend == null)
            return;

        if (_originalColors.TryGetValue(collider.gameObject, out var originalColor))
        {
            rend.material.color = originalColor;
            _originalColors.Remove(collider.gameObject);
        }
    }
}
