using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

public class TextToSpeech : MonoBehaviour, InterfaceTextToSpeech
{
    [SerializeField] private AudioSource audioSource;

    private string pythonPath;
    private string scriptPath;
    private string tempDir;
    private bool isProcessing = false;
    private Coroutine activePlaybackCoroutine = null;

    // Bumped by every new request and every interrupt. Audio that finishes under an
    // older number is dropped without playing or running its callback.
    private int _generation = 0;

    // The current request's synthesis, killed on interrupt.
    private Process _activeProcess;
    private readonly object _processLock = new object();

    // One file per utterance so syntheses can't overwrite each other. Repeat (F3)
    // replays the last file played.
    private int _fileCounter = 0;
    private string _lastPlayedPath;

    // Audio already made (spoken or prepared ahead), keyed by text and voice.
    private const int CacheSize = 12;
    private readonly Dictionary<string, string> _ready = new Dictionary<string, string>();
    private readonly List<string> _readyOrder = new List<string>();
    private readonly HashSet<string> _preparing = new HashSet<string>();
    private readonly object _cacheLock = new object();

    // Awake, so a request made in another component's Start already has its paths.
    void Awake()
    {
        pythonPath = EnvLoader.Get("PYTHON_PATH", "python3");
        scriptPath = Path.Combine(Application.dataPath, "StreamingAssets", "Tools", "google_cloud_texttospeech_v1.py");

        // Write output outside Assets/ so Unity doesn't try to import it
        tempDir = Path.Combine(Application.dataPath, "..", "Temp", "tts");
        try
        {
            Directory.CreateDirectory(tempDir);
            foreach (var old in Directory.GetFiles(tempDir, "*.wav"))
                File.Delete(old);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning($"Could not prepare the TTS folder: {e.Message}");
        }
    }

    public void ConvertTextToSpeech(string text, SpeechSettings settings, Action onComplete)
    {
        if (isProcessing)
        {
            UnityEngine.Debug.LogWarning("TTS busy. Interrupting previous request");
            StopCurrentPlayback();
        }
        if (string.IsNullOrEmpty(text))
        {
            UnityEngine.Debug.LogError("No text for TTS.");
            return;
        }

        AppLog.Info(LogArea.Speech, $"Speaking: \"{text}\"");
        isProcessing = true;
        int generation = ++_generation;
        string key = CacheKey(text, settings);

        string ready;
        bool preparing;
        lock (_cacheLock)
        {
            _ready.TryGetValue(key, out ready);
            preparing = _preparing.Contains(key);
        }
        if (ready != null && File.Exists(ready))
        {
            StartPlayback(ready, generation, onComplete);
            return;
        }
        if (preparing)
        {
            activePlaybackCoroutine = StartCoroutine(PlayWhenPrepared(key, text, settings, generation, onComplete));
            return;
        }
        StartSynthesis(text, settings, key, generation, onComplete);
    }

    public void Prefetch(string text, SpeechSettings settings)
    {
        if (string.IsNullOrEmpty(text)) return;
        string key = CacheKey(text, settings);
        lock (_cacheLock)
        {
            if (_preparing.Contains(key)) return;
            if (_ready.TryGetValue(key, out var existing) && File.Exists(existing)) return;
            _preparing.Add(key);
        }
        string path = NewPath();
        string arguments = BuildArguments(text, settings, path);
        var thread = new Thread(() =>
        {
            bool ok = Synthesize(arguments, path, trackAsActive: false);
            lock (_cacheLock) _preparing.Remove(key);
            if (ok) Remember(key, path);
            else TryDelete(path);
        });
        thread.IsBackground = true;
        thread.Start();
    }

    public void StopSpeechPlayback()
    {
        StopCurrentPlayback();
    }

    public void StopCurrentPlayback()
    {
        if (audioSource != null && audioSource.isPlaying)
        {
            audioSource.Stop();
            AppLog.Detail(LogArea.Speech, "TTS audio stopped");
        }

        if (activePlaybackCoroutine != null)
        {
            StopCoroutine(activePlaybackCoroutine);
            activePlaybackCoroutine = null;
        }

        _generation++;   // anything still being made for the old request is discarded
        KillActiveProcess();
        isProcessing = false;
    }

