using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Delegate for transform handlers.
/// Takes input data and transform specification, returns transformed data.
/// </summary>
public delegate List<Dictionary<string, object>> TransformHandler(
    List<Dictionary<string, object>> data,
    JToken transformSpec,
    VegaTransformEngine engine);

/// <summary>
/// Executes Vega-Lite transform operations on data.
/// Supports: filter, aggregate, calculate, timeUnit, bin, window
/// Extensible via RegisterTransform() for custom transform types.
///
/// Example - Adding a custom "normalize" transform:
/// <code>
/// var engine = new VegaTransformEngine();
/// engine.RegisterTransform("normalize", (data, spec, eng) => {
///     string field = spec["field"]?.ToString();
///     string asField = spec["as"]?.ToString() ?? field + "_normalized";
///
///     var values = data.Select(r => Convert.ToDouble(r[field])).ToList();
///     double min = values.Min();
///     double max = values.Max();
///     double range = max - min;
///
///     return data.Select(row => {
///         var newRow = new Dictionary&lt;string, object&gt;(row);
///         double value = Convert.ToDouble(row[field]);
///         newRow[asField] = range > 0 ? (value - min) / range : 0.5;
///         return newRow;
///     }).ToList();
/// });
/// </code>
/// </summary>
public class VegaTransformEngine
{
    // Primary ordering field (typically X-axis) for preserving bounds during aggregation
    private string _primaryOrderingField;

    // Registry of transform handlers
    private readonly Dictionary<string, TransformHandler> _transformHandlers;

    /// <summary>
    /// Public accessor for primary ordering field (used by aggregate transforms).
    /// </summary>
    public string PrimaryOrderingField => _primaryOrderingField;

    public VegaTransformEngine()
    {
        _transformHandlers = new Dictionary<string, TransformHandler>();

        // Register built-in transforms (silently)
        RegisterTransform("filter", ApplyFilter, silent: true);
        RegisterTransform("aggregate", ApplyAggregate, silent: true);
        RegisterTransform("calculate", ApplyCalculate, silent: true);
        RegisterTransform("timeUnit", ApplyTimeUnit, silent: true);
        RegisterTransform("bin", ApplyBin, silent: true);
        RegisterTransform("window", ApplyWindow, silent: true);
    }

    /// <summary>
    /// Register a custom transform handler.
    /// </summary>
    /// <param name="transformType">Transform type name (e.g., "filter", "myCustomTransform")</param>
    /// <param name="handler">Handler function that processes the transform</param>
    /// <param name="silent">If true, don't log registration (used for built-in transforms)</param>
    public void RegisterTransform(string transformType, TransformHandler handler, bool silent = false)
    {
        _transformHandlers[transformType] = handler;
        if (!silent)
        {
            Debug.Log($"Registered custom transform handler: '{transformType}'");
        }
    }

    /// <summary>
    /// Unregister a transform handler (useful for testing or replacing built-in transforms).
    /// </summary>
    public void UnregisterTransform(string transformType)
    {
        _transformHandlers.Remove(transformType);
    }

    /// <summary>
    /// Apply a sequence of transforms to data.
    /// Transforms are applied in order as they appear in the spec.
    /// </summary>
    /// <param name="data">Input data</param>
    /// <param name="transforms">List of transform operations</param>
    /// <param name="primaryOrderingField">Field used for X-axis/ordering (e.g., "date", "country", "year").
    /// If provided, aggregation will preserve first/last values as {field}_start and {field}_end.</param>
    public List<Dictionary<string, object>> ApplyTransforms(
        List<Dictionary<string, object>> data,
        List<JToken> transforms,
        string primaryOrderingField = null)
    {
        if (transforms == null || transforms.Count == 0)
            return data;

        // Store primary ordering field for use in aggregate operations
        _primaryOrderingField = primaryOrderingField;

        var result = new List<Dictionary<string, object>>(data);

        foreach (var transform in transforms)
        {
            // Try to find a registered handler for this transform
            bool handled = false;

            foreach (var transformType in _transformHandlers.Keys)
            {
                if (transform[transformType] != null)
                {
                    var handler = _transformHandlers[transformType];
                    result = handler(result, transform, this);
                    handled = true;
                    break;
                }
            }

            if (!handled)
            {
                Debug.LogWarning($"Unsupported transform: {transform}");
            }
        }

        return result;
    }

