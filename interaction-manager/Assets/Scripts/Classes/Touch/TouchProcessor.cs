using UnityEngine;
using System.Collections.Generic;
using System.Linq;

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
    public List<float> probabilities { get; private set; }
    public HashSet<Vector2Int> nodePositions { get; private set; }

    void Start()
    {
        _sigma = CalculateSigma(_fingerWidthMm, 1.5f, 1.0f);
        Debug.Log($"SpatialTouchProcessor: fingerWidth={_fingerWidthMm}mm, sigma={_sigma:F3}");
    }

    /// <summary>
    /// Processes a touch event using spatial inference.
    /// </summary>
    /// <param name="coords">ALL touched pin coordinates (raised + lowered) - used for spatial footprint calculation</param>
    /// <param name="matchingNodes">Only raised pins with actual node data - the candidate targets</param>
    public void ProcessTouch(HashSet<Vector2Int> coords, List<NodeComponent> matchingNodes)
    {
        if (coords == null || coords.Count == 0 || matchingNodes == null || matchingNodes.Count == 0)
            return;

        // Use ALL touched coords (including lowered pins) to find geometric center of touch
        // This provides better spatial accuracy for near-misses and between-pin touches
        var (calculatedCenter, calculatedClosestPoint) = FindClosestPoint(coords);
        center = calculatedCenter;
        closestPoint = calculatedClosestPoint;

        // Extract positions as a list (single enumeration — order preserved for probability alignment)
        var pinList = matchingNodes.Where(n => n.xy != null && n.xy.Length >= 2)
                                   .Select(n => new Vector2Int(n.xy[0], n.xy[1]))
                                   .ToList();
        nodePositions = new HashSet<Vector2Int>(pinList);

        // Compute probability: "Which raised pin is closest to the touch centroid?"
        probabilities = ComputeProbabilityDistribution(pinList, closestPoint, _sigma);

        var (calculatedMostLikelyPin, calculatedMostLikelyProbability) = IdentifyMostLikelyPin(pinList, probabilities);
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
        var result = new List<Vector2Int>();
        var positionsList = nodePositions.ToList();
        
        for (int i = 0; i < positionsList.Count && i < probabilities.Count; i++)
        {
            if (probabilities[i] >= threshold)
            {
                result.Add(positionsList[i]);
            }
        }
        return result;
    }
}
