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

    // Script in StreamingAssets/Tools: openai_transcribe.py or google_cloud_speechtotext_v1.py
    [SerializeField] private string sttScript = "openai_transcribe.py";
    private string pythonPath;
    private string scriptPath;

    private bool isProcessing = false;
    private System.Action<string> onCompleteCallback;
    private Thread speechThread = null;
    private Process process = null;

    // Given to the callback when nobody spoke or the script failed. "cancelled
    // transcript" is different: listening was stopped on purpose, and nothing is said.
    private const string NO_TRANSCRIPT = "";

    // Bumped on every start and stop, so a cancelled or superseded run drops its
    // result instead of reporting "nothing heard".
    private int _sttGeneration = 0;

    // Push to talk: the key's release ends the turn, sent to the script as one line on
    // stdin. The release can come before the process exists, so it is remembered and
    // sent when the process starts.
    private readonly object _pttLock = new object();
    private Process _pttProcess = null;
    private bool _pttReleased = false;
    private bool _pttRun = false;   // the current run is push to talk

    public bool skip = false;

    void Awake()
    {
        _agentWakeWord = agentWakeWordService as InterfaceAgentWakeWord ?? throw new InvalidOperationException("agentWakeWordService not assigned!");
    }

    void Start()
    {
        pythonPath = EnvLoader.Get("PYTHON_PATH", "python3");
        scriptPath = Path.Combine(Application.dataPath, "StreamingAssets", "Tools", sttScript);

        AppLog.Detail(LogArea.Speech, "Speech-to-Text System Ready");

        if (UnityMainThreadDispatcher.Instance() == null)
        {
            UnityEngine.Debug.LogWarning("UnityMainThreadDispatcher is missing! Creating one...");
            GameObject dispatcherObject = new GameObject("MainThreadDispatcher");
            dispatcherObject.AddComponent<UnityMainThreadDispatcher>();
            DontDestroyOnLoad(dispatcherObject);
        }
    }

    public void StartSpeechRecognition(System.Action<string> callback, bool quietMode = true, string touchData = "", System.Action onComplete = null, bool fromWakeWord = false, bool pushToTalk = false)
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
        int generation = ++_sttGeneration;
        lock (_pttLock)
        {
            _pttProcess = null;
            _pttReleased = false;
            _pttRun = pushToTalk;
        }

        // Default onComplete callback if not provided
        onComplete ??= () => AppLog.Detail(LogArea.Speech, "Speech-to-Text completed.");

        speechThread = new Thread(() =>
        {
            if (string.IsNullOrEmpty(pythonPath) || string.IsNullOrEmpty(scriptPath))
            {
                UnityEngine.Debug.LogError("Error: pythonPath or scriptPath is not set!");
                CleanupRecognition(NO_TRANSCRIPT);
                return;
            }

            string result = RunPythonScript(quietMode, touchData, pushToTalk, generation);

            // Enqueue callbacks on main thread
            var dispatcher = UnityMainThreadDispatcher.Instance();
            if (dispatcher != null)
            {
                dispatcher.Enqueue(() =>
                {
                    // A cancel has already reported, or a newer run owns the callback.
                    if (generation == _sttGeneration)
                    {
                        isProcessing = false;
                        speechThread = null;
                        callback.Invoke(result);
                        _agentWakeWord?.SetWakeWordStatus(false);
                    }
                    else
                    {
                        AppLog.Detail(LogArea.Speech, "Stale recognition result discarded");
                    }
                    onComplete?.Invoke();
                });
            }
            else
            {
                UnityEngine.Debug.LogError("Error: UnityMainThreadDispatcher is null!");
                isProcessing = false;
                speechThread = null;
            }
        });

        speechThread.Start();
    }

    private string RunPythonScript(bool quietMode, string touchData, bool pushToTalk, int generation)
    {
        AppLog.Detail(LogArea.Speech, "Running Speech-to-Text Python script...");

        string arguments = quietMode ? $"\"{scriptPath}\" --quiet" : $"\"{scriptPath}\"";
        if (pushToTalk) arguments += " --push-to-talk";

        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = pushToTalk,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        // This run's own handle: the shared field is only there so a stop can kill it,
        // and reading the field here would let an old run dispose of the next one.
        Process proc = null;
        try
        {
            proc = new Process { StartInfo = psi };
            process = proc;
            // stderr is read asynchronously: reading both pipes in turn can deadlock
            // when the child fills stderr.
            var stderr = new System.Text.StringBuilder();
            proc.ErrorDataReceived += (s, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            proc.Start();
            proc.BeginErrorReadLine();
            if (pushToTalk)
            {
                lock (_pttLock)
                {
                    if (generation == _sttGeneration)
                    {
                        _pttProcess = proc;
                        if (_pttReleased) SendRelease(proc);   // released before the process existed
                    }
                }
            }

            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            int exitCode = proc.ExitCode;

            lock (_pttLock) { if (_pttProcess == proc) _pttProcess = null; }
            proc.Dispose();
            if (process == proc) process = null;

            // Output on stderr isn't a failure (pyaudio prints warnings there on macOS).
            // Failure is a non-zero exit code or no transcript.
            string error = stderr.ToString();
            if (exitCode != 0)
            {
                UnityEngine.Debug.LogError($"STT python exited with code {exitCode}: {error}");
                return NO_TRANSCRIPT;
            }

            if (!string.IsNullOrEmpty(error))
                UnityEngine.Debug.LogWarning($"STT stderr (informational): {error}");

            if (string.IsNullOrEmpty(output))
            {
                // The script prints nothing when no speech was heard.
                UnityEngine.Debug.LogWarning("Python script returned empty output (no speech heard).");
                return NO_TRANSCRIPT;
            }

            return output;
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError($"Exception running Python script: {ex.Message}");

            // Clean up process if it exists
            if (proc != null)
            {
                lock (_pttLock) { if (_pttProcess == proc) _pttProcess = null; }
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill();
                    }
                    proc.Dispose();
                }
                catch { }
                if (process == proc) process = null;
            }

            return NO_TRANSCRIPT;
        }
    }

    /// <summary>
    /// The push-to-talk key was released: the script stops listening and returns what
    /// it heard up to now. Nothing said after this is transcribed.
    /// </summary>
    public void EndPushToTalk()
    {
        lock (_pttLock)
        {
            _pttReleased = true;
            if (_pttProcess != null) SendRelease(_pttProcess);
        }
    }

    // Called under _pttLock, at most once per process: one line, then stdin is closed.
    private static void SendRelease(Process proc)
    {
        try
        {
            if (proc.HasExited) return;
            proc.StandardInput.WriteLine("release");
            proc.StandardInput.Close();
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogWarning($"Could not tell STT the key was released: {ex.Message}");
        }
    }

    public void StopCurrentRecognition()
    {
        _sttGeneration++;   // the run being stopped must not deliver its result
        lock (_pttLock) { _pttProcess = null; }

        // Stop Python process
        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                {
                    AppLog.Detail(LogArea.Speech, $"Killing STT Python process (PID {process.Id})");
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

        AppLog.Detail(LogArea.Speech, "Cancelling STT (soft), leaving mic live.");
        StopCurrentRecognition();
        CleanupRecognition("cancelled transcript");
    }

    public bool IsListening
    {
        get { lock (_pttLock) return isProcessing && !(_pttRun && _pttReleased); }
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
