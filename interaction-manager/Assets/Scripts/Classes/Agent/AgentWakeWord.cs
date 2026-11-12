using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Newtonsoft.Json.Linq;

/// <summary>
/// Wake word detection with openWakeWord, run as a background Python process
/// (StreamingAssets/Tools/openwakeword_listen.py). It starts at Play, prints one JSON
/// line per detection, and is paused and resumed over stdin. The listener opens its
/// own microphone stream, so stopping it never cuts off other audio.
/// </summary>
public class AgentWakeWord : MonoBehaviour, InterfaceAgentWakeWord
{
    [SerializeField] private MonoBehaviour textToSpeechService;
    private InterfaceTextToSpeech _textToSpeech;

    [Tooltip("A trained .onnx file under StreamingAssets, e.g. keyword_files/Hey_Graphy.onnx " +
             "(a bare name like Hey_Graphy also finds keyword_files/Hey_Graphy.onnx), " +
             "or a pre-trained openWakeWord model: hey_jarvis, alexa, hey_mycroft, hey_rhasspy.")]
    [SerializeField] private string wakeWordModel = "keyword_files/Hey_Graphy.onnx";
    [Range(0f, 1f)]
    [SerializeField] private float detectionThreshold = 0.5f;

    private const string LISTENER_SCRIPT = "openwakeword_listen.py";

    private System.Diagnostics.Process _listener;
    private bool _paused = false;
    private bool isError = false;
    private float lastWakeWordTime = 0f;
    private float wakeWordCooldown = 2.0f;
    public static bool isWakeWordActive = false;
    public event Action WakeWordDetected = delegate { };

    void Awake()
    {
        _textToSpeech = textToSpeechService as InterfaceTextToSpeech ?? throw new InvalidOperationException("textToSpeechService not assigned!");
    }

    void Start()
    {
        StartListener();
    }

    private void StartListener()
    {
        string pythonPath = EnvLoader.Get("PYTHON_PATH", "python3");
        string scriptPath = Path.Combine(Application.streamingAssetsPath, "Tools", LISTENER_SCRIPT);

        // A .onnx model is a file under StreamingAssets. A bare name is a trained model
        // in keyword_files/ when that file exists, otherwise a pre-trained model name
        // the listener resolves itself.
        string model = wakeWordModel.Trim();
        if (!model.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
        {
            string trained = Path.Combine("keyword_files", model + ".onnx");
            if (File.Exists(Path.Combine(Application.streamingAssetsPath, trained)))
                model = trained;
        }
        if (model.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
        {
            model = Path.Combine(Application.streamingAssetsPath, model);
            if (!File.Exists(model))
            {
                SetError($"model file not found: {model}");
                return;
            }
        }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = $"\"{scriptPath}\" --model \"{model}\" " +
                        $"--threshold {detectionThreshold.ToString(CultureInfo.InvariantCulture)}",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            var proc = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (s, e) => { if (e.Data != null) OnListenerLine(e.Data); };
            proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
            proc.Exited += (s, e) => OnListenerExited(proc, stderr);
            _listener = proc;
            _paused = false;
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch (Exception e)
        {
            _listener = null;
            SetError("could not start the listener: " + e.Message);
        }
    }

    // Runs on a thread-pool thread: parse here, act on the main thread.
    private void OnListenerLine(string line)
    {
        JObject evt;
        try { evt = JObject.Parse(line); }
        catch (Exception) { return; }   // not a protocol line

        var dispatcher = UnityMainThreadDispatcher.Instance();
        if (dispatcher == null) return;

        string kind = evt["event"]?.ToString();
        if (kind == "detected")
        {
            dispatcher.Enqueue(() =>
            {
                AppLog.Detail(LogArea.Speech, $"Wake word heard ({evt["name"]}, score {evt["score"]})");
                OnWakeWordDetected(0);
            });
        }
        else if (kind == "ready")
        {
            dispatcher.Enqueue(() => AppLog.Info(LogArea.Speech, $"Wake word ready ({evt["model"]})"));
        }
    }

    // Runs on a thread-pool thread. A listener stopped on purpose is detached from
    // _listener first, so only an unexpected exit gets this far.
    private void OnListenerExited(System.Diagnostics.Process proc, StringBuilder stderr)
    {
        if (_listener != proc) return;
        try { proc.WaitForExit(); } catch (Exception) { }   // let stderr finish draining
        string error;
        lock (stderr) error = stderr.ToString().Trim();

        var dispatcher = UnityMainThreadDispatcher.Instance();
        if (dispatcher == null) return;
        dispatcher.Enqueue(() =>
        {
            if (_listener != proc) return;
            _listener = null;
            SetError("listener exited" + (string.IsNullOrEmpty(error) ? "" : ": " + error));
        });
    }

    private void StopListener()
    {
        var proc = _listener;
        _listener = null;   // its exit is expected from here on
        if (proc == null) return;
        try
        {
            if (!proc.HasExited)
            {
                proc.StandardInput.Close();   // EOF: the listener exits on its own
                if (!proc.WaitForExit(1000))
                    proc.Kill();
            }
            proc.Dispose();
        }
        catch (Exception e)
        {
            Debug.LogWarning("Stopping wake word listener: " + e.Message);
        }
    }

    private void SendCommand(string command)
    {
        // Pause and resume run on every push to talk, so a failed listener must do nothing here.
        if (isError || _listener == null) return;
        try
        {
            _listener.StandardInput.WriteLine(command);
            _listener.StandardInput.Flush();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Wake word {command} failed: {e.Message}");
        }
    }

    public void StopAudio()
    {
        // Stop all AudioSources in the scene
        AudioSource[] allAudioSources = FindObjectsOfType<AudioSource>();
        foreach (AudioSource audio in allAudioSources)
        {
            if (audio.isPlaying)
            {
                audio.Stop();
            }
        }
        // Also drops speech that is still being prepared.
        _textToSpeech.StopSpeechPlayback();
        AppLog.Detail(LogArea.Speech, "All audio stopped.");
    }

    public bool GetWakeWordStatus()
    {
        return isWakeWordActive;
    }

    public void SetWakeWordStatus(bool status)
    {
        isWakeWordActive = status;
    }

    public void OnWakeWordDetected(int keywordIndex)
    {
        // A detection already on its way when listening was paused is stale.
        if (isError || _paused)
        {
            return;
        }

        isWakeWordActive = true;
        float currentTime = Time.time;
        if (currentTime - lastWakeWordTime < wakeWordCooldown) return;

        lastWakeWordTime = currentTime;

        if (keywordIndex >= 0)
        {
            WakeWordDetected();
        }
    }

    public void PauseWakeWord()
    {
        if (_paused) return;
        _paused = true;
        SendCommand("pause");
        AppLog.Detail(LogArea.Speech, "Wake word detection paused.");
    }

    public void ResumeWakeWord()
    {
        if (!_paused) return;
        _paused = false;
        SendCommand("resume");
        AppLog.Detail(LogArea.Speech, "Wake word detection resumed.");
    }

    public void RestartWakeWordDetection()
    {
        AppLog.Info(LogArea.Speech, "Restarting wake word detection...");
        StopListener();
        StopAudio();
        isError = false;
        lastWakeWordTime = 0f; // Reset cooldown timer
        StartListener();
    }

    private void SetError(string message)
    {
        if (isError) return;   // report the first failure only
        isError = true;
        Debug.LogError($"Wake word disabled for this session: {message}");
        StopListener();
    }

    void OnDestroy()
    {
        StopListener();
    }

    void OnApplicationQuit()
    {
        StopListener();
    }
}
