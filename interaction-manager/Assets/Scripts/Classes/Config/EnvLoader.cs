using System.Collections.Generic;
using System.IO;
using UnityEngine;

public static class EnvLoader
{
    private static Dictionary<string, string> _values;

    public static string Get(string key, string fallback = "")
    {
        if (_values == null)
            Load();
        return _values.TryGetValue(key, out string value) ? value : fallback;
    }

    private static void Load()
    {
        _values = new Dictionary<string, string>();
        string envPath = Path.Combine(Application.dataPath, "..", "..", ".env");

        if (!File.Exists(envPath))
        {
            Debug.LogWarning($".env file not found at {envPath}");
            return;
        }

        foreach (string line in File.ReadAllLines(envPath))
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                continue;

            int eqIndex = trimmed.IndexOf('=');
            if (eqIndex < 0)
                continue;

            string k = trimmed.Substring(0, eqIndex).Trim();
            string v = trimmed.Substring(eqIndex + 1).Trim();
            _values[k] = v;
        }
    }
}
