using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static RTDGridConstants;

/// <summary>
/// Pure positioning math for RTD grid layout — no grid mutation, no Vega-spec knowledge.
/// </summary>
public static class RTDLayout
{
    public static int MapValueToPixel(float value, float domainMin, float domainMax, int pixelMin, int pixelMax)
    {
        if (Mathf.Approximately(domainMax, domainMin))
            return pixelMin;

        float ratio = (value - domainMin) / (domainMax - domainMin);
        float pixel = pixelMin + ratio * (pixelMax - pixelMin);
        return Mathf.RoundToInt(pixel);
    }

    public static int MapIndexToPixel(int index, int count, int pixelMin, int pixelMax)
    {
        if (count <= 1) return pixelMin;

        // Use integer-only Bresenham-style distribution for perfectly even spacing.
        int numGaps = count - 1;
        int availableWidth = pixelMax - pixelMin;
        return pixelMin + (index * availableWidth + numGaps / 2) / numGaps;
    }

    /// <summary>
    /// Calculate dynamic bar width based on number of bars in the viewport.
    /// </summary>
    public static int CalculateBarWidth(int barCount)
    {
        if (barCount <= 0) return 4;
        int dataWidth = 50; // cols 6-55
        int rawWidth = (dataWidth / barCount) - 1; // subtract 1 for gap between bars
        return Math.Max(1, rawWidth);
    }

    /// <summary>
    /// Nudge a data point's pixel position away from axes so symbols don't clip.
    /// </summary>
    public static (int col, int row) NudgeForSymbol(int col, int row, int yPixelMin)
    {
        if (row >= X_AXIS_ROW - 1) row = X_AXIS_ROW - 2;
        if (row <= yPixelMin) row = yPixelMin + 1;
        if (col <= Y_AXIS_COL + 1) col = Y_AXIS_COL + 2;
        return (col, row);
    }

    /// <summary>
    /// Determine whether an X-axis tick should be drawn at the given index,
    /// using evenly-spaced intervals when count exceeds maxTicks.
    /// </summary>
    public static bool ShouldDrawXTick(int index, int count, int maxTicks)
    {
        if (count <= maxTicks) return true;
        int tickInterval = Math.Max(2, (count + maxTicks - 2) / (maxTicks - 1));
        return (index == 0) || (index == count - 1) || (index % tickInterval == 0);
    }

    /// <summary>
    /// Generate "nice" tick values for a given range (similar to D3.js ticks algorithm).
    /// Returns evenly-spaced round numbers that span the data range.
    /// </summary>
    public static List<float> GenerateNiceTicks(float dataMin, float dataMax, int targetCount = 6)
    {
        if (dataMin >= dataMax)
            return new List<float> { dataMin };

        float range = dataMax - dataMin;
        float roughStep = range / (targetCount - 1);

        // Find the "nice" step size (1, 2, 5, 10, 20, 50, 100, etc.)
        float magnitude = Mathf.Pow(10, Mathf.Floor(Mathf.Log10(roughStep)));
        float normalizedStep = roughStep / magnitude;

        float niceStep;
        if (normalizedStep <= 1) niceStep = 1;
        else if (normalizedStep <= 2) niceStep = 2;
        else if (normalizedStep <= 5) niceStep = 5;
        else niceStep = 10;

        niceStep *= magnitude;

        // Generate ticks starting from a nice round number
        float niceMin = Mathf.Floor(dataMin / niceStep) * niceStep;
        float niceMax = Mathf.Ceil(dataMax / niceStep) * niceStep;

        List<float> ticks = new List<float>();
        for (float tick = niceMin; tick <= niceMax + 0.0001f; tick += niceStep)
        {
            ticks.Add(tick);
        }

        return ticks;
    }

    public static float GetNumericValue(object value)
    {
        if (value == null) return 0f;

        // Handle different numeric types
        if (value is float f) return f;
        if (value is double d) return (float)d;
        if (value is int i) return (float)i;
        if (value is long l) return (float)l;

        // Handle JSON tokens
        if (value is Newtonsoft.Json.Linq.JValue jval)
        {
            return jval.ToObject<float>();
        }

        // Try parsing string
        if (value is string s && float.TryParse(s, out float result))
        {
            return result;
        }

        // Fallback conversion
        try
        {
            return Convert.ToSingle(value);
        }
        catch
        {
            Debug.LogWarning($"Could not convert value '{value}' to float. Using 0f.");
            return 0f;
        }
    }

    public static (float min, float max) GetNumericDomain(VegaChannel encoding, List<System.Collections.Generic.Dictionary<string, object>> data, string field)
    {
        // Check scale domain first
        if (encoding.Scale != null && encoding.Scale.Domain != null)
        {
            return encoding.Scale.GetNumericDomain();
        }

        // Check axis values (only for numeric axes)
        if (encoding.Axis != null && encoding.Axis.Values != null && !encoding.IsCategorical())
        {
            var values = encoding.Axis.GetNumericValues();
            if (values != null && values.Length >= 2)
            {
                return (values[0], values[values.Length - 1]);
            }
        }

        // Calculate from data
        var numericValues = data.Select(d => GetNumericValue(d[field])).ToList();
        return (numericValues.Min(), numericValues.Max());
    }
}
