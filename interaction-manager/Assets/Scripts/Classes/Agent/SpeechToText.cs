using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class SpeechToText : MonoBehaviour, InterfaceSpeechToText
{

    // interface classes
    [SerializeField] private MonoBehaviour agentWakeWordService;
    private InterfaceAgentWakeWord _agentWakeWord;

    private string pythonPath;
    private string scriptPath;

    private bool isProcessing = false;
    private System.Action<string> onCompleteCallback;
    private Thread speechThread = null;
    private Process process = null;

    public bool skip = false;

    void Awake()
    {
        _agentWakeWord = agentWakeWordService as InterfaceAgentWakeWord ?? throw new InvalidOperationException("agentWakeWordService not assigned!");
    }

    void Start()
    {
        pythonPath = EnvLoader.Get("PYTHON_PATH", "python3");
        scriptPath = Path.Combine(Application.dataPath, "StreamingAssets", "Tools", "google_cloud_speechtotext_v1.py");

        UnityEngine.Debug.Log("Speech-to-Text System Ready");

        if (UnityMainThreadDispatcher.Instance() == null)
        {
            UnityEngine.Debug.LogWarning("UnityMainThreadDispatcher is missing! Creating one...");
            GameObject dispatcherObject = new GameObject("MainThreadDispatcher");
            dispatcherObject.AddComponent<UnityMainThreadDispatcher>();
            DontDestroyOnLoad(dispatcherObject);
        }
    }

    public void StartSpeechRecognition(System.Action<string> callback, bool quietMode = true, string touchData = "", System.Action onComplete = null, bool fromWakeWord = false)
    {
        // interrupt any existing recognition
        if (isProcessing)
        {
            UnityEngine.Debug.LogWarning("STT busy. Interrupting previous request");
            StopCurrentRecognition();
        }

        if (callback == null)
        {
            UnityEngine.Debug.LogError("ERROR: callback is null!");
            return;
        }

        isProcessing = true;
        onCompleteCallback = callback;

        // Default onComplete callback if not provided
        onComplete ??= () => UnityEngine.Debug.Log("Speech-to-Text completed.");

        speechThread = new Thread(() =>
        {
            if (string.IsNullOrEmpty(pythonPath) || string.IsNullOrEmpty(scriptPath))
            {
                UnityEngine.Debug.LogError("Error: pythonPath or scriptPath is not set!");
                CleanupRecognition("cancelled transcript");
                return;
            }

            string result = RunPythonScript(quietMode, touchData);
        
            // Enqueue callbacks on main thread
            var dispatcher = UnityMainThreadDispatcher.Instance();
            if (dispatcher != null)
            {
                dispatcher.Enqueue(() =>
                {
                    onCompleteCallback?.Invoke(result);
                    _agentWakeWord?.SetWakeWordStatus(false);
                    onComplete?.Invoke();
                });
            }
            else
            {
                UnityEngine.Debug.LogError("Error: UnityMainThreadDispatcher is null!");
            }

            isProcessing = false;
            speechThread = null;
        });

        speechThread.Start();
    }

    private string RunPythonScript(bool quietMode, string touchData)
    {
        UnityEngine.Debug.Log("Running Speech-to-Text Python script...");

        string arguments = quietMode ? $"\"{scriptPath}\" --quiet" : $"\"{scriptPath}\"";

        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        try
        {
            process = new Process { StartInfo = psi };
            process.Start();

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();

            process.WaitForExit();

            // Dispose process immediately after use
            process.Dispose();
            process = null;

            // Handle errors...
            if (!string.IsNullOrEmpty(error))
            {
                UnityEngine.Debug.LogWarning($"Python Error: {error}");
                return "cancelled transcript";
            }

            if (string.IsNullOrEmpty(output))
            {
                UnityEngine.Debug.LogWarning("Python script returned empty output.");
                return "cancelled transcript";
            }

            return output;
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError($"Exception running Python script: {ex.Message}");
            
            // Clean up process if it exists
            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                    process.Dispose();
                }
                catch { }
                process = null;
            }
            
            return "cancelled transcript";
        }
    }
    
    public void StopCurrentRecognition()
    {
        // Stop Python process
        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                {
                    UnityEngine.Debug.Log($"Killing STT Python process (PID {process.Id})");
                    process.Kill();
                    process.WaitForExit();
                }
                process.Dispose();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"Error stopping STT process: {ex.Message}");
            }
            finally
            {
                process = null;
            }
        }

        // Stop microphone
        Microphone.End(null);

        // Abort thread if still running (shouldn't happen often)
        if (speechThread != null && speechThread.IsAlive)
        {
            UnityEngine.Debug.LogWarning("STT thread still running during interrupt");
        }

        isProcessing = false;
        speechThread = null;
    }
    
    private void CleanupRecognition(string resultText)
    {
        isProcessing = false;
        speechThread = null;
        
        var dispatcher = UnityMainThreadDispatcher.Instance();
        if (dispatcher != null && onCompleteCallback != null)
        {
            dispatcher.Enqueue(() =>
            {
                onCompleteCallback.Invoke(resultText);
                _agentWakeWord?.SetWakeWordStatus(false);
            });
        }
    }

    public void StopPythonScript()
    {
        StopCurrentRecognition();
    }

    public void StopSpeechRecognition()
    {
        UnityEngine.Debug.LogWarning("Speech recognition interrupted");
        StopCurrentRecognition();
        CleanupRecognition("cancelled transcript");
    }

    public void CancelSpeechRecognition()
    {
        if (!isProcessing)
        {
            UnityEngine.Debug.LogWarning("CancelRecognition: no active transcription to cancel.");
            return;
        }

        UnityEngine.Debug.Log("Cancelling STT (soft)—leaving mic live.");
        StopCurrentRecognition();
        CleanupRecognition("cancelled transcript");
    }
    public bool GetSkipStatus()
    {
        return skip;
    }

    public void SetSkipStatus(bool status)
    {
        skip = status;
    }
}
