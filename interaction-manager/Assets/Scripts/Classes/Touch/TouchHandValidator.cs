using UnityEngine;
using Leap;

/// <summary>
/// Validates hand chirality (left vs right) with hysteresis to prevent flickering
/// when the tracking system temporarily mislabels hands.
/// </summary>
public class TouchHandValidator
{
    // Configuration
    private readonly bool _isLeftComponent;
    private readonly float _acceptConfidence;
    private readonly float _releaseConfidence;
    private readonly int _framesToSwitch;

    // State
    private bool _lockedIsLeft = false;
    private int _lockedHandId = -1;
    private bool _hasLock = false;
    private int _wrongChiralityLowConfFrames = 0;

    public TouchHandValidator(
        bool isLeftComponent,
        float acceptConfidence = 0.50f,
        float releaseConfidence = 0.35f,
        int framesToSwitch = 12)
    {
        _isLeftComponent = isLeftComponent;
        _acceptConfidence = acceptConfidence;
        _releaseConfidence = releaseConfidence;
        _framesToSwitch = framesToSwitch;
    }

    /// <summary>
    /// Validates if the given hand matches the expected chirality (left/right).
    /// Uses a locking mechanism with hysteresis to prevent flickering on brief mislabels.
    /// </summary>
    /// <param name="hand">The Leap hand to validate</param>
    /// <returns>True if the hand is valid for this component</returns>
    public bool ValidateChirality(Hand hand)
    {
        bool handIsLeft = hand.IsLeft;
        float conf = hand.Confidence;

        // If we already locked to this hand id, try to keep it stable
        if (_hasLock && _lockedHandId == hand.Id)
        {
            if (handIsLeft == _lockedIsLeft)
            {
                _wrongChiralityLowConfFrames = 0;
                return conf >= _acceptConfidence;
            }

            // Disagrees with our lock; only allow flip after several LOW-confidence frames
            if (conf < _releaseConfidence)
            {
                _wrongChiralityLowConfFrames++;
                if (_wrongChiralityLowConfFrames >= _framesToSwitch)
                {
                    _lockedIsLeft = handIsLeft;
                    _wrongChiralityLowConfFrames = 0;
                    return conf >= _acceptConfidence;
                }
            }
            else
            {
                _wrongChiralityLowConfFrames = 0;
            }
            return false; // hold the lock for now
        }

        // No lock yet (new id or we lost the old one): require correct side + confidence
        bool expected = (_isLeftComponent == handIsLeft);
        if (expected && conf >= _acceptConfidence)
        {
            _lockedHandId = hand.Id;
            _lockedIsLeft = handIsLeft;
            _hasLock = true;
            _wrongChiralityLowConfFrames = 0;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Selects the best hand from a frame based on chirality and confidence.
    /// </summary>
    /// <param name="frame">The Leap frame containing hands</param>
    /// <returns>The best matching hand, or null if no valid hand found</returns>
    public Hand SelectBestHand(Frame frame)
    {
        Hand best = null;
        float bestConf = -1f;

        foreach (var h in frame.Hands)
        {
            if (!ValidateChirality(h)) continue;
            if (h.Confidence > bestConf)
            {
                best = h;
                bestConf = h.Confidence;
            }
        }

        return best;
    }

    /// <summary>
    /// Resets the validator state (clears hand lock).
    /// </summary>
    public void Reset()
    {
        _lockedHandId = -1;
        _hasLock = false;
        _wrongChiralityLowConfFrames = 0;
    }

    // Public getters for debugging
    public bool HasLock => _hasLock;
    public int LockedHandId => _lockedHandId;
    public bool LockedIsLeft => _lockedIsLeft;
}
