using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public interface InterfaceSpeechToText
{
    public void StartSpeechRecognition(System.Action<string> callback, bool quietMode, string touchData, Action onComplete, bool fromWakeWord = false, bool pushToTalk = false);
    // The push-to-talk key was released: end the recording and return what was heard.
    void EndPushToTalk();
    bool GetSkipStatus();
    void SetSkipStatus(bool status);
    void StopSpeechRecognition();
    void CancelSpeechRecognition();
    // True while a recording can still take in speech: from its start until its transcript
    // arrives, except that push to talk stops listening when the key is released.
    bool IsListening { get; }
}