    /// <summary>
    /// Filter transform: Keep only rows matching a predicate.
    /// Example: {"filter": "datum.year > 2010"}
    /// </summary>
    private List<Dictionary<string, object>> ApplyFilter(
        List<Dictionary<string, object>> data,
        JToken transformSpec,
        VegaTransformEngine engine)
    {
        JToken filterExpr = transformSpec["filter"];
        string expression = filterExpr.ToString();

        return data.Where(row => EvaluateFilter(row, expression)).ToList();
    }

    /// <summary>
    /// Aggregate transform: Group and summarize data.
    /// Example: {"aggregate": [{"op": "mean", "field": "price", "as": "avg_price"}], "groupby": ["symbol"]}
    /// </summary>
    private List<Dictionary<string, object>> ApplyAggregate(
        List<Dictionary<string, object>> data,
        JToken transformSpec,
        VegaTransformEngine engine)
    {
        var aggregates = transformSpec["aggregate"] as JArray;
        var groupbyFields = transformSpec["groupby"] as JArray;

        if (aggregates == null || aggregates.Count == 0)
        {
            Debug.LogWarning(" Aggregate transform missing 'aggregate' array");
            return data;
        }

        // If no groupby, aggregate entire dataset
        if (groupbyFields == null || groupbyFields.Count == 0)
        {
            return new List<Dictionary<string, object>> { AggregateGroup(data, aggregates) };
        }

        // Group by specified fields
        var groups = data.GroupBy(row =>
        {
            var key = new List<object>();
            foreach (var field in groupbyFields)
            {
                var fieldName = field.ToString();
                key.Add(row.ContainsKey(fieldName) ? row[fieldName] : null);
            }
            return string.Join("|", key);
        });

        // Aggregate each group
        var result = new List<Dictionary<string, object>>();
        foreach (var group in groups)
        {
            var aggregated = AggregateGroup(group.ToList(), aggregates);

            // Add groupby fields
            for (int i = 0; i < groupbyFields.Count; i++)
            {
                var fieldName = groupbyFields[i].ToString();
                aggregated[fieldName] = group.First()[fieldName];
            }

            result.Add(aggregated);
        }

        return result;
    }

