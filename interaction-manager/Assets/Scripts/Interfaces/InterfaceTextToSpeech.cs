using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public interface InterfaceTextToSpeech
{
    // Callers always pass the shared SpeechSettings.
    void ConvertTextToSpeech(string text, SpeechSettings settings, Action onComplete);
    // Prepare this text's audio in the background (e.g. the next chunk) without playing it.
    void Prefetch(string text, SpeechSettings settings);
    void StopSpeechPlayback();
    void RepeatLastAudio();
    /// <summary>Set the speaking flag directly (StopSpeechPlayback also drops pending audio).</summary>
    void SetIsProcessing(bool status);
    bool IsSpeaking();

}
