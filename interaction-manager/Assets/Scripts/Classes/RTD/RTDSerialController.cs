using System;
using System.IO.Ports;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Handles low-level serial port communication with the DotPad device.
/// Manages connection, data transmission, and packet parsing.
/// </summary>
public class RTDSerialController
{
    // ===== Constants =====
    private const float SERIAL_CONNECT_TIMEOUT = 10f;

    // Packet framing
    private const byte SYNC_BYTE_1 = 0xAA;
    private const byte SYNC_BYTE_2 = 0x55;
    private const int HEADER_LENGTH = 4;
    private const byte CHECKSUM_SEED = 0xA5;

    // Command types
    private const byte CMD_ACK = 0x02;
    private const byte CMD_BUTTON = 0x03;

    // ACK subtypes
    private const byte ACK_SUBTYPE_LINE = 0x01;

    // ACK result codes
    private const byte ACK_RESULT_OK = 0x00;
    private const byte ACK_RESULT_NACK = 0x01;
    private const byte ACK_RESULT_CHECKSUM_ERROR = 0x02;

    // ===== Configuration =====
    public string PortPath { get; set; } = "";
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public Parity Parity { get; set; } = Parity.None;
    public StopBits StopBits { get; set; } = StopBits.One;
    public int Timeout { get; set; } = 5000;

    /// <summary>
    /// When true, connection attempts are gated behind Arm(). The controller will not
    /// try to open the port until Arm() is explicitly called.
    /// </summary>
    public bool RequireArmBeforeConnect { get; set; } = true;

    // ===== State =====
    private SerialPort _serialPort;
    private readonly List<byte> _receiveBuffer = new List<byte>();
    private bool _serialArmed = false;
    private float _serialConnectStartTime;
    private bool _gaveUpOnSerial = false;

    // ===== Events =====
    public event Action<byte[]> ButtonPacketReceived = delegate { };
    public event Action<int> LineAckReceived = delegate { };

    // ===== Properties =====
    public bool IsConnected => _serialPort != null && _serialPort.IsOpen;
    public bool HasGivenUp => _gaveUpOnSerial;
    public bool IsArmed => _serialArmed;

    // ===== Public API =====

    /// <summary>
    /// Enable connection attempts. Call this when the controller is ready to connect.
    /// </summary>
    public void Arm()
    {
        _serialArmed = true;
        _gaveUpOnSerial = false;
        _serialConnectStartTime = 0f;
        EnsureSerialPortOpen();
    }

    /// <summary>
    /// Disable connection attempts and close port.
    /// </summary>
    public void Disarm()
    {
        _receiveBuffer.Clear();
        _serialArmed = false;
        EnsureSerialPortClosed();
    }

