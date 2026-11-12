# Streams the microphone to OpenAI over the Realtime API (a WebSocket) and
# transcribes it with gpt-transcribe. This is NOT live transcription: the
# model transcribes each turn once it has ended (gpt-live-transcribe is the
# live, word-by-word model, and it does not support semantic_vad).
#
# Prints the finished utterance as {"transcript": ..., "words": []} (the same
# contract SpeechToText.cs reads from the other STT scripts), or nothing when
# no speech was heard. Errors go to stderr with exit code 1.
#
# End of speech is decided by OpenAI (semantic_vad), not locally: it keeps a
# mid-question pause in one transcript instead of cutting the question off.
#
# --push-to-talk: the key decides the turn instead. Unity writes one line to
# stdin when the key is released; audio after that is never sent, the rest of
# the turn is committed, and every segment heard while the key was held is
# joined into one transcript (a pause mid-question no longer ends it). The
# wake word and the Speech To Text button have no key, so they run without it.

from websockets.sync.client import connect
import threading
import pyaudio
import base64
import queue
import json
import time
import sys
import os

# Audio recording parameters (realtime transcription expects 24kHz pcm16)
RATE = 24000
CHUNK = int(RATE / 10)  # 100ms

NO_SPEECH_TIMEOUT = 8.0  # give up if nobody speaks
MAX_DURATION = 20.0      # force the transcript of a very long utterance
GIVE_UP = 30.0           # hard stop if no transcript ever arrives
RELEASE_WAIT = 5.0       # push to talk: wait this long for the last transcript after release

# Tags the commit sent on key release, so an error about it (nothing left to
# commit: semantic_vad already closed the turn) can be told from a real failure.
RELEASE_COMMIT_ID = "release_commit"

REALTIME_URL = "wss://api.openai.com/v1/realtime?intent=transcription"

# Transcription hints. The context describes who is speaking and the setting;
# the keywords are literal terms that may be spoken, spelled the way the
# manual spells them (the transcript copies the keyword's spelling). Keywords
# are hints, not required output, but each one is a chance for an unspoken
# word to appear, so keep the list to words that actually get misheard. A
# keyword must be one line with no < or > -- one invalid keyword rejects the
# whole session update.
STATIC_CONTEXT = (
    "A blind or low-vision person is talking to Graphy, a voice assistant for "
    "exploring charts by touch on a Dot Pad, a refreshable tactile display. "
    "They may ask questions about the data, ask to hide, show or highlight parts "
    "of the chart, or ask how to use the system, including its keys and braille "
    "row. They may refer to the part of the chart they are touching."
)
STATIC_KEYWORDS = [
    # requests
    "hide", "show", "filter", "highlight",
    # the device and display
    "Graphy", "Dot Pad", "braille", "braille row",
    "x-axis", "y-axis", "tick", "function key", "arrow key",
    # data words that get misheard
    "peak",
]

script_dir = os.path.dirname(os.path.abspath(__file__))
repo_root = os.path.join(script_dir, "..", "..", "..", "..")


def load_dotenv():
    """Load the repo-root .env (same format agent/config.py reads)."""
    env_path = os.path.join(repo_root, ".env")
    if not os.path.exists(env_path):
        return
    with open(env_path) as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                key, value = line.split("=", 1)
                os.environ.setdefault(key.strip(), value.strip())


def log(message, quiet):
    if not quiet:
        print(message, file=sys.stderr, flush=True)


class MicrophoneStream:
    """Opens a recording stream; chunks are queued from pyaudio's callback,
    so audio captured while the WebSocket is still connecting isn't lost."""

    def __init__(self, rate=RATE, chunk=CHUNK):
        self._rate = rate
        self._chunk = chunk
        self.buff = queue.Queue()

    def __enter__(self):
        self._audio_interface = pyaudio.PyAudio()
        self._audio_stream = self._audio_interface.open(
            format=pyaudio.paInt16,
            channels=1,
            rate=self._rate,
            input=True,
            frames_per_buffer=self._chunk,
            stream_callback=self._fill_buffer,
        )
        return self

    def __exit__(self, type, value, traceback):
        self._audio_stream.stop_stream()
        self._audio_stream.close()
        self._audio_interface.terminate()

    def _fill_buffer(self, in_data, frame_count, time_info, status_flags):
        self.buff.put(in_data)
        return None, pyaudio.paContinue