    /// <summary>
    /// Aggregate a single group of rows.
    /// Automatically preserves bounds for the primary ordering field (if specified).
    /// </summary>
    private Dictionary<string, object> AggregateGroup(
        List<Dictionary<string, object>> group,
        JArray aggregates)
    {
        var result = new Dictionary<string, object>();

        foreach (var agg in aggregates)
        {
            string op = agg["op"]?.ToString();
            string field = agg["field"]?.ToString();
            string asField = agg["as"]?.ToString() ?? field;

            if (string.IsNullOrEmpty(op))
                continue;

            try
            {
                object value = op.ToLower() switch
                {
                    "count" => group.Count,
                    "valid" => group.Count(r => r.ContainsKey(field) && r[field] != null),
                    "missing" => group.Count(r => !r.ContainsKey(field) || r[field] == null),
                    "distinct" => group.Select(r => r.ContainsKey(field) ? r[field] : null).Distinct().Count(),
                    "sum" => group.Sum(r => GetNumeric(r, field)),
                    "mean" => group.Average(r => GetNumeric(r, field)),
                    "average" => group.Average(r => GetNumeric(r, field)),
                    "min" => group.Min(r => GetNumeric(r, field)),
                    "max" => group.Max(r => GetNumeric(r, field)),
                    "median" => CalculateMedian(group.Select(r => GetNumeric(r, field)).ToList()),
                    "q1" => CalculateQuantile(group.Select(r => GetNumeric(r, field)).ToList(), 0.25),
                    "q3" => CalculateQuantile(group.Select(r => GetNumeric(r, field)).ToList(), 0.75),
                    "stdev" => CalculateStdDev(group.Select(r => GetNumeric(r, field)).ToList()),
                    "variance" => CalculateVariance(group.Select(r => GetNumeric(r, field)).ToList()),
                    _ => throw new NotSupportedException($"Unsupported aggregate operation: {op}")
                };

                // Round floating-point results to 2 decimal places for readability
                if (value is double d)
                {
                    result[asField] = Math.Round(d, 2);
                }
                else
                {
                    result[asField] = value;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Aggregate {op} on field {field} failed: {ex.Message}");
                result[asField] = 0;
            }
        }

        // Preserve bounds for primary ordering field (X-axis)
        PreservePrimaryFieldBounds(group, result);

        return result;
    }

    /// <summary>
    /// Preserve first and last values of the primary ordering field (e.g., X-axis).
    /// Adds {field}_start and {field}_end to enable value-based mapping across aggregation levels.
    /// This is crucial for zoom-under-finger to work correctly when switching between layers.
    /// </summary>
    private void PreservePrimaryFieldBounds(
        List<Dictionary<string, object>> group,
        Dictionary<string, object> result)
    {
        if (string.IsNullOrEmpty(_primaryOrderingField) || group.Count == 0)
            return;

        var firstRow = group.First();
        var lastRow = group.Last();

        // Check if primary field exists in the data
        if (!firstRow.ContainsKey(_primaryOrderingField))
        {
            // Field might have been transformed (e.g., "date" -> "month" via timeUnit)
            // In this case, we can't preserve the original field, which is okay
            return;
        }

        string startKey = $"{_primaryOrderingField}_start";
        string endKey = $"{_primaryOrderingField}_end";

        // Preserve first and last values
        result[startKey] = firstRow[_primaryOrderingField];
        result[endKey] = lastRow.ContainsKey(_primaryOrderingField)
            ? lastRow[_primaryOrderingField]
            : firstRow[_primaryOrderingField];

        Debug.Log($"Preserved ordering field '{_primaryOrderingField}': [{result[startKey]}] to [{result[endKey]}]");
    }

    /// <summary>
    /// Calculate transform: Create new fields from expressions.
    /// Example: {"calculate": "datum.price * 1.1", "as": "price_with_tax"}
    /// </summary>
    private List<Dictionary<string, object>> ApplyCalculate(
        List<Dictionary<string, object>> data,
        JToken transformSpec,
        VegaTransformEngine engine)
    {
        string expression = transformSpec["calculate"]?.ToString();
        string asField = transformSpec["as"]?.ToString();

        if (string.IsNullOrEmpty(expression) || string.IsNullOrEmpty(asField))
            return data;

        var result = new List<Dictionary<string, object>>();

        foreach (var row in data)
        {
            var newRow = new Dictionary<string, object>(row);
            newRow[asField] = EvaluateExpression(row, expression);
            result.Add(newRow);
        }

        return result;
    }

    /// <summary>
    /// TimeUnit transform: Extract time units from temporal fields.
    /// Example: {"timeUnit": "year", "field": "date", "as": "year"}
    /// Supports any combination of time units (year, quarter, month, week, day, etc.)
    /// </summary>
    private List<Dictionary<string, object>> ApplyTimeUnit(
        List<Dictionary<string, object>> data,
        JToken transformSpec,
        VegaTransformEngine engine)
    {
        string timeUnit = transformSpec["timeUnit"]?.ToString();
        string field = transformSpec["field"]?.ToString();
        string asField = transformSpec["as"]?.ToString() ?? field;

        if (string.IsNullOrEmpty(timeUnit) || string.IsNullOrEmpty(field))
            return data;

        var result = new List<Dictionary<string, object>>();

        foreach (var row in data)
        {
            var newRow = new Dictionary<string, object>(row);

            if (row.ContainsKey(field))
            {
                object fieldValue = row[field];
                object timeUnitValue = ExtractTimeUnit(fieldValue, timeUnit);

                if (timeUnitValue != null)
                {
                    newRow[asField] = timeUnitValue;
                }
            }

            result.Add(newRow);
        }

        return result;
    }

    /// <summary>
    /// Generic time unit extractor - handles any combination of time units.
    /// Supports composite units like "yearquarter", "yearmonth", "yearweek", etc.
    /// </summary>
    private object ExtractTimeUnit(object value, string timeUnit)
    {
        // Try parsing as DateTime first
        System.DateTime? date = null;

        if (value is string dateStr && System.DateTime.TryParse(dateStr, out var parsedDate))
        {
            date = parsedDate;
        }
        else if (value is System.DateTime dt)
        {
            date = dt;
        }

        if (date.HasValue)
        {
            return ExtractTimeUnitFromDate(date.Value, timeUnit);
        }

        // Fallback: if it's a numeric value, try to interpret it
        // (e.g., 2019.25 for Q1 2019, or just 2019 for year)
        try
        {
            double numericValue = Convert.ToDouble(value);
            return numericValue;  // Return as-is for numeric timeUnit values
        }
        catch
        {
            return value;  // Return original value if we can't parse it
        }
    }

    /// <summary>
    /// Extract time unit components from a DateTime.
    /// Handles composite units by extracting each component and combining them.
    /// Examples:
    /// - "year" -> 2019
    /// - "quarter" -> 1, 2, 3, or 4
    /// - "yearquarter" -> 20191 (year * 10 + quarter)
    /// - "yearmonth" -> 201901 (year * 100 + month)
    /// - "yearweek" -> 201901 (year * 100 + week)
    /// </summary>
    private object ExtractTimeUnitFromDate(System.DateTime date, string timeUnit)
    {
        timeUnit = timeUnit.ToLower();

        // Handle composite units by detecting patterns
        // Check for "year" + something patterns
        if (timeUnit.StartsWith("year"))
        {
            string remainder = timeUnit.Substring(4);  // Remove "year" prefix

            if (string.IsNullOrEmpty(remainder))
            {
                return date.Year;
            }

            // Composite: year + another unit
            int year = date.Year;
            object subUnit = ExtractTimeUnitComponent(date, remainder);

            // Combine year with sub-unit
            return CombineTimeUnits(year, subUnit, remainder);
        }

        // Single component units
        return ExtractTimeUnitComponent(date, timeUnit);
    }

    /// <summary>
    /// Extract a single time unit component from a date.
    /// </summary>
    private object ExtractTimeUnitComponent(System.DateTime date, string unit)
    {
        switch (unit.ToLower())
        {
            case "year":
                return date.Year;

            case "quarter":
                return (date.Month - 1) / 3 + 1;  // 1-4

            case "month":
                return date.Month;  // 1-12

            case "week":
                var calendar = System.Globalization.CultureInfo.CurrentCulture.Calendar;
                return calendar.GetWeekOfYear(date,
                    System.Globalization.CalendarWeekRule.FirstDay,
                    System.DayOfWeek.Sunday);

            case "day":
            case "date":
                return date.Day;

            case "dayofyear":
                return date.DayOfYear;

            case "hours":
                return date.Hour;

            case "minutes":
                return date.Minute;

            case "seconds":
                return date.Second;

            case "milliseconds":
                return date.Millisecond;

            default:
                Debug.LogWarning($"Unknown time unit component: {unit}");
                return 0;
        }
    }

    /// <summary>
    /// Combine year with a sub-unit to create composite temporal values.
    /// Uses slash-separated format for readability.
    /// Examples:
    /// - year=2019, quarter=1 -> "2019/Q1"
    /// - year=2019, month=3 -> "2019/03"
    /// - year=2019, week=15 -> "2019/W15"
    /// </summary>
    private object CombineTimeUnits(int year, object subUnit, string subUnitName)
    {
        int subValue = Convert.ToInt32(subUnit);

        switch (subUnitName.ToLower())
        {
            case "quarter":
                // yearquarter: 2019 + Q1 = "2019/Q1"
                return $"{year}/Q{subValue}";

            case "month":
                // yearmonth: 2019 + March = "2019/03"
                return $"{year}/{subValue:D2}";

            case "week":
                // yearweek: 2019 + week 15 = "2019/W15"
                return $"{year}/W{subValue:D2}";

            case "day":
            case "date":
                // yearday: 2019 + day 150 = "2019/150"
                return $"{year}/{subValue:D3}";

            default:
                // Generic: slash-separated format
                return $"{year}/{subValue}";
        }
    }

    /// <summary>
    /// Bin transform: Group numeric values into bins.
    /// Example: {"bin": true, "field": "price", "as": "price_bin"}
    /// </summary>
    private List<Dictionary<string, object>> ApplyBin(
        List<Dictionary<string, object>> data,
        JToken transformSpec,
        VegaTransformEngine engine)
    {
        string field = transformSpec["field"]?.ToString();
        string asField = transformSpec["as"]?.ToString() ?? field + "_bin";

        var binConfig = transformSpec["bin"];
        int maxbins = 10;

        if (binConfig.Type == JTokenType.Object)
        {
            maxbins = binConfig["maxbins"]?.Value<int>() ?? 10;
        }

        if (string.IsNullOrEmpty(field))
            return data;

        // Calculate bin boundaries
        var values = data.Where(r => r.ContainsKey(field))
                        .Select(r => GetNumeric(r, field))
                        .ToList();

        double min = values.Min();
        double max = values.Max();
        double binWidth = (max - min) / maxbins;

        var result = new List<Dictionary<string, object>>();

        foreach (var row in data)
        {
            var newRow = new Dictionary<string, object>(row);

            if (row.ContainsKey(field))
            {
                double value = GetNumeric(row, field);
                double binStart = Math.Floor((value - min) / binWidth) * binWidth + min;
                newRow[asField] = binStart;
            }

            result.Add(newRow);
        }

        return result;
    }

    /// <summary>
    /// Window transform: Calculate rolling/cumulative values.
    /// Example: {"window": [{"op": "rank", "as": "rank"}], "sort": [{"field": "price"}]}
    /// </summary>
    private List<Dictionary<string, object>> ApplyWindow(
        List<Dictionary<string, object>> data,
        JToken transformSpec,
        VegaTransformEngine engine)
    {
        var windows = transformSpec["window"] as JArray;

        if (windows == null || windows.Count == 0)
            return data;

        var result = new List<Dictionary<string, object>>(data);

        foreach (var window in windows)
        {
            string op = window["op"]?.ToString();
            string asField = window["as"]?.ToString();

            if (string.IsNullOrEmpty(op) || string.IsNullOrEmpty(asField))
                continue;

            // rank and row_number: assign sequential numbers starting from 1
            if (op == "rank" || op == "row_number")
            {
                for (int i = 0; i < result.Count; i++)
                {
                    result[i][asField] = i + 1;
                }
            }
        }

        return result;
    }

    // ===== Helper Methods =====

    private bool EvaluateFilter(Dictionary<string, object> row, string expression)
    {
        // Filter expression evaluation is not yet implemented — all rows pass through.
        Debug.LogWarning($"[VegaTransform] Filter expression not evaluated: '{expression}' — all rows included.");
        return true;
    }

    private object EvaluateExpression(Dictionary<string, object> row, string expression)
    {
        // Generic expression evaluator that handles:
        // - Date/time functions: year(datum.field), quarter(datum.field), etc.
        // - Math functions: floor(datum.field), ceil(datum.field), etc.
        // - Compound expressions: year(datum.date) + '-Q' + quarter(datum.date)

        try
        {
            return EvaluateExpressionRecursive(row, expression.Trim());
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to evaluate expression '{expression}': {ex.Message}");
            return 0;
        }
    }

    private object EvaluateExpressionRecursive(Dictionary<string, object> row, string expr)
    {
        expr = expr.Trim();

        // Handle compound expressions with string concatenation: "A + '-Q' + B"
        if (expr.Contains("'") && expr.Contains("+"))
        {
            return EvaluateStringConcatenation(row, expr);
        }

        // Handle function calls: functionName(...)
        var functionMatch = new System.Text.RegularExpressions.Regex(@"^(\w+)\((.*)\)$").Match(expr);
        if (functionMatch.Success)
        {
            string functionName = functionMatch.Groups[1].Value;
            string argument = functionMatch.Groups[2].Value;

            return EvaluateFunction(row, functionName, argument);
        }

        // Handle datum.field references
        if (expr.StartsWith("datum."))
        {
            string fieldName = expr.Substring(6);
            if (row.ContainsKey(fieldName))
            {
                return row[fieldName];
            }
            return null;
        }

        // Handle numeric literals
        if (double.TryParse(expr, out double numValue))
        {
            return numValue;
        }

        // Handle string literals (quoted)
        if (expr.StartsWith("'") && expr.EndsWith("'"))
        {
            return expr.Substring(1, expr.Length - 2);
        }

        Debug.LogWarning($"Unsupported expression: {expr}");
        return 0;
    }

    private object EvaluateStringConcatenation(Dictionary<string, object> row, string expr)
    {
        // Split by + but preserve quoted strings
        var parts = new System.Collections.Generic.List<string>();
        bool inQuote = false;
        int partStart = 0;

        for (int i = 0; i < expr.Length; i++)
        {
            if (expr[i] == '\'')
            {
                inQuote = !inQuote;
            }
            else if (expr[i] == '+' && !inQuote)
            {
                parts.Add(expr.Substring(partStart, i - partStart).Trim());
                partStart = i + 1;
            }
        }
        parts.Add(expr.Substring(partStart).Trim());

        // Evaluate each part and concatenate
        var result = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            object value = EvaluateExpressionRecursive(row, part);
            result.Append(value?.ToString() ?? "");
        }

        return result.ToString();
    }

