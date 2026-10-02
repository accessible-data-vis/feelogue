using UnityEngine;
using System.Collections.Generic;
using System.Linq;

/// <summary>Turns a touch's pin footprint into a most likely pin and per-node probabilities.</summary>
public class TouchProcessor : MonoBehaviour
{
    [SerializeField, Range(0.1f, 10f)]
    private float _sigma = 1.2f;
    [SerializeField]
    private float _fingerWidthMm = 18f;

    public Vector2Int center { get; private set; }
    public Vector2Int closestPoint { get; private set; }
    public Vector2Int interpretedTapPoint { get; private set; }
    public Vector2 mostLikelyPin { get; private set; }
    public float mostLikelyProbability { get; private set; }
    // probabilities[i] belongs to matchingNodes[i] as passed to ProcessTouch; a node
    // with no pin position gets 0. The spoken label and the agent payload both index
    // it by node position.
    public List<float> probabilities { get; private set; }
    public HashSet<Vector2Int> nodePositions { get; private set; }
    // Pin per node, duplicates kept. nodePositions is a deduped set, so it can't be
    // zipped with the probabilities.
    private List<Vector2Int> _pinByNode;
    private List<float> _probByPin;

    void Start()
    {
        _sigma = CalculateSigma(_fingerWidthMm, 1.5f, 1.0f);
        AppLog.Detail(LogArea.Touch, $"SpatialTouchProcessor: fingerWidth={_fingerWidthMm}mm, sigma={_sigma:F3}");
    }

    /// <summary>
    /// Processes a touch event using spatial inference.
    /// </summary>
    /// <param name="coords">All touched pin coordinates (raised + lowered) - used for spatial footprint calculation</param>
    /// <param name="matchingNodes">Only raised pins with actual node data - the candidate targets</param>
    public void ProcessTouch(HashSet<Vector2Int> coords, List<NodeComponent> matchingNodes)
    {
        if (coords == null || coords.Count == 0 || matchingNodes == null || matchingNodes.Count == 0)
            return;

        // Use all touched coords (including lowered pins) to find geometric center of touch
        // This provides better spatial accuracy for near-misses and between-pin touches
        var (calculatedCenter, calculatedClosestPoint) = FindClosestPoint(coords);
        center = calculatedCenter;
        closestPoint = calculatedClosestPoint;

        // Pins of the nodes that have a position, with the input slot each came from,
        // so the result can be mapped back onto every node.
        var pinList = new List<Vector2Int>();
        var sourceIndex = new List<int>();
        for (int i = 0; i < matchingNodes.Count; i++)
        {
            var n = matchingNodes[i];
            if (n.xy != null && n.xy.Length >= 2)
            {
                // A bar is scored at its pin nearest the touch, not at its corner.
                var pin = new Vector2Int(n.xy[0], n.xy[1]);
                if (n.barCoordinates != null && n.barCoordinates.Count > 1)
                    pin = n.barCoordinates
                        .Select(c => new Vector2Int(c.x, c.y))
                        .OrderBy(p => (p - closestPoint).sqrMagnitude)
                        .First();
                pinList.Add(pin);
                sourceIndex.Add(i);
            }
        }
        if (pinList.Count == 0)
        {
            // Shouldn't happen, but never keep the previous touch's probabilities.
            probabilities = new List<float>(new float[matchingNodes.Count]);
            nodePositions = new HashSet<Vector2Int>();
            _pinByNode = null;
            _probByPin = null;
            return;
        }
        nodePositions = new HashSet<Vector2Int>(pinList);

        // Compute probability: "Which raised pin is closest to the touch centroid?"
        _probByPin = ComputeProbabilityDistribution(pinList, closestPoint, _sigma);
        _pinByNode = pinList;

        // Re-expand onto the caller's node list (0 for nodes with no pin)
        var full = new List<float>(new float[matchingNodes.Count]);
        for (int k = 0; k < sourceIndex.Count; k++)
            full[sourceIndex[k]] = _probByPin[k];
        probabilities = full;

        var (calculatedMostLikelyPin, calculatedMostLikelyProbability) = IdentifyMostLikelyPin(pinList, _probByPin);
        mostLikelyPin = calculatedMostLikelyPin;
        mostLikelyProbability = calculatedMostLikelyProbability;
        interpretedTapPoint = new Vector2Int(Mathf.RoundToInt(mostLikelyPin.x), Mathf.RoundToInt(mostLikelyPin.y));
    }

    private (Vector2Int, Vector2Int) FindClosestPoint(HashSet<Vector2Int> points)
    {
        Vector2Int center = CalculateCenter(points);
        Vector2Int closest = points.OrderBy(p => CalculateDistance(p, center)).First();
        return (center, closest);
    }

    private float CalculateDistance(Vector2Int p1, Vector2Int p2)
    {
        float dx = p1.x - p2.x;
        float dy = p1.y - p2.y;
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    private Vector2Int CalculateCenter(HashSet<Vector2Int> points)
    {
        if (points == null || points.Count == 0)
            return Vector2Int.zero;

        int sumX = points.Sum(p => p.x);
        int sumY = points.Sum(p => p.y);
        return new Vector2Int(sumX / points.Count, sumY / points.Count);
    }

    private List<float> ComputeProbabilityDistribution(IList<Vector2Int> pins, Vector2Int point, float sigma)
    {
        List<float> distances = pins.Select(p => Vector2Int.Distance(p, point)).ToList();
        float normalizationFactor = Mathf.Sqrt(2 * Mathf.PI) * sigma;
        List<float> unnormalized = distances.Select(d => (1 / normalizationFactor) * Mathf.Exp(-Mathf.Pow(d, 2) / (2 * sigma * sigma))).ToList();
        float total = unnormalized.Sum();

        if (total <= 0f)
            return pins.Select(_ => 1f / pins.Count).ToList();

        return unnormalized.Select(p => p / total).ToList();
    }

    private (Vector2, float) IdentifyMostLikelyPin(List<Vector2Int> pinList, List<float> probs)
    {
        int index = probs.IndexOf(probs.Max());
        return (pinList[index], probs[index]);
    }

    public static float CalculateSigma(float fingerWidthMm, float pinWidthMm = 1.5f, float pinGapMm = 1.0f)
    {
        float pitchMm = pinWidthMm + pinGapMm;          // 1) Compute centre-to-centre pitch
        float sigmaPhys = fingerWidthMm / 2.355f;       // 2) Convert FWHM to σ in mm
        return sigmaPhys / pitchMm;                     // 3) Convert physical σ to grid-units:
    }

    public List<Vector2Int> GetHighConfidencePositions(float threshold = 0.2f)
    {
        // Use the per-node pins, not the deduped nodePositions set, since nodes can
        // share a pin. A pin qualifies when any node on it clears the threshold.
        var result = new HashSet<Vector2Int>();
        if (_pinByNode == null || _probByPin == null)
            return new List<Vector2Int>();
        for (int i = 0; i < _pinByNode.Count && i < _probByPin.Count; i++)
        {
            if (_probByPin[i] >= threshold)
                result.Add(_pinByNode[i]);
        }
        return result.ToList();
    }
}
