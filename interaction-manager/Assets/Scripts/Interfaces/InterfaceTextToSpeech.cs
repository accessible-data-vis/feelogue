using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public interface InterfaceTextToSpeech
{
    void ConvertTextToSpeech(string text);
    void ConvertTextToSpeech(string text, SpeechSettings settings, Action onComplete);
    void StopSpeechPlayback();
    void RepeatLastAudio();
    void SetIsProcessing(bool status);
    bool IsSpeaking();

}
