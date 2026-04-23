using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public interface InterfaceRTDButtonParser
{
    public void ProcessButtonPacket(byte[] packet);
    event Action PanNextPressed;
    event Action PanNextReleased;
    event Action PanNextPressedImmediate;
    event Action PanPrevPressed;
    event Action PanPrevReleased;
    event Action PanPrevPressedImmediate;
    event Action Function1Pressed;
    event Action Function1Released;
    event Action Function2Pressed;
    event Action Function3Pressed;
    event Action Function4Pressed;
    event Action BothPanButtonsPressed;
}
