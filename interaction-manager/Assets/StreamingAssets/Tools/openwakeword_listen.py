# Listens for a wake word with openWakeWord and prints one JSON line per
# detection on stdout -- {"event": "detected", "name": ..., "score": ...} --
# for AgentWakeWord.cs, which starts this script at Play and keeps it running.
# {"event": "ready", "model": ...} is printed once the model is loaded.
#
# Unity controls it over stdin, one command per line:
#   pause   keep the mic open but stop detecting (while a request is being
#           transcribed, so the wake word can't interrupt it)
#   resume  detect again, ignoring the first moment so audio from before the
#           pause can't fire
# EOF on stdin means Unity is gone: exit, so no orphan keeps the mic open.
#
# --model is a pre-trained name ("hey_jarvis", "alexa", "hey_mycroft",
# "hey_rhasspy"; downloaded once with openwakeword.utils.download_models()) or
# the path of a trained .onnx file. macOS has no TFLite runtime, so inference
# always runs on ONNX. Errors go to stderr with exit code 1.

import argparse
import threading
import json
import time
import sys

import numpy as np
import pyaudio
from openwakeword.model import Model

RATE = 16000
FRAME = 1280         # 80 ms, the frame size openWakeWord is built around
DEBOUNCE = 1.5       # one utterance of the wake word gives one detection
RESUME_GRACE = 1.0   # after resume, ignore scores while old audio flushes out


def emit(**event):
    print(json.dumps(event), flush=True)


def read_commands(state, stop):
    """Applies stdin commands until EOF, which stops the listener."""
    for line in sys.stdin:
        command = line.strip().lower()
        if command == "pause":
            state["paused"] = True
        elif command == "resume":
            state["resumed_at"] = time.time()
            state["reset"] = True   # done on the main thread, between predictions
            state["paused"] = False
    stop.set()


def listen(model_name, threshold):
    model = Model(wakeword_models=[model_name], inference_framework="onnx")
    name = next(iter(model.models))   # a path loads under its file name

    audio = pyaudio.PyAudio()
    stream = audio.open(format=pyaudio.paInt16, channels=1, rate=RATE,
                        input=True, frames_per_buffer=FRAME)
    state = {"paused": False, "reset": False, "resumed_at": 0.0}
    stop = threading.Event()
    threading.Thread(target=read_commands, args=(state, stop), daemon=True).start()
    emit(event="ready", model=name)

    last_detection = 0.0
    try:
        while not stop.is_set():
            # Read even while paused, so the stream never backs up.
            frame = np.frombuffer(stream.read(FRAME, exception_on_overflow=False), dtype=np.int16)
            if state["paused"]:
                continue
            if state["reset"]:
                state["reset"] = False
                model.reset()
            score = model.predict(frame)[name]
            now = time.time()
            if (score >= threshold and now - last_detection >= DEBOUNCE
                    and now - state["resumed_at"] >= RESUME_GRACE):
                last_detection = now
                emit(event="detected", name=name, score=round(float(score), 3))
    finally:
        stream.stop_stream()
        stream.close()
        audio.terminate()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", default="hey_jarvis")
    parser.add_argument("--threshold", type=float, default=0.5)
    args = parser.parse_args()
    try:
        listen(args.model, args.threshold)
    except Exception as ex:
        print(f"{type(ex).__name__}: {ex}", file=sys.stderr)
        sys.exit(1)
