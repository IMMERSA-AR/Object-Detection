using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using TMPro;
using UnityEngine.Networking;
using System.Collections;

[RequireComponent(typeof(AudioSource))]
public class VoiceAPIController : MonoBehaviour
{
    [Header("WebSocket Settings")]
    public string wsUrl = "wss://immersa-voice-chat-api.up.railway.app/ws/voice-chat";

    [Header("API Parameters")]
    public string characterId = "s1";
    public string role = "mohandeskhana-student";

    [Header("UI & Animation Indicators")]
    public TMP_Text statusText;
    public Animator karimAnimator;

    [Header("OVR Lip Sync Integration")]
    [Tooltip("Drag the GameObject with OVRLipSyncContext here, or leave empty if it's on this same GameObject.")]
    public OVRLipSyncContext lipSyncContext;

    [Header("Quest AR/VR Interaction")]
    [Tooltip("Drag the RightControllerAnchor from your OVRCameraRig here")]
    public Transform rightController;

    [Tooltip("The collider on this NPC that the user will point at")]
    public Collider npcCollider;

    [Tooltip("Drag a LineRenderer component here to show the laser")]
    public LineRenderer laserPointer;

    private string micDevice;
    private AudioClip recordingClip;
    [Header("Audio Output")]
    public AudioSource audioSource; // Mourad's main source (for lips)
    public AudioSource speakerSource; // The new child source (for your ears)
    private bool isRecording = false;

    // WebSocket variables
    private ClientWebSocket websocket;
    private CancellationTokenSource cts;

    // Audio Queue for playing incoming mini-chunks smoothly
    private Queue<AudioClip> audioQueue = new Queue<AudioClip>();
    private readonly Queue<Action> mainThreadActions = new Queue<Action>();
    void Start()
    {

        // MUST be first
        audioSource = GetComponent<AudioSource>();
        audioSource.spatialBlend = 0f;
        audioSource.playOnAwake = false;
        audioSource.loop = false;
        audioSource.volume = 1f;
        AudioListener[] listeners = FindObjectsOfType<AudioListener>();
        Debug.Log($"🎧 Audio Listeners in scene: {listeners.Length}");
        foreach (var l in listeners)
            Debug.Log($"🎧 Listener on: {l.gameObject.name} | enabled={l.enabled}");
        Debug.Log($"🔊 AudioSource on: {gameObject.name} | enabled={audioSource.enabled}");
        Debug.Log($"🔊 AudioSource output: {(audioSource.outputAudioMixerGroup == null ? "No mixer - direct output" : audioSource.outputAudioMixerGroup.name)}");
        // Initialize laser renderer
        if (laserPointer != null)
        {
            laserPointer.positionCount = 2;
            laserPointer.startWidth = 0.005f;
            laserPointer.endWidth = 0.005f;
            laserPointer.SetPosition(0, Vector3.zero);
            laserPointer.SetPosition(1, Vector3.zero);
        }

        // Remove the PlayOneShot test sound line entirely

        if (lipSyncContext == null)
            lipSyncContext = GetComponent<OVRLipSyncContext>();

        if (lipSyncContext != null)
            lipSyncContext.audioSource = audioSource;
        else
            Debug.LogWarning("OVRLipSyncContext not found! Lip sync will not work.");

        if (Microphone.devices.Length > 0)
        {
            micDevice = Microphone.devices[0];
            Debug.Log("Microphone ready: " + micDevice);
        }
        else Debug.LogError("No microphone detected!");
    }

