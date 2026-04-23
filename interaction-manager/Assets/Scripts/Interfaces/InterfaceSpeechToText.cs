using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public interface InterfaceSpeechToText
{
    public void StartSpeechRecognition(System.Action<string> callback, bool quietMode, string touchData, Action onComplete, bool fromWakeWord = false);
    bool GetSkipStatus();
    void SetSkipStatus(bool status);
    void StopSpeechRecognition();
    void CancelSpeechRecognition();
}