def send_chunk(ws, chunk):
    """Appends one mic chunk to the input buffer; False once disconnected."""
    try:
        ws.send(json.dumps({
            "type": "input_audio_buffer.append",
            "audio": base64.b64encode(chunk).decode(),
        }))
        return True
    except Exception:
        return False


def stream_audio(ws, mic, stop, released, drained):
    """Sends queued mic chunks (backlog first) until stopped or disconnected.

    Push to talk: once the key is released, sends what is still queued and
    stops, so nothing said after the release reaches the transcript, then sets
    `drained` so the turn can be committed behind the last chunk."""
    while not stop.is_set():
        if released.is_set():
            while True:
                try:
                    chunk = mic.buff.get_nowait()
                except queue.Empty:
                    break
                if not send_chunk(ws, chunk):
                    return
            drained.set()
            return
        try:
            chunk = mic.buff.get(timeout=0.1)
        except queue.Empty:
            continue
        if not send_chunk(ws, chunk):
            return


def wait_for_release(released):
    """Push to talk: Unity writes one line to stdin when the key is released.
    EOF counts too: Unity is gone, so nobody is holding the key."""
    sys.stdin.readline()
    released.set()


def session_config(model):
    return {
        "type": "session.update",
        "session": {
            "type": "transcription",
            "audio": {
                "input": {
                    "format": {"type": "audio/pcm", "rate": RATE},
                    "transcription": {
                        "model": model,
                        "language": "en",
                        "prompt": STATIC_CONTEXT,
                        "keywords": STATIC_KEYWORDS,
                    },
                    "turn_detection": {"type": "semantic_vad", "eagerness": "auto"},
                }
            },
        },
    }


