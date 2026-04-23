/*
The MIT License (MIT)

Copyright (c) 2018 Giovanni Paolo Vigano'

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using UnityEngine;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;
using M2MqttUnity;

public class MQTTReceiver : M2MqttUnityClient
{
    [SerializeField] private MonoBehaviour rtdUpdaterService;
    private InterfaceRTDUpdater _rtdUpdater;
    [SerializeField] private MonoBehaviour textToSpeechService;
    private InterfaceTextToSpeech _textToSpeech;
    [SerializeField] private MonoBehaviour buttonGUIService;
    private InterfaceButtonGUI _buttonGUI;
    [SerializeField] private MonoBehaviour graphVisualizerService;
    private InterfaceGraphVisualizer _graphVisualizer;

    [SerializeField] public string topicSubscribe = "agent_out";
    [SerializeField] public string topicInteractionData = "agent_in";
    [SerializeField] public string topicChartData = "agent_in";
    [Tooltip("When enabled, broker address and credentials are loaded from .env (MQTT_REMOTE_* keys).")]
    [SerializeField] public bool isRemote = false;

    public event Action<string, string> MessageReceived = delegate { };
    public event Action Connected = delegate { };

    public bool isConnected { get; private set; }

    protected override void Awake()
    {
        base.Awake();

        if (isRemote)
        {
            string host = EnvLoader.Get("MQTT_REMOTE_HOST");
            string portStr = EnvLoader.Get("MQTT_REMOTE_PORT");
            string user = EnvLoader.Get("MQTT_REMOTE_USERNAME");
            string pass = EnvLoader.Get("MQTT_REMOTE_PASSWORD");
            if (!string.IsNullOrEmpty(host)) brokerAddress = host;
            if (int.TryParse(portStr, out int port)) brokerPort = port;
            if (!string.IsNullOrEmpty(user)) mqttUserName = user;
            if (!string.IsNullOrEmpty(pass)) mqttPassword = pass;
        }

        _rtdUpdater = rtdUpdaterService as InterfaceRTDUpdater ?? throw new InvalidOperationException("rtdUpdaterService not assigned!");
        _textToSpeech = textToSpeechService as InterfaceTextToSpeech ?? throw new InvalidOperationException("textToSpeechService not assigned!");
        _buttonGUI = buttonGUIService as InterfaceButtonGUI ?? throw new InvalidOperationException("buttonGUIService not assigned!");
        _graphVisualizer = graphVisualizerService as InterfaceGraphVisualizer ?? throw new InvalidOperationException("graphVisualizerService not assigned!");
    }

    public void PublishInteraction(string interactionData)
    {
        if (client == null)
        {
            Debug.LogWarning("MQTT client is not connected! Cannot publish.");
            return;
        }
        client.Publish(topicInteractionData, System.Text.Encoding.UTF8.GetBytes(interactionData), MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE, false);
    }

    public void PublishChart(string chartData)
    {
        if (client == null)
        {
            Debug.LogError(" Cannot publish chart data: MQTT client is null");
            return;
        }
        client.Publish(topicChartData, System.Text.Encoding.UTF8.GetBytes(chartData), MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE, false);
    }

    public void SetEncrypted(bool isEncrypted)
    {
        this.isEncrypted = isEncrypted;
    }

    protected override void OnConnected()
    {
        base.OnConnected();
        isConnected = true;
        Connected?.Invoke();
    }

    protected override void OnConnectionFailed(string errorMessage)
    {
        Debug.Log("CONNECTION FAILED! " + errorMessage);
    }

    protected override void OnDisconnected()
    {
        Debug.Log("Disconnected.");
        isConnected = false;
    }

    protected override void OnConnectionLost()
    {
        Debug.Log("CONNECTION LOST!");
    }

    protected override void SubscribeTopics()
    {
        client.Subscribe(new string[] { topicSubscribe }, new byte[] { MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE });
    }

    protected override void UnsubscribeTopics()
    {
        client.Unsubscribe(new string[] { topicSubscribe });
    }

    protected override void DecodeMessage(string topic, byte[] message)
    {
        string payload = System.Text.Encoding.UTF8.GetString(message);
        MessageReceived(topic, payload);
    }

    private void OnDestroy()
    {
        Disconnect();
    }
}