    public void RepeatLastAudio()
    {
        if (string.IsNullOrEmpty(_lastPlayedPath) || !File.Exists(_lastPlayedPath))
        {
            UnityEngine.Debug.LogWarning("Repeat failed: nothing has been spoken yet.");
            return;
        }

        if (isProcessing)
        {
            UnityEngine.Debug.LogWarning("TTS busy. Interrupting to repeat.");
            StopCurrentPlayback();
        }

        isProcessing = true;
        StartPlayback(_lastPlayedPath, ++_generation, null);
    }

    public void SetIsProcessing(bool status)
    {
        isProcessing = status;
    }

    public bool IsSpeaking()
    {
        return isProcessing;
    }

    // ===== Synthesis =====

    private void StartSynthesis(string text, SpeechSettings settings, string key, int generation, Action onComplete)
    {
        string path = NewPath();
        string arguments = BuildArguments(text, settings, path);
        var thread = new Thread(() =>
        {
            bool ok = Synthesize(arguments, path, trackAsActive: true);
            if (ok) Remember(key, path);
            else
            {
                TryDelete(path);
                UnityEngine.Debug.LogWarning($"TTS: no audio was made for \"{text}\"");
            }

            var dispatcher = UnityMainThreadDispatcher.Instance();
            if (dispatcher == null)
            {
                UnityEngine.Debug.LogError("UnityMainThreadDispatcher is missing from the scene!");
                return;
            }
            dispatcher.Enqueue(() =>
            {
                if (generation != _generation)
                {
                    AppLog.Detail(LogArea.Speech, "Stale synthesis discarded");
                    return;
                }
                if (ok) StartPlayback(path, generation, onComplete);
                else Finish(generation, onComplete);
            });
        });
        thread.IsBackground = true;
        thread.Start();
    }

    private IEnumerator PlayWhenPrepared(string key, string text, SpeechSettings settings, int generation, Action onComplete)
    {
        // Already being prepared (e.g. the next chunk): wait for it.
        float waited = 0f;
        while (waited < 10f)
        {
            lock (_cacheLock)
            {
                if (!_preparing.Contains(key)) break;
            }
            waited += Time.deltaTime;
            yield return null;
        }
        activePlaybackCoroutine = null;
        if (generation != _generation) yield break;

        string ready;
        lock (_cacheLock) _ready.TryGetValue(key, out ready);
        if (ready != null && File.Exists(ready)) StartPlayback(ready, generation, onComplete);
        else StartSynthesis(text, settings, key, generation, onComplete);
    }

    private string BuildArguments(string text, SpeechSettings settings, string outPath)
    {
        // Escape quotes and backslashes to prevent command-line parsing errors
        string escapedText = text.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string arguments = $"\"{scriptPath}\" \"{escapedText}\" --out \"{outPath}\"";

        if (settings != null)
        {
            if (settings.selectionMode == VoiceSelectionMode.SpecificVoice && !string.IsNullOrEmpty(settings.voiceName))
            {
                arguments += $" --voice-name \"{settings.voiceName}\"";
                arguments += $" --language \"{settings.languageCode}\"";
            }
            else
            {
                arguments += $" --language \"{settings.languageCode}\"";
                arguments += $" --gender {settings.voiceGender.ToString().ToUpper()}";
            }

            arguments += $" --speed {settings.speakingRate.ToString(CultureInfo.InvariantCulture)}";
            arguments += $" --pitch {settings.pitch.ToString(CultureInfo.InvariantCulture)}";
        }
        return arguments;
    }