    /// <summary>
    /// Send raw bytes to the serial port.
    /// </summary>
    public void SendBytes(byte[] data)
    {
        if (RequireArmBeforeConnect && !_serialArmed)
        {
            Debug.LogWarning("[Serial] SendBytes called before armed; send dropped.");
            return;
        }

        EnsureSerialPortOpen();

        if (_serialPort != null && _serialPort.IsOpen)
        {
            try
            {
                _serialPort.Write(data, 0, data.Length);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Serial] Send failed: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Call this from Unity Update() to handle connection attempts and incoming data.
    /// </summary>
    public void Update()
    {
        EnsureSerialPortOpen();
        ProcessIncomingData();
    }

    // ===== Internal Methods =====

    private void EnsureSerialPortOpen()
    {
        if (RequireArmBeforeConnect && !_serialArmed)
            return;

        if (string.IsNullOrEmpty(PortPath))
        {
            Debug.LogWarning("[Serial] PortPath not configured — skipping connection. Set SERIAL_PORT_PATH in .env.");
            _gaveUpOnSerial = true;
            return;
        }

        // If port is already open, nothing to do
        if (_serialPort != null && _serialPort.IsOpen)
            return;

        if (_gaveUpOnSerial)
            return;

        // start timer on first attempt
        if (_serialConnectStartTime <= 0f)
            _serialConnectStartTime = Time.realtimeSinceStartup;

        // stop trying after 10s
        if (Time.realtimeSinceStartup - _serialConnectStartTime > SERIAL_CONNECT_TIMEOUT)
        {
            Debug.LogError("Failed to connect to serial within 10s. Stopping attempts.");
            _gaveUpOnSerial = true;
            return;
        }

        try
        {
            if (_serialPort == null)
            {
                _serialPort = new SerialPort(PortPath, BaudRate, Parity, DataBits, StopBits)
                {
                    ReadTimeout = Timeout,
                    WriteTimeout = Timeout,
                    DtrEnable = true,
                    RtsEnable = true,
                    Handshake = Handshake.None
                };
            }
            if (!_serialPort.IsOpen)
            {
                _serialPort.Open();
                Debug.Log("Serial port opened.");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Failed to open serial port: {e.Message}");
        }
    }

    private void EnsureSerialPortClosed()
    {
        try
        {
            if (_serialPort != null)
            {
                try
                {
                    _serialPort.DiscardInBuffer();
                    _serialPort.DiscardOutBuffer();
                }
                catch
                {
                    // Port may already be in a bad state; ignore discard errors.
                }

                if (_serialPort.IsOpen)
                    _serialPort.Close();

                _serialPort.Dispose();
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Error closing serial port: {e.Message}");
        }
        finally
        {
            _serialPort = null;
        }
    }

    private void ProcessIncomingData()
    {
        if (_serialPort != null && _serialPort.IsOpen && _serialPort.BytesToRead > 0)
        {
            int count = _serialPort.BytesToRead;
            byte[] raw = new byte[count];
            int actualRead = _serialPort.Read(raw, 0, count);

            _receiveBuffer.AddRange(new ArraySegment<byte>(raw, 0, actualRead));
            OnSerialDataReceived();
        }
    }

    private void OnSerialDataReceived()
    {
        while (true)
        {
            if (_receiveBuffer.Count < HEADER_LENGTH) break;

            // Align to sync bytes
            if (!(_receiveBuffer[0] == SYNC_BYTE_1 && _receiveBuffer[1] == SYNC_BYTE_2))
            {
                _receiveBuffer.RemoveAt(0);
                continue;
            }

            int len = _receiveBuffer[3];
            if (_receiveBuffer.Count < HEADER_LENGTH + len)
                break; // wait for full packet

            byte[] packet = _receiveBuffer.GetRange(0, HEADER_LENGTH + len).ToArray();
            _receiveBuffer.RemoveRange(0, HEADER_LENGTH + len);

            if (!ChecksumOk(packet))
            {
                Debug.LogWarning($"Bad checksum (len={len}). Dropping packet.");
                continue;
            }

            byte cmdType = packet[5];

            if (cmdType == CMD_ACK)
            {
                if (len >= 6 && packet[6] == ACK_SUBTYPE_LINE)
                {
                    int ackedLine = packet[4];
                    byte result = packet[7];

                    if (result == ACK_RESULT_OK)
                        LineAckReceived?.Invoke(ackedLine);
                    else if (result == ACK_RESULT_NACK)
                        Debug.LogWarning($">>> Line-NACK for {ackedLine}");
                    else if (result == ACK_RESULT_CHECKSUM_ERROR)
                        Debug.LogError($">>> Line {ackedLine} checksum error");
                }
            }
            else if (cmdType == CMD_BUTTON)
            {
                ButtonPacketReceived?.Invoke(packet);
            }
        }
    }

    private bool ChecksumOk(byte[] packet)
    {
        int len = packet[3];
        byte cs = CHECKSUM_SEED;
        for (int i = HEADER_LENGTH; i < HEADER_LENGTH + len - 1; i++) cs ^= packet[i];
        return cs == packet[HEADER_LENGTH + len - 1];
    }
}
