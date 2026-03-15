# VoiceClient (Vosk STT) for local LM Studio

This is a minimal C# console client that records from your microphone, sends audio to Vosk for speech-to-text, forwards recognized text to a local LM Studio HTTP endpoint, and plays the LLM response using Windows TTS.

Prerequisites
- Windows PC
- .NET 6.0 SDK (or later) installed
- A Vosk model downloaded and unzipped (e.g., small-en-us model). See https://alphacephei.com/vosk/models






































- Adjust the LM request/response format to match your LM Studio instance exactly (send me a sample API response).- Change to automatic silence detection.- Add a config file to store defaults.If you want, I can:- This is a simple push-to-talk loop (start/stop via Enter). For continuous or VAD-based operation, further engineering is needed.- Vosk model quality and size affects accuracy. Use a 16kHz-compatible model.- The program guesses a default LM API path of `/api/v1/generate` if you only provide a bare host:port. If your LM Studio uses a different path (for example `/v1/chat/completions`), provide the full URL when prompted.Notes and tuning- Press Enter to start recording, speak, then press Enter again to stop. The recognized text is shown, sent to the LM, and the spoken response is played via Windows TTS.- When prompted, enter the LM endpoint (press Enter to use `http://127.0.0.1:1234`) and the path to the Vosk model folder.```dotnet run --project TestBot.csproj```bash- Run the app:- Start LM Studio and ensure its HTTP API is reachable.Running```dotnet build```bash2. Build:```dotnet add package System.Speechdotnet add package Voskdotnet add package NAudiocd VoiceClient```bash1. Open a terminal in this folder and add the NuGet packages:Setupn- LM Studio running locally (you indicated `http://127.0.0.1:1234`)