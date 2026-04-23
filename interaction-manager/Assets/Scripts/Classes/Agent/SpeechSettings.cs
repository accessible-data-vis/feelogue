using UnityEngine;

[CreateAssetMenu(fileName = "SpeechSettings", menuName = "Audio/Speech Settings")]
public class SpeechSettings : ScriptableObject
{
    public VoiceSelectionMode selectionMode = VoiceSelectionMode.LanguageAndGender;

    [Tooltip("Specific voice (only used if mode = SpecificVoice)")]
    public string voiceName = "";

    [Tooltip("Language + gender (only used if mode = LanguageAndGender)")]
    public string languageCode = "en-AU"; // Default: Australian English — change to match your target locale
    public VoiceGender voiceGender = VoiceGender.Female;

    [Header("Speech Speed")]
    [Tooltip("Speaking rate: 0.25 (very slow) to 4.0 (very fast). Default is 1.0")]
    [Range(0.25f, 4.0f)]
    public float speakingRate = 1.0f;

    [Tooltip("Pitch adjustment: -20.0 (lower) to 20.0 (higher). Default is 0.0")]
    [Range(-20f, 20f)]
    public float pitch = 0.0f;

    [Header("Pause Durations (milliseconds)")]
    [Tooltip("Pause between data values (Year: 2023 [PAUSE] Rainfall: 45.2)")]
    public int valuesPause = 300;

    [Tooltip("Pause between label and value (Year: [PAUSE] 2023)")]
    public int labelPause = 200;

    [Tooltip("Use slower rate when data has more than this many values")]
    public int complexDataThreshold = 3;

    [Tooltip("Rate for complex data")]
    public string complexDataRate = "slow";

    [Header("Emphasis")]
    [Tooltip("Level: none, reduced, moderate, strong")]
    public string emphasisLevel = "moderate";

    [Tooltip("Emphasize numeric values")]
    public bool emphasizeValues = true;

    [Header("Number Format")]
    [Tooltip("How to pronounce numbers")]
    public NumberStyle numberPronunciation = NumberStyle.Cardinal;
}

public enum VoiceSelectionMode
{
    SpecificVoice,
    LanguageAndGender
}

public enum VoiceGender
{
    Neutral,
    Male,
    Female
}

public enum NumberStyle
{
    Cardinal,  // "forty-five point two"
    Digits     // "four five point two"
}