    /// <summary>Run the synthesis script into outPath on a worker thread; true if it made the file.</summary>
    private bool Synthesize(string arguments, string outPath, bool trackAsActive)
    {
        TryDelete(outPath);
        var psi = new ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process process = null;
        try
        {
            process = new Process { StartInfo = psi };
            var errors = new StringBuilder();
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
            process.Start();
            if (trackAsActive)
                lock (_processLock) _activeProcess = process;
            // stderr is read asynchronously: reading both pipes in turn can deadlock
            // when the library writes a lot to stderr.
            process.BeginErrorReadLine();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (!string.IsNullOrEmpty(output))
                AppLog.Detail(LogArea.Speech, $"Python TTS output: {output}");
            string err;
            lock (errors) err = errors.ToString();
            if (!string.IsNullOrEmpty(err))
                UnityEngine.Debug.LogWarning($"Python TTS errors: {err}");

            return process.ExitCode == 0 && File.Exists(outPath);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"TTS Python script failed: {e.Message}");
            return false;
        }
        finally
        {
            if (trackAsActive)
                lock (_processLock)
                    if (_activeProcess == process) _activeProcess = null;
            process?.Dispose();
        }
    }

    private void KillActiveProcess()
    {
        Process process;
        lock (_processLock)
        {
            process = _activeProcess;
            _activeProcess = null;
        }
        try
        {
            if (process != null && !process.HasExited) process.Kill();
        }
        catch (Exception)
        {
            // Already exited or disposed: nothing to stop.
        }
    }

    private string NewPath() => Path.Combine(tempDir, $"tts_{Interlocked.Increment(ref _fileCounter)}.wav");

    private static string CacheKey(string text, SpeechSettings s) =>
        s == null
            ? $"default|{text}"
            : $"{s.selectionMode}|{s.voiceName}|{s.languageCode}|{s.voiceGender}|" +
              $"{s.speakingRate.ToString(CultureInfo.InvariantCulture)}|{s.pitch.ToString(CultureInfo.InvariantCulture)}|{text}";

    private void Remember(string key, string path)
    {
        var evicted = new List<string>();
        lock (_cacheLock)
        {
            if (_ready.TryGetValue(key, out var previous) && previous != path)
                evicted.Add(previous);
            _ready[key] = path;
            _readyOrder.Remove(key);
            _readyOrder.Add(key);
            while (_readyOrder.Count > CacheSize)
            {
                string oldest = _readyOrder[0];
                _readyOrder.RemoveAt(0);
                if (_ready.TryGetValue(oldest, out var oldPath))
                {
                    _ready.Remove(oldest);
                    evicted.Add(oldPath);
                }
            }
        }
        foreach (var p in evicted)
            if (p != _lastPlayedPath) TryDelete(p);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // In use or already gone: harmless, the folder is cleared on start.
        }
    }

    // ===== Playback (main thread) =====

    private void StartPlayback(string path, int generation, Action onComplete)
    {
        activePlaybackCoroutine = StartCoroutine(PlayAudioAsync(path, generation, onComplete));
    }

    /// <summary>Only the current request finishes: clears the speaking flag and runs its callback.</summary>
    private void Finish(int generation, Action onComplete)
    {
        if (generation != _generation) return;
        isProcessing = false;
        activePlaybackCoroutine = null;
        onComplete?.Invoke();
    }

    private IEnumerator PlayAudioAsync(string filePath, int generation, Action onComplete)
    {
        AppLog.Detail(LogArea.Speech, $"Loading audio file: {filePath}");

        string url = "file://" + filePath;

        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.WAV))
        {
            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.ConnectionError || www.result == UnityWebRequest.Result.ProtocolError)
            {
                UnityEngine.Debug.LogError($"Error loading audio: {www.error}");
                Finish(generation, onComplete);
                yield break;
            }

            AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
            if (clip.loadState != AudioDataLoadState.Loaded)
            {
                UnityEngine.Debug.LogError("Error: AudioClip failed to load.");
                Finish(generation, onComplete);
                yield break;
            }
            if (audioSource == null)
            {
                UnityEngine.Debug.LogError("AudioSource is null, cannot play TTS clip.");
                Finish(generation, onComplete);
                yield break;
            }
            if (generation != _generation) yield break;   // superseded while loading

            audioSource.clip = clip;
            audioSource.Play();
            _lastPlayedPath = filePath;
            AppLog.Detail(LogArea.Speech, $"Playing TTS clip ({clip.length:F2}s)");
            while (audioSource.isPlaying)
                yield return null;
            Finish(generation, onComplete);
        }
    }
}
