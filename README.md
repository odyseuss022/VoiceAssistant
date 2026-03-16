# VoiceClient (Vosk STT) for local LM Studio

This is a minimal C# console client that records from your microphone, sends audio to Vosk for speech-to-text, forwards recognized text to a local LM Studio HTTP endpoint, and plays the LLM response using Windows TTS.

Prerequisites
- Windows PC
- .NET 6.0 SDK (or later) installed

Edit appsettings.json to point to your LM Studio endpoint. Default is http://localhost:1234
