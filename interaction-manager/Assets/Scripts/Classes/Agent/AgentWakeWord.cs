//
// Copyright 2021-2023 Picovoice Inc.
//
// You may not use this file except in compliance with the license. A copy of the license is located in the "LICENSE"
// file accompanying this source.
//
// Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on
// an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the
// specific language governing permissions and limitations under the License.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using System.Collections;
using Pv.Unity;
using System.Runtime.InteropServices;

public class AgentWakeWord : MonoBehaviour, InterfaceAgentWakeWord
{
    [SerializeField] private MonoBehaviour textToSpeechService;
    private InterfaceTextToSpeech _textToSpeech;
    private static readonly string ACCESS_KEY = LoadPorcupineConfig("access_key");
    private static readonly string KEYWORD_PATH = LoadPorcupineConfig("keyword_path");
    static List<string> keywordPaths = new List<string>(){ Path.Combine(Application.dataPath, "..", KEYWORD_PATH) };
    private bool _isProcessing;
    PorcupineManager _porcupineManager;
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
        try
        {
            _porcupineManager = PorcupineManager.FromKeywordPaths(ACCESS_KEY, keywordPaths, OnWakeWordDetected, processErrorCallback: ErrorCallback);
        }
        catch (PorcupineInvalidArgumentException ex)
        {
            SetError(ex.Message);
        }
        catch (PorcupineActivationException)
        {
            SetError("AccessKey activation error");
        }
        catch (PorcupineActivationLimitException)
        {
            SetError("AccessKey reached its device limit");
        }
        catch (PorcupineActivationRefusedException)
        {
            SetError("AccessKey refused");
        }
        catch (PorcupineActivationThrottledException)
        {
            SetError("AccessKey has been throttled");
        }
        catch (PorcupineException ex)
        {
            SetError("PorcupineManager was unable to initialize: " + ex.Message);
        }
        
        ToggleProcessing();
    }

    private void ToggleProcessing()
    {
        if (!_isProcessing)
        {
            StartProcessing();
        }
        else
        {
            StopProcessing();
        }
    }

    private void StartProcessing()
    {
        if (_porcupineManager != null && !_isProcessing)
        {
            _isProcessing = true;
            _porcupineManager.Start();
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
        _textToSpeech.SetIsProcessing(false);
        Debug.Log("All audio stopped.");
    }

    private void StopProcessing()
    {
        if (_porcupineManager != null && _isProcessing)
        {
            _isProcessing = false;
            _porcupineManager.Stop();
        }
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
        if (isError)
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
        StopProcessing();
        Debug.Log("Wake word detection paused.");
    }

    public void ResumeWakeWord()
    {
        StartProcessing();
        Debug.Log("Wake word detection resumed.");
    }

    public void RestartWakeWordDetection()
    {
        Debug.Log("Restarting wake word detection...");

        // Step 1: Stop all active processes
        StopProcessing();
        StopAudio();

        // Step 2: Reset necessary variables
        _isProcessing = false;
        isError = false;
        lastWakeWordTime = 0f; // Reset cooldown timer

        // Step 3: Reinitialize PorcupineManager
        try
        {
            if (_porcupineManager != null)
            {
                _porcupineManager.Delete(); // Fully destroy existing instance
            }

            _porcupineManager = PorcupineManager.FromKeywordPaths(
                ACCESS_KEY, keywordPaths, OnWakeWordDetected, processErrorCallback: ErrorCallback);

            Debug.Log("PorcupineManager successfully reinitialized.");
        }
        catch (PorcupineException ex)
        {
            SetError("PorcupineManager failed to restart: " + ex.Message);
            return;
        }

        // Step 4: Resume wake word detection
        ResumeWakeWord();

        Debug.Log("Wake word detection restarted successfully.");
    }

    private void ErrorCallback(Exception e)
    {
        SetError(e.Message);
    }

    private void SetError(string message)
    {
        isError = true;
        StopProcessing();
    }

    void Update()
    {
        if (isError)
        {
            return;
        }
    }

    void OnApplicationQuit()
    {
        if (_porcupineManager != null)
        {
            _porcupineManager.Delete();
        }
    }

    private static string LoadPorcupineConfig(string key)
    {
        string keyPath = Path.Combine(Application.dataPath, "..", "key-porcupine.json");
        try
        {
            string json = File.ReadAllText(keyPath);
            JObject obj = JObject.Parse(json);
            return obj[key]?.ToString() ?? "";
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"Failed to load Porcupine config '{key}' from {keyPath}: {e.Message}");
            return "";
        }
    }
}