def transcribe(quiet, push_to_talk=False):
    """Returns the transcript text, or None if no speech was heard."""
    load_dotenv()
    model = os.environ.get("OPENAI_TRANSCRIBE_MODEL") or "gpt-transcribe"
    api_key = os.environ.get("OPENAI_API_KEY")
    if not api_key:
        raise RuntimeError("OPENAI_API_KEY is not set in .env")

    start = time.time()
    speech_started = None
    speech_stopped = None
    committed = False
    outcome = "no speech detected"
    transcript = None
    transcript_at = None

    # Push to talk only. The release commit's outcome is "resolved" once the
    # server confirms it (committed) or refuses it (nothing left to commit);
    # the turn is over when it is resolved and every committed segment has
    # its transcript.
    released = threading.Event()
    drained = threading.Event()
    released_at = None
    release_sent = False
    release_resolved = False
    segment_ids = []    # committed segments, in order
    segments = {}       # segment id -> its transcript
    if push_to_talk:
        threading.Thread(target=wait_for_release, args=(released,), daemon=True).start()

    with MicrophoneStream(RATE, CHUNK) as mic:
        # Unity waits for this process to exit, and a polite close handshake
        # added ~2s after the transcript arrived, so don't wait on it.
        with connect(REALTIME_URL, additional_headers={"Authorization": f"Bearer {api_key}"},
                     close_timeout=0.1) as ws:
            ws.send(json.dumps(session_config(model)))
            log(f"Connected in {time.time() - start:.1f}s, streaming to {model}", quiet)

            stop = threading.Event()
            threading.Thread(target=stream_audio, args=(ws, mic, stop, released, drained),
                             daemon=True).start()

            try:
                while True:
                    elapsed = time.time() - start
                    if push_to_talk:
                        if elapsed >= MAX_DURATION:
                            released.set()   # still holding at the cap: end the turn here
                        if released.is_set() and released_at is None:
                            released_at = elapsed
                            log("Key released", quiet)
                        if drained.is_set() and not release_sent:
                            # Everything heard before the release has been
                            # sent: close the turn behind it.
                            ws.send(json.dumps({"type": "input_audio_buffer.commit",
                                                "event_id": RELEASE_COMMIT_ID}))
                            release_sent = True
                        if release_resolved and all(s in segments for s in segment_ids):
                            break
                        if released_at is not None and elapsed - released_at >= RELEASE_WAIT:
                            outcome = "gave up waiting after release"
                            break
                    if speech_started is None and released_at is None and elapsed >= NO_SPEECH_TIMEOUT:
                        break
                    if (not push_to_talk and speech_started is not None
                            and elapsed >= MAX_DURATION and not committed):
                        # Still talking at the cap: ask for the transcript now
                        ws.send(json.dumps({"type": "input_audio_buffer.commit"}))
                        committed = True
                    if elapsed >= GIVE_UP:
                        outcome = "gave up waiting for transcript"
                        break

                    try:
                        event = json.loads(ws.recv(timeout=0.2))
                    except TimeoutError:
                        continue

                    kind = event.get("type", "")
                    if kind == "input_audio_buffer.speech_started":
                        speech_started = speech_started or time.time() - start
                        log("Speech started", quiet)
                    elif kind == "input_audio_buffer.speech_stopped":
                        speech_stopped = time.time() - start
                        log("Speech stopped", quiet)
                    elif kind == "input_audio_buffer.committed" and push_to_talk:
                        segment_ids.append(event.get("item_id"))
                        if release_sent:
                            release_resolved = True
                    elif kind == "conversation.item.input_audio_transcription.completed":
                        text = (event.get("transcript") or "").strip()
                        if push_to_talk:
                            # Keep listening until the key is released.
                            segments[event.get("item_id")] = text
                            if text:
                                transcript_at = time.time() - start
                        elif text:
                            transcript = text
                            transcript_at = time.time() - start
                            outcome = "completed"
                            break
                        else:
                            # A blip (e.g. the start tone) produced no words: keep listening
                            log("Empty transcript, still listening", quiet)
                    elif kind == "error":
                        error = event.get("error", {})
                        if release_sent and (error.get("event_id") == RELEASE_COMMIT_ID or
                                             str(error.get("code", "")).startswith("input_audio_buffer")):
                            # Nothing left to commit: semantic_vad closed the
                            # turn just before the release.
                            log(f"Release commit refused: {error.get('message')}", quiet)
                            release_resolved = True
                        else:
                            raise RuntimeError(error.get("message", str(event)))
            finally:
                stop.set()

    if push_to_talk:
        transcript = " ".join(segments[s] for s in segment_ids if segments.get(s)) or None
        if transcript and outcome == "no speech detected":
            outcome = "completed"

    # Always reported (even with --quiet): Unity logs stderr, so an empty
    # result shows what happened.
    fmt = lambda t: f"{t:.1f}s" if t is not None else "-"
    print(f"STT {outcome}: speech {fmt(speech_started)}-{fmt(speech_stopped)}, "
          f"transcript at {fmt(transcript_at)}, finished at {time.time() - start:.1f}s",
          file=sys.stderr, flush=True)
    return transcript


def main(quiet=False, push_to_talk=False):
    transcript = transcribe(quiet, push_to_talk)
    if not transcript:
        return None

    # gpt-transcribe returns text only: no confidence, no word timestamps.
    result = {"transcript": transcript, "words": []}
    print(json.dumps(result, indent=2))
    return result


if __name__ == "__main__":
    try:
        main(quiet="--quiet" in sys.argv, push_to_talk="--push-to-talk" in sys.argv)
    except Exception as ex:
        print(f"{type(ex).__name__}: {ex}", file=sys.stderr)
        sys.exit(1)
