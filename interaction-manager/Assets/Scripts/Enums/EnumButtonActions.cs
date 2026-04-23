using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public enum PanningAction : byte
{
    Next = 0x02,
    Prev = 0x04,
    Release = 0x00,
}

public enum FunctionAction : byte
{
    F1 = 0x80,
    F2 = 0x40,
    F3 = 0x20,
    F4 = 0x10,
    Release = 0x00,
}

public enum ButtonState
{
    None = 0,   //0000
    F1 = 1 << 0, //0001
    F2 = 1 << 1, //0010
    F3 = 1 << 2, //0100
    F4 = 1 << 3 //1000
}