    private object EvaluateFunction(Dictionary<string, object> row, string functionName, string argument)
    {
        // Recursively evaluate the argument
        object argValue = EvaluateExpressionRecursive(row, argument);

        switch (functionName.ToLower())
        {
            // Math functions
            case "floor":
                return (int)Math.Floor(Convert.ToDouble(argValue));

            case "ceil":
            case "ceiling":
                return (int)Math.Ceiling(Convert.ToDouble(argValue));

            case "round":
                return (int)Math.Round(Convert.ToDouble(argValue));

            case "abs":
                return Math.Abs(Convert.ToDouble(argValue));

            case "sqrt":
                return Math.Sqrt(Convert.ToDouble(argValue));

            // Date/time functions - delegate to ExtractTimeUnit
            case "year":
            case "quarter":
            case "month":
            case "week":
            case "day":
            case "date":
            case "hours":
            case "minutes":
            case "seconds":
                return ExtractTimeUnitComponent(ParseDateTime(argValue), functionName);

            default:
                Debug.LogWarning($"Unknown function: {functionName}");
                return argValue;
        }
    }

    private System.DateTime ParseDateTime(object value)
    {
        if (value is System.DateTime dt)
            return dt;

        if (value is string dateStr && System.DateTime.TryParse(dateStr, out var parsed))
            return parsed;

        throw new InvalidOperationException($"Cannot parse '{value}' as DateTime");
    }


    private double GetNumeric(Dictionary<string, object> row, string field)
    {
        if (!row.ContainsKey(field) || row[field] == null)
            return 0;

        return Convert.ToDouble(row[field]);
    }

    private double CalculateMedian(List<double> values)
    {
        if (values.Count == 0) return 0;

        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;

        if (sorted.Count % 2 == 0)
            return (sorted[mid - 1] + sorted[mid]) / 2.0;
        else
            return sorted[mid];
    }

    private double CalculateQuantile(List<double> values, double quantile)
    {
        if (values.Count == 0) return 0;

        var sorted = values.OrderBy(v => v).ToList();
        double index = quantile * (sorted.Count - 1);
        int lower = (int)Math.Floor(index);
        int upper = (int)Math.Ceiling(index);

        if (lower == upper)
            return sorted[lower];

        double fraction = index - lower;
        return sorted[lower] * (1 - fraction) + sorted[upper] * fraction;
    }

    private double CalculateStdDev(List<double> values)
    {
        return Math.Sqrt(CalculateVariance(values));
    }

    private double CalculateVariance(List<double> values)
    {
        if (values.Count == 0) return 0;

        double mean = values.Average();
        return values.Average(v => Math.Pow(v - mean, 2));
    }

}
