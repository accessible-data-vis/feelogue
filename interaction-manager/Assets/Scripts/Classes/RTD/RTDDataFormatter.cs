using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Formats chart node data for Text-to-Speech, Braille display, and visual labels.
/// </summary>
public class RTDDataFormatter
{
    // ── Static label formatter ────────────────────────────────────────────────

    /// <summary>
    /// Format a node values dictionary for visual label display.
    /// Filters internal fields (_start/_end/_rtd_index) and handles temporal,
    /// date, and numeric values with appropriate precision.
    /// </summary>
    public static string FormatNodeValues(Dictionary<string, object> values)
    {
        if (values == null || values.Count == 0)
            return "";

        var parts = new List<string>();
        foreach (var kvp in values)
        {
            if (!IsDisplayField(kvp.Key)) continue;
            string formatted = FormatDisplayValue(kvp.Value);
            if (!string.IsNullOrEmpty(formatted))
                parts.Add(formatted);
        }
        return string.Join("\n", parts);
    }

    private static string FormatDisplayValue(object value)
    {
        if (value == null) return null;

        if (value is string strValue)
        {
            // Pre-formatted timeUnit values (e.g. "2025/03", "2025/Q1", "2025/W15"): display as-is
            if (strValue.Contains("/") &&
                (strValue.Contains("Q") || strValue.Contains("W") ||
                 System.Text.RegularExpressions.Regex.IsMatch(strValue, @"^\d{4}/\d+")))
                return strValue;

            // Date strings: infer temporal granularity
            if (DateTime.TryParse(strValue, out var date))
            {
                if (date.Month == 1 && date.Day == 1)
                    return date.ToString("yyyy");
                if (date.Day == 1 && (date.Month == 1 || date.Month == 4 || date.Month == 7 || date.Month == 10))
                    return $"{date.Year}/Q{(date.Month - 1) / 3 + 1}";
                if (date.Day == 1)
                    return $"{date.Year}/{date.Month:D2}";
                return date.ToString("yyyy-MM-dd");
            }

            return strValue;
        }

        if (value is float || value is double)
            return Convert.ToDouble(value).ToString("F2");
        if (value is int || value is long)
            return value.ToString();

        return value.ToString();
    }

    // ── Instance TTS/Braille formatter ───────────────────────────────────────

    // How each field's numbers are spoken, from its axis title (set per chart)
    private Dictionary<string, Func<string, string>> _unitByField = new Dictionary<string, Func<string, string>>();

    /// <summary>
    /// Speak the chart's x and y numbers with their units, as the agent does: "$190",
    /// "4.35%", "395 kW", "120 thousand dollars".
    /// </summary>
    public void SetAxisUnits(string xField, string xTitle, string yField, string yTitle)
    {
        _unitByField = new Dictionary<string, Func<string, string>>();
        foreach (var (field, title) in new[] { (xField, xTitle), (yField, yTitle) })
        {
            var unit = UnitFormatter(title, field);
            if (!string.IsNullOrEmpty(field) && unit != null)
                _unitByField[field] = unit;
        }
    }

    /// <summary>The unit a field's numbers carry, from its axis title or name, or null.</summary>
    private static Func<string, string> UnitFormatter(string title, string field)
    {
        string text = $"{title} {field}";
        var m = Regex.Match(title ?? "", @"\(([^)]*)\)");
        if (!m.Success) m = Regex.Match(field ?? "", @"\(([^)]*)\)");
        string unit = m.Success ? m.Groups[1].Value.Trim() : null;

        var scaled = Regex.Match(unit ?? "", @"^\$\s*([KMB])$", RegexOptions.IgnoreCase);   // "$K": thousands of dollars
        if (scaled.Success)
        {
            string word = scaled.Groups[1].Value.ToUpperInvariant() == "K" ? "thousand"
                        : scaled.Groups[1].Value.ToUpperInvariant() == "M" ? "million" : "billion";
            return n => $"{n} {word} dollars";
        }
        if (text.Contains("$") || Regex.IsMatch(text, @"\b(AUD|USD|dollars?)\b", RegexOptions.IgnoreCase))
            return n => $"${n}";
        if (unit == "%")
            return n => $"{n}%";
        if (!string.IsNullOrEmpty(unit))
            return n => $"{n} {unit}";
        return null;
    }

    /// <summary>
    /// Format node values for TTS output, filtering by probability threshold.
    /// </summary>
    public string FormatValuesForTTS(List<NodeComponent> nodes, List<float> probabilities)
    {
        if (nodes == null || nodes.Count == 0)
            return "";

        var messages = new List<string>();

        for (int i = 0; i < nodes.Count; i++)
        {
            float prob = (probabilities != null && i < probabilities.Count)
                ? probabilities[i]
                : 1.0f;

            if (prob < RTDConstants.PROBABILITY_THRESHOLD)
                continue;

            string label = FormatNodeValues(nodes[i]);
            if (!string.IsNullOrEmpty(label))
                messages.Add(label);
        }

        return string.Join("; ", messages);
    }

    /// <summary>
    /// Generate a descriptive label for a single data point.
    /// </summary>
    public string GenerateDataPointLabel(NodeComponent node)
    {
        return FormatNodeValues(node);
    }

    /// <summary>
    /// Format a node's display values, filtering out internal fields.
    /// </summary>
    private string FormatNodeValues(NodeComponent node)
    {
        if (node.values == null || node.values.Count == 0)
            return "";

        var parts = node.values
            .Where(kvp => IsDisplayField(kvp.Key))
            .Select(kvp => kvp.Value is string || !_unitByField.TryGetValue(kvp.Key, out var unit)
                ? FormatValue(kvp.Value)
                : unit(FormatValue(kvp.Value)))
            .ToList();

        return string.Join(", ", parts);
    }

    /// <summary>
    /// True for fields shown to the user; false for internal ones (_start, _end,
    /// _rtd_index and the row id).
    /// </summary>
    public static bool IsDisplayField(string key)
    {
        if (key == VegaChartLoader.RowIdField)
            return false;
        if (key.EndsWith("_start") || key.EndsWith("_end"))
            return false;
        if (key.EndsWith("_rtd_index"))
            return false;
        return true;
    }

    /// <summary>
    /// Format a value with appropriate precision.
    /// Strings are returned as-is, whole numbers display without decimals, decimals show up to 2 places.
    /// </summary>
    private string FormatValue(object value)
    {
        if (value is string strValue)
            return strValue;

        try
        {
            double val = Convert.ToDouble(value);
            return (val % 1.0 == 0.0) ? val.ToString("F0") : val.ToString("0.##");
        }
        catch
        {
            return value?.ToString() ?? "";
        }
    }
}
