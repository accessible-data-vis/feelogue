using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public interface InterfaceMQTTManager
{
    void ToggleMQTTSource();
    MQTTReceiver GetActiveMQTTInstance();
    bool IsUsingRemote();
    void PublishInteraction(string json);
    void PublishChart(string chartData);
    void OnMessageReceived(string topic, string payload);
    event Action<string, string> MessageReceived;
    event Action MQTTConnected;
}
