using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public interface InterfaceAgentWakeWord
{
    event Action WakeWordDetected;
    void OnWakeWordDetected(int wakeWordId);
    bool GetWakeWordStatus();
    void SetWakeWordStatus(bool status);
    void StopAudio();
    void PauseWakeWord();
    void ResumeWakeWord();
}
