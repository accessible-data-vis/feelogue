using System;
using UnityEngine;
using System.IO;


public class GlobalLogger : MonoBehaviour
{
    // interface classes
    [SerializeField] private MonoBehaviour buttonGUIService;
    private InterfaceButtonGUI _buttonGUI;

    private string logFilePath;

    void Awake()
    {
        _buttonGUI = buttonGUIService as InterfaceButtonGUI ?? throw new InvalidOperationException("buttonGUIService not assigned!");

        CreateNewLogFile();
        Application.logMessageReceived += HandleLog;
        DontDestroyOnLoad(gameObject);
    }

    public void CreateNewLogFile()
    {
        string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string namePart = string.IsNullOrWhiteSpace(_buttonGUI.GetNameLog()) ? "Anonymous" : _buttonGUI.GetNameLog();
        logFilePath = Path.Combine(Application.persistentDataPath, $"UnityLog_{namePart}_{timestamp}.txt");

        File.WriteAllText(logFilePath, $"--- New Session: {System.DateTime.Now} ---\n");
        Debug.Log(" New log file created at: " + logFilePath);
    }

    void HandleLog(string logString, string stackTrace, LogType type)
    {
        string logEntry = $"{System.DateTime.Now:yyyy-MM-dd HH:mm:ss} [{type}] {logString}\n";
        if (type == LogType.Warning || type == LogType.Error || type == LogType.Exception)
            logEntry += $"{stackTrace}\n";

        File.AppendAllText(logFilePath, logEntry);
    }

    void OnDestroy()
    {
        Application.logMessageReceived -= HandleLog;
    }
}
