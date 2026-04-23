using System;
using System.Collections.Generic;
using UnityEngine;

public class RTDCommandQueue
{
    private Queue<QueuedCommand> _queue = new Queue<QueuedCommand>();
    private QueuedCommand _currentCommand = null;
    private bool _waitingForAck = false;

    public int QueuedCount => _queue.Count;
    public bool IsWaitingForAck => _waitingForAck;
    public int CurrentCommandLine => _currentCommand?.lineNumber ?? -1;

    private class QueuedCommand
    {
        public byte[] packet;
        public int lineNumber;
        public int retryCount;
        public int maxAttempts; // total sends allowed (1 original + maxAttempts-1 retries)
        public Action onComplete;
        public Action onFailure;
        public bool isTailCommand;

        public QueuedCommand(byte[] pkt, int line, int maxAttempts, Action complete, Action failure, bool isTail = false)
        {
            packet = pkt;
            lineNumber = line;
            retryCount = 0;
            this.maxAttempts = maxAttempts;
            onComplete = complete;
            onFailure = failure;
            isTailCommand = isTail;
        }
    }

    public void Enqueue(byte[] packet, int lineNumber, int maxAttempts = 3, Action onComplete = null, Action onFailure = null, bool isTailCommand = false)
    {
        var cmd = new QueuedCommand(packet, lineNumber, maxAttempts, onComplete, onFailure, isTailCommand);
        _queue.Enqueue(cmd);
    }

    public void Clear()
    {
        _queue.Clear();
        _currentCommand = null;
        _waitingForAck = false;
    }

    /// <summary>
    /// Removes pending tail commands from the queue.
    /// Note: if the currently in-flight command is a tail command it is NOT cancelled here;
    /// it will complete or fail normally.
    /// </summary>
    public void ClearTailCommands()
    {
        var temp = new Queue<QueuedCommand>();
        while (_queue.Count > 0)
        {
            var cmd = _queue.Dequeue();
            if (!cmd.isTailCommand)
                temp.Enqueue(cmd);
        }
        _queue = temp;
    }

    public bool TryGetNextCommand(out byte[] packet, out int lineNumber)
    {
        packet = null;
        lineNumber = -1;

        if (_waitingForAck || _queue.Count == 0)
            return false;

        _currentCommand = _queue.Dequeue();
        packet = _currentCommand.packet;
        lineNumber = _currentCommand.lineNumber;
        _waitingForAck = true;

        return true;
    }

    public void HandleAck(int ackedLine)
    {
        if (!_waitingForAck || _currentCommand == null)
        {
            Debug.Log($"[Queue] Stale ACK for line {ackedLine} (not waiting)");
            return;
        }

        // Device does not reliably echo the sent line number back, so accept any ACK as completion.
        _currentCommand.onComplete?.Invoke();
        _currentCommand = null;
        _waitingForAck = false;
    }

    public bool HandleTimeout(out byte[] retryPacket, out int retryLine)
    {
        retryPacket = null;
        retryLine = -1;

        if (_currentCommand == null)
            return false;

        _currentCommand.retryCount++;

        if (_currentCommand.retryCount < _currentCommand.maxAttempts)
        {
            Debug.Log($"[Queue] Retry {_currentCommand.retryCount}/{_currentCommand.maxAttempts} for line {_currentCommand.lineNumber}");
            retryPacket = _currentCommand.packet;
            retryLine = _currentCommand.lineNumber;
            return true;
        }
        else
        {
            Debug.LogError($"[Queue] Command failed after {_currentCommand.maxAttempts} attempts: line {_currentCommand.lineNumber}");
            _currentCommand.onFailure?.Invoke();
            _currentCommand = null;
            _waitingForAck = false;
            return false;
        }
    }
}