    void Update()
    {
        lock (mainThreadActions)
        {
            while (mainThreadActions.Count > 0)
                mainThreadActions.Dequeue().Invoke();
        }

        if (audioSource == null)

        {
            print("AudioSource is null! Cannot play audio.");
            return; // safety guard
        }


        if (audioQueue.Count > 0 && !audioSource.isPlaying)
        {
            AudioClip clip = audioQueue.Dequeue();
            Debug.Log($"▶️ Playing chunk | length={clip.length}s");

            // --- SOURCE 1: MOURAD'S LIPS (MUTED) ---
            // This feeds the audio to Meta's tollbooth to move the mouth.
            // We mute it so it doesn't accidentally output glitchy/silent audio.
            audioSource.Stop();
            audioSource.loop = false;
            audioSource.clip = clip;
            audioSource.mute = false;
            audioSource.volume = 1f;
            audioSource.Play();

            // --- SOURCE 2: THE ACTUAL AUDIO (UNMUTED) ---
            // This bypasses Meta entirely and goes straight to your headset.
            if (speakerSource != null)
            {
                speakerSource.Stop();
                speakerSource.loop = false;
                speakerSource.clip = clip;
                speakerSource.mute = false;
                speakerSource.spatialBlend = 1f; // Set to 1 so the voice comes FROM Mourad
                speakerSource.Play();
            }

            if (karimAnimator != null) karimAnimator.SetBool("IsTalking", true);
        }


        if (rightController != null)
        {
            bool hitNPC = false;
            Ray ray = new Ray(rightController.position, rightController.forward);
            Vector3 endPosition = rightController.position + rightController.forward * 5f;

            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                endPosition = hit.point;
                if (hit.collider == npcCollider)
                    hitNPC = true;
            }

            if (laserPointer != null)
            {
                laserPointer.SetPosition(0, rightController.position);
                laserPointer.SetPosition(1, endPosition);
                laserPointer.startColor = hitNPC ? Color.green : Color.red;
                laserPointer.endColor = hitNPC ? Color.green : Color.red;
            }

            if (OVRInput.GetDown(OVRInput.RawButton.RIndexTrigger) && hitNPC)
                ToggleRecording();
        }
    }
    public void ToggleRecording()
    {
        Debug.Log("SUCCESS: NPC was clicked!");

        if (!isRecording)
        {
            isRecording = true;

            // INSTANT UI UPDATE: Just like your old script
            if (statusText != null)
            {
                statusText.text = "Listening...";
                statusText.color = Color.red;
            }

            _ = RecordingSessionTask();
        }
        else
        {
            isRecording = false;

            // INSTANT UI UPDATE: User clicked again to stop
            if (statusText != null)
            {
                statusText.text = "Thinking...";
                statusText.color = Color.yellow;
            }
        }
    }
    private async Task RecordingSessionTask()
    {
        websocket = new ClientWebSocket();
        cts = new CancellationTokenSource();

        try
        {
            // --- 1. CONNECT ---
            Debug.Log("🔌 [1/5] Connecting to: " + wsUrl);
            UpdateUI("Connecting...", Color.yellow);
            await websocket.ConnectAsync(new Uri(wsUrl), cts.Token);
            Debug.Log("✅ [1/5] Connected!");

            string welcomeMsg = await ReceiveTextMsg();
            Debug.Log("📨 Welcome: " + welcomeMsg);

            // --- 2. START SESSION ---
            Debug.Log("🚀 [2/5] Sending start_session | character=" + characterId);
            SessionStartMsg startMsg = new SessionStartMsg { character_id = characterId };
            await SendTextMsg(JsonUtility.ToJson(startMsg));

            string startAck = await ReceiveTextMsg();
            Debug.Log("📨 Session ACK: " + startAck);

            // --- 3. SEND AUDIO CHUNKS ---
            Debug.Log("🎤 [3/5] Starting microphone recording...");
            UpdateUI("Listening...", Color.red);
            recordingClip = Microphone.Start(micDevice, true, 300, 16000);
            int lastPos = 0;
            int chunkIndex = 0;

            while (isRecording)
            {
                await Task.Delay(200);
                int currentPos = Microphone.GetPosition(micDevice);
                if (currentPos == lastPos) continue;

                float[] samples = GetMicSamples(lastPos, currentPos);
                lastPos = currentPos;

                byte[] pcmBytes = EncodeToPCM16(samples);
                string b64 = Convert.ToBase64String(pcmBytes);

                AudioChunkMsg chunkMsg = new AudioChunkMsg { chunk_index = chunkIndex, audio = b64 };
                await SendTextMsg(JsonUtility.ToJson(chunkMsg));
                Debug.Log($"🎙️ Sent chunk #{chunkIndex} | {pcmBytes.Length} bytes");

                string ack = await ReceiveTextMsg();
                Debug.Log($"📨 Chunk ACK: " + ack);
                chunkIndex++;
            }

            Microphone.End(micDevice);
            Debug.Log($"🛑 Microphone stopped. Total chunks sent: {chunkIndex}");

            // --- 4. END OF UTTERANCE ---
            Debug.Log("📤 [4/5] Sending end_of_utterance...");
            UpdateUI("Thinking...", Color.yellow);
            await SendTextMsg(JsonUtility.ToJson(new EndUtteranceMsg()));

            // --- 5. RECEIVE OUTPUTS ---
            Debug.Log("👂 [5/5] Waiting for server response...");
            int audioChunksReceived = 0;

            while (websocket.State == WebSocketState.Open)
            {
                Debug.Log($"⏳ Waiting for message... {DateTime.Now:HH:mm:ss.fff}");
                string msg = await ReceiveTextMsg();
                Debug.Log($"📨 Message completely downloaded at {DateTime.Now:HH:mm:ss.fff}");
                ServerMsg response = JsonUtility.FromJson<ServerMsg>(msg);
                Debug.Log($"📨 Server msg type='{response.type}' | raw={msg}");

                if (response.type == "error")
                {
                    Debug.LogError("❌ Server error: " + response.message);
                    break;
                }

                if (response.type == "tts_done")
                {
                    Debug.Log($"🏁 TTS done! Total audio chunks received: {audioChunksReceived}");
                    break;
                }

                if (response.type == "tts_audio_chunk")
                {
                    audioChunksReceived++;
                    if (string.IsNullOrEmpty(response.audio))
                    {
                        Debug.LogWarning($"⚠️ tts_audio_chunk #{response.chunk_index} has empty audio!");
                        continue;
                    }

                    byte[] audioBytes = Convert.FromBase64String(response.audio);
                    Debug.Log($"🔊 Audio chunk #{response.chunk_index} | {audioBytes.Length} bytes — queuing...");
                    UpdateUI("Speaking...", Color.green);

                    lock (mainThreadActions)
                    {
                        mainThreadActions.Enqueue(() => ProcessReceivedWav(audioBytes));
                    }
                }
                else
                {
                    Debug.Log($"ℹ️ Skipping msg type='{response.type}'");
                }
            }

            Debug.Log("🔒 Closing WebSocket...");
            await websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", cts.Token);
            UpdateUI("", Color.white);
            Debug.Log("✅ Session complete.");
        }
        catch (Exception e)
        {
            Debug.LogError("💥 WebSocket Error: " + e.Message);
            Debug.LogError("Stack: " + e.StackTrace);
            UpdateUI("Error!", Color.red);
        }
    }

    // --- HELPER METHODS ---

    private void ProcessReceivedWav(byte[] wavBytes)
    {
        // 1. Extract the raw audio floats safely
        float[] samples = Convert16BitWavToFloats(wavBytes);

        // 2. HARDCODE to the backend's format: 1 Channel (Mono) and 44100 Hz
        // This stops Unity from breaking if a chunk is missing its header
        AudioClip clip = AudioClip.Create("AI_Chunk", samples.Length, 1, 44100, false);
        clip.SetData(samples, 0);

        audioQueue.Enqueue(clip);
    }

    private float[] GetMicSamples(int lastPosition, int currentPosition)
    {
        int sampleCount = currentPosition > lastPosition ?
                          currentPosition - lastPosition :
                          (recordingClip.samples - lastPosition) + currentPosition;

        float[] samples = new float[sampleCount];
        recordingClip.GetData(samples, lastPosition);
        return samples;
    }
    private byte[] EncodeToPCM16(float[] samples)
    {
        byte[] bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short s = (short)Mathf.Clamp(samples[i] * short.MaxValue, short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)(s & 0xFF);
            bytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return bytes;
    }

    private float[] Convert16BitWavToFloats(byte[] wavBytes)
    {
        int dataIndex = 0;

        // Search for the "data" chunk marker to safely skip the WAV header
        for (int i = 0; i < wavBytes.Length - 4; i++)
        {
            if (wavBytes[i] == 'd' && wavBytes[i + 1] == 'a' && wavBytes[i + 2] == 't' && wavBytes[i + 3] == 'a')
            {
                dataIndex = i + 8;
                break;
            }
        }

        // Fallback: If "data" wasn't found but it starts with "RIFF", assume standard 44-byte header.
        // Otherwise, assume it's a raw chunk (dataIndex = 0).
        if (dataIndex == 0 && wavBytes.Length >= 4 && wavBytes[0] == 'R' && wavBytes[1] == 'I' && wavBytes[2] == 'F' && wavBytes[3] == 'F')
        {
            dataIndex = 44;
        }

        int sampleCount = (wavBytes.Length - dataIndex) / 2;
        float[] samples = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            // Convert the 16-bit WAV bytes back into Unity's float format (-1.0 to 1.0)
            samples[i] = BitConverter.ToInt16(wavBytes, dataIndex + i * 2) / 32768f;
        }

        return samples;
    }

    private async Task SendTextMsg(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await websocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
    }

    private async Task<string> ReceiveTextMsg()
    {
        // Increased from 8KB to 256KB to stop Unity from choking on massive Base64 audio strings
        var buffer = new ArraySegment<byte>(new byte[256 * 1024]);
        using (var ms = new MemoryStream())
        {
            WebSocketReceiveResult result;
            do
            {
                result = await websocket.ReceiveAsync(buffer, cts.Token);
                ms.Write(buffer.Array, buffer.Offset, result.Count);
            } while (!result.EndOfMessage);

            ms.Seek(0, SeekOrigin.Begin);
            using (var reader = new StreamReader(ms, Encoding.UTF8))
            {
                return await reader.ReadToEndAsync();
            }
        }
    }

    private void UpdateUI(string text, Color color)
    {
        lock (mainThreadActions)
        {
            mainThreadActions.Enqueue(() =>
            {
                if (statusText != null) { statusText.text = text; statusText.color = color; }
            });
        }
    }

    private void OnDestroy()
    {
        if (websocket != null && websocket.State == WebSocketState.Open)
        {
            cts.Cancel();
            websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Destroyed", CancellationToken.None);
        }
    }
}

// --- JSON DATA CLASSES ---
[Serializable]
public class SessionStartMsg
{
    public string type = "start_session";
    public string character_id;
    public int sample_rate = 16000;
    public string audio_format = "pcm16_base64_chunks";
}

[Serializable]
public class AudioChunkMsg
{
    public string type = "audio_chunk";
    public int chunk_index;
    public string audio;
}

[Serializable]
public class EndUtteranceMsg
{
    public string type = "end_of_utterance";
}

[Serializable]
public class ServerMsg
{
    public string type;        // "tts_audio_chunk", "tts_done", "error", 
                               // "partial_transcript", "final_transcript", "reply_text_done"
    public string audio;       // present on tts_audio_chunk
    public int chunk_index;
    public string text;        // present on transcripts / reply
    public string message;     // present on error/ack
}