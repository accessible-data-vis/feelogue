# Based on Google Cloud Text-to-Speech synthesis sample
# Copyright 2018 Google LLC
# Licensed under the Apache License, Version 2.0
# https://github.com/GoogleCloudPlatform/python-docs-samples/blob/main/texttospeech/snippets/synthesize_text.py

from google.cloud import texttospeech
import pyaudio
import os
import os.path
import wave
from pygame import mixer
import sys
import argparse

# Load Google Cloud credentials
dir_path = os.path.dirname(os.path.abspath(__file__))
project_root = os.path.join(dir_path, "..", "..", "..")
creds = os.path.join(project_root, "key-service-account-google.json")
os.environ["GOOGLE_APPLICATION_CREDENTIALS"] = creds

mixer.init(buffer=1024)


def main():
    # Parse command-line arguments
    parser = argparse.ArgumentParser(description="Google Cloud Text-to-Speech")
    parser.add_argument("text", help="Text to synthesize")
    parser.add_argument("--voice-name", default="", help="Specific voice name")
    parser.add_argument("--language", default="en-AU", help="Language code")
    parser.add_argument(
        "--gender", default="NEUTRAL", help="Voice gender: MALE, FEMALE, NEUTRAL"
    )
    parser.add_argument(
        "--speed", type=float, default=1.0, help="Speaking rate (0.25 to 4.0)"
    )
    parser.add_argument(
        "--pitch", type=float, default=0.0, help="Pitch adjustment (-20.0 to 20.0)"
    )
    args = parser.parse_args()

    # Instantiates a client
    client = texttospeech.TextToSpeechClient()

    # Detect if text contains SSML tags
    text_input = args.text.strip()
    if text_input.startswith("<speak>"):
        synthesis_input = texttospeech.SynthesisInput(ssml=text_input)
    else:
        synthesis_input = texttospeech.SynthesisInput(text=text_input)

    # Voice selection
    if args.voice_name:
        voice = texttospeech.VoiceSelectionParams(
            name=args.voice_name, language_code=args.language
        )
    else:
        gender_map = {
            "MALE": texttospeech.SsmlVoiceGender.MALE,
            "FEMALE": texttospeech.SsmlVoiceGender.FEMALE,
            "NEUTRAL": texttospeech.SsmlVoiceGender.NEUTRAL,
        }
        voice = texttospeech.VoiceSelectionParams(
            language_code=args.language,
            ssml_gender=gender_map.get(
                args.gender.upper(), texttospeech.SsmlVoiceGender.NEUTRAL
            ),
        )

    # Select the type of audio file you want returned
    audio_config = texttospeech.AudioConfig(
        audio_encoding=texttospeech.AudioEncoding.LINEAR16,
        speaking_rate=args.speed,
        pitch=args.pitch,
    )

    # Perform the text-to-speech request on the text input with the selected
    # voice parameters and audio file type
    response = client.synthesize_speech(
        input=synthesis_input, voice=voice, audio_config=audio_config
    )

    output_file = os.path.join(project_root, "Temp", "output.wav")
    with open(output_file, "wb") as out:
        out.write(response.audio_content)

    print(output_file)


if __name__ == "__main__":
    main()
