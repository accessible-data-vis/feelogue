using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

public class TextToSpeech : MonoBehaviour, InterfaceTextToSpeech
{
    [SerializeField] private AudioSource audioSource;

    private string pythonPath;
    private string scriptPath;
    private string outputFilePath;
    private bool isProcessing = false;
    private Coroutine activePlaybackCoroutine = null;
    private Thread activePythonThread = null;

    void Start()
    {
        pythonPath = EnvLoader.Get("PYTHON_PATH", "python3");
        scriptPath = Path.Combine(Application.dataPath, "StreamingAssets", "Tools", "google_cloud_texttospeech_v1.py");

        // Write output outside Assets/ so Unity doesn't try to import it
        outputFilePath = Path.Combine(Application.dataPath, "..", "Temp", "output.wav");
        if (File.Exists(outputFilePath))
        {
            try
            {
                File.Delete(outputFilePath);
                UnityEngine.Debug.Log($"Deleted old TTS output at {outputFilePath}");
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"output.wav already deleted: {e.Message}");
            }
        }
    }

    public void ConvertTextToSpeech(string text) => ConvertTextToSpeech(text, null, null);

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

        isProcessing = true;

        Action wrappedCallback = () =>
        {
            isProcessing = false;
            onComplete?.Invoke();
        };

        activePythonThread = new Thread(() => RunPythonScript(text, settings, wrappedCallback));
        activePythonThread.Start();
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
            UnityEngine.Debug.Log("TTS audio stopped");
        }

        if (activePlaybackCoroutine != null)
        {
            StopCoroutine(activePlaybackCoroutine);
            activePlaybackCoroutine = null;
        }

        isProcessing = false;
    }

    public void RepeatLastAudio()
    {
        if (!File.Exists(outputFilePath))
        {
            UnityEngine.Debug.LogWarning("Repeat failed: no existing output.wav found.");
            return;
        }

        if (isProcessing)
        {
            UnityEngine.Debug.LogWarning("TTS busy. Cannot repeat right now.");
            StopCurrentPlayback();
        }

        isProcessing = true;
        var dispatcher = UnityMainThreadDispatcher.Instance();
        if (dispatcher != null)
        {
            dispatcher.Enqueue(() =>
            {
                activePlaybackCoroutine = StartCoroutine(PlayAudioAsync(outputFilePath, () =>
                {
                    isProcessing = false;
                    activePlaybackCoroutine = null;
                }));
            });
        }
        else
        {
            UnityEngine.Debug.LogError("UnityMainThreadDispatcher missing. Cannot repeat audio.");
            isProcessing = false;
        }
    }

    private void RunPythonScript(string text, SpeechSettings settings, Action onComplete)
    {
        // Escape quotes and backslashes to prevent command-line parsing errors
        string escapedText = text.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string arguments = $"\"{scriptPath}\" \"{escapedText}\"";

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

            arguments += $" --speed {settings.speakingRate}";
            arguments += $" --pitch {settings.pitch}";
        }

        // Delete old output file to prevent stale audio playing before new generation completes
        if (File.Exists(outputFilePath))
        {
            try
            {
                File.Delete(outputFilePath);
                UnityEngine.Debug.Log($"Deleted old output.wav before generating new TTS");
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"Could not delete old output.wav: {e.Message}");
            }
        }

        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using (Process process = new Process { StartInfo = psi })
            {
                process.Start();

                string output = process.StandardOutput.ReadToEnd();
                string errors = process.StandardError.ReadToEnd();

                process.WaitForExit();

                if (!string.IsNullOrEmpty(output))
                    UnityEngine.Debug.Log($"Python TTS output: {output}");
                if (!string.IsNullOrEmpty(errors))
                    UnityEngine.Debug.LogWarning($"Python TTS errors: {errors}");
            }

            if (File.Exists(outputFilePath))
            {
                var dispatcher = UnityMainThreadDispatcher.Instance();
                if (dispatcher != null)
                {
                    dispatcher.Enqueue(() =>
                    {
                        activePlaybackCoroutine = StartCoroutine(PlayAudioAsync(outputFilePath, onComplete));
                    });
                }
                else
                {
                    UnityEngine.Debug.LogError("UnityMainThreadDispatcher is missing from the scene!");
                    onComplete?.Invoke();
                }
            }
            else
            {
                UnityEngine.Debug.LogError($"Error: Audio file not found at {outputFilePath}");
                onComplete?.Invoke();
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"TTS Python script failed: {e.Message}");
            onComplete?.Invoke();
        }
    }

    public void SetIsProcessing(bool status)
    {
        isProcessing = status;
    }

    public bool IsSpeaking()
    {
        return isProcessing;
    }

    private IEnumerator PlayAudioAsync(string filePath, Action onComplete)
    {
        UnityEngine.Debug.Log($"Loading audio file: {filePath}");

        string url = "file://" + filePath;

        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.WAV))
        {
            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.ConnectionError || www.result == UnityWebRequest.Result.ProtocolError)
            {
                UnityEngine.Debug.LogError($"Error loading audio: {www.error}");
                activePlaybackCoroutine = null;
                activePythonThread = null;
                onComplete?.Invoke();
                yield break;
            }

            AudioClip clip = DownloadHandlerAudioClip.GetContent(www);

            if (clip.loadState == AudioDataLoadState.Loaded)
            {
                yield return StartCoroutine(PlayAndWaitCoroutine(clip, onComplete));
            }
            else
            {
                UnityEngine.Debug.LogError("Error: AudioClip failed to load.");
                activePlaybackCoroutine = null;
                activePythonThread = null;
                onComplete?.Invoke();
            }
        }
    }

    private IEnumerator PlayAndWaitCoroutine(AudioClip clip, Action onComplete)
    {
        if (audioSource == null)
        {
            UnityEngine.Debug.LogError("AudioSource is null, cannot play TTS clip.");
            onComplete?.Invoke();
            yield break;
        }

        audioSource.clip = clip;
        audioSource.Play();
        UnityEngine.Debug.Log($"Playing TTS clip ({clip.length:F2}s)");
        while (audioSource.isPlaying)
            yield return null;
        isProcessing = false;
        activePlaybackCoroutine = null;
        activePythonThread = null;
        onComplete?.Invoke();
    }
}
