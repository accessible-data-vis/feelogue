using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class AudioToneManager : MonoBehaviour, InterfaceAudioToneManager
{
    [SerializeField] private AudioSource startTone;
    [SerializeField] private AudioSource endTone;
    [SerializeField] private AudioSource waitTone;

    public void PlayStartTone()
    {
        if (startTone != null)
            startTone.Play();
    }

    public void PlayEndTone()
    {
        if (endTone != null)
            endTone.Play();
    }

    public void LoopWaitTone()
    {
        if (waitTone != null && !waitTone.isPlaying)
        {
            waitTone.volume = 0.1f;
            waitTone.Play();
        }
    }
    
    public void StopWaitTone()
    {
        if (waitTone != null && waitTone.isPlaying)
            waitTone.Stop();
    }
}
