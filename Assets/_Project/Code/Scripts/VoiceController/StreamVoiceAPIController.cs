using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using TMPro;

[RequireComponent(typeof(AudioSource))]
public class VoiceAPIController : MonoBehaviour
{
    [Header("WebSocket Settings")]
    public string wsUrl = "wss://immersa-voice-chat-api.up.railway.app/ws/voice-chat";

    [Header("API Parameters")]
    public string characterId = "s1";

    [Header("UI & Animation Indicators")]
    public TMP_Text statusText;
    public Animator karimAnimator;

    [Header("Custom Lip Sync Integration")]
    [Tooltip("Drag the GameObject that has CustomLipSyncContext attached (usually the NPC root).")]
    public LipSync.CustomLipSyncContext customLipSyncContext;

    [Header("Emotion")]
    [Tooltip("Drag the GameObject that has EmotionController attached (usually the NPC face mesh root).")]
    public EmotionController emotionController;

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
    public AudioSource audioSource; // Mourad's source (drives both the sound and the lip-sync)
    private bool isRecording = false;
    private bool _wasSpeaking = false;  // 3ashan we fire Neutral only once when he stops talking

    private ClientWebSocket websocket;
    private CancellationTokenSource cts;
    private bool _sessionActive = false;

    // we queue the incoming audio chunks and play them one by one
    private Queue<AudioClip> audioQueue = new Queue<AudioClip>();
    private readonly Queue<Action> mainThreadActions = new Queue<Action>();

    void Start()
    {
        if (statusText == null)
        {
            statusText = GetComponentInChildren<TMP_Text>();
            if (statusText != null)
            {
                statusText.text = "Hello!";
                statusText.color = Color.white;
            }
            else
            {
                Debug.LogError("VoiceAPI: Could not find any TextMeshPro on Murad!");
            }
        }
        if (rightController == null)
        {
            GameObject rightHand = GameObject.Find("RightControllerAnchor");
            if (rightHand != null)
                rightController = rightHand.transform;
            else
                Debug.LogError("VoiceAPI: Could not find the RightControllerAnchor in the scene!");
        }
        //grab the audio source first
        audioSource = GetComponent<AudioSource>();
        audioSource.spatialBlend = 0f;
        audioSource.playOnAwake = false;
        audioSource.loop = false;
        audioSource.volume = 1f;
        // small audio debug to know the listener/source are alive
        AudioListener[] listeners = FindObjectsOfType<AudioListener>();
        Debug.Log($"Audio Listeners in scene: {listeners.Length}");
        foreach (var l in listeners)
            Debug.Log($"Listener on: {l.gameObject.name} | enabled={l.enabled}");
        Debug.Log($"AudioSource on: {gameObject.name} | enabled={audioSource.enabled}");

        // check the ray refs got assigned (kant moshkila abl kda)
        Debug.Log($"[RAY] rightController = {(rightController == null ? "NULL" : rightController.name)}");
        Debug.Log($"[RAY] laserPointer = {(laserPointer == null ? "NULL" : laserPointer.name)}");
        Debug.Log($"[RAY] npcCollider = {(npcCollider == null ? "NULL" : npcCollider.name)}");

        // set up the laser line
        if (laserPointer != null)
        {
            laserPointer.positionCount = 2;
            laserPointer.startWidth = 0.02f;   // wide enough to see in AR
            laserPointer.endWidth = 0.02f;
            laserPointer.SetPosition(0, Vector3.zero);
            laserPointer.SetPosition(1, Vector3.zero);
        }

        // auto find the lip sync context if forgot to drag it
        if (customLipSyncContext == null)
            customLipSyncContext = GetComponent<LipSync.CustomLipSyncContext>();
        if (customLipSyncContext == null)
            Debug.LogWarning("[VoiceAPI] CustomLipSyncContext not found! Drag the NPC root into the Inspector.");

        // same for the emotion controller
        if (emotionController == null)
            emotionController = GetComponentInChildren<EmotionController>();
        if (emotionController == null)
            Debug.LogWarning("[VoiceAPI] EmotionController not found! Add it to Mourad's face mesh.");

        if (Microphone.devices.Length > 0)
        {
            micDevice = Microphone.devices[0];
            Debug.Log("Microphone ready: " + micDevice);
        }
        else Debug.LogError("No microphone detected!");

        _ = ConnectAndStartSession();
    }

    private async Task ConnectAndStartSession()
    {
        // clean up any old socket or session lingering from a previous turn
        try
        {
            try { websocket?.Abort(); websocket?.Dispose(); } catch { }
            try { cts?.Cancel(); cts?.Dispose(); } catch { }

            websocket = new ClientWebSocket();
            cts = new CancellationTokenSource();
            _sessionActive = false;

            Debug.Log("Connecting to: " + wsUrl);
            UpdateUI("Connecting...", Color.yellow);
            await websocket.ConnectAsync(new Uri(wsUrl), cts.Token);
            Debug.Log("Connected!");

            string welcomeMsg = await ReceiveTextMsg();
            Debug.Log("Welcome: " + welcomeMsg);

            SessionStartMsg startMsg = new SessionStartMsg { character_id = characterId };
            await SendTextMsg(JsonUtility.ToJson(startMsg));

            string startAck = await ReceiveTextMsg();
            Debug.Log("Session ACK: " + startAck);

            _sessionActive = true;
            UpdateUI("Hello!", Color.white);
            Debug.Log("Session ready, the socket stays open between turns");
        }
        catch (Exception e)
        {
            Debug.LogError("Connection failed: " + e.Message);
            UpdateUI("Can't connect. Check Wi-Fi & try again!", Color.red);
        }
    }

    void Update()
    {
        // run anything that was queued from the network thread on the main thread
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

        // reset the face to Neutral once when he finishes talking (not every frame)
        bool isSpeaking = audioQueue.Count > 0 || audioSource.isPlaying;
        if (_wasSpeaking && !isSpeaking)
        {
            if (karimAnimator != null) karimAnimator.SetBool("IsTalking", false);
            emotionController?.PlayEmotion("Neutral");
        }
        _wasSpeaking = isSpeaking;

        if (audioQueue.Count > 0 && !audioSource.isPlaying)
        {
            AudioClip clip = audioQueue.Dequeue();
            Debug.Log($"Playing chunk | length={clip.length}s");

            // so the lips can never drift from what you hear
            audioSource.Stop();
            audioSource.loop = false;
            audioSource.clip = clip;
            audioSource.mute = false;
            audioSource.volume = 1f;
            audioSource.spatialBlend = 1f;   // we can change this to be 3D spatial sound.
            audioSource.Play();

            if (karimAnimator != null)
            {
                karimAnimator.SetBool("IsTalking", true);
                karimAnimator.SetBool("IsThinking", false);
            }
        }

        // point-and-click: shoot a ray from the controller, color it green on Mourad
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
        Debug.Log("NPC was clicked");

        if (!isRecording)
        {
            isRecording = true;
            if (statusText != null)
            {
                statusText.text = "Listening...";
                statusText.color = Color.red;
            }
            _ = RecordingSessionTask();
        }
        else
        {
            // clicked again to stop recording
            isRecording = false;
            if (statusText != null)
            {
                statusText.text = "Thinking...";
                statusText.color = Color.yellow;
            }
        }
    }

    private async Task RecordingSessionTask()
    {
        try
        {
            // reconnect if the server dropped us between turns
            if (websocket == null || websocket.State != WebSocketState.Open)
            {
                Debug.LogWarning("WebSocket not open, reconnecting (context will reset)...");
                await ConnectAndStartSession();
                if (!_sessionActive) return;
            }

            // start recording and stream the mic in small chunks
            Debug.Log("Starting microphone recording...");
            UpdateUI("Listening...", Color.red);

            // if an old session left the mic running, stop it first
            if (Microphone.IsRecording(micDevice)) Microphone.End(micDevice);
            recordingClip = Microphone.Start(micDevice, true, 300, 16000);
            if (recordingClip == null)
            {
                Debug.LogError("Microphone.Start returned null, mic unavailable.");
                UpdateUI("Mic problem. Try again!", Color.red);
                isRecording = false;
                return;
            }

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
                Debug.Log($"Sent chunk #{chunkIndex} | {pcmBytes.Length} bytes");

                string ack = await ReceiveTextMsg();
                Debug.Log("Chunk ACK: " + ack);
                chunkIndex++;
            }

            Microphone.End(micDevice);
            Debug.Log($"Microphone stopped. Total chunks sent: {chunkIndex}");

            // tell the server we are done talking
            Debug.Log("Sending end_of_utterance...");
            UpdateUI("Thinking...", Color.yellow);
            if (karimAnimator != null) karimAnimator.SetBool("IsThinking", true);
            await SendTextMsg(JsonUtility.ToJson(new EndUtteranceMsg()));

            // now recieve the reply (text + emotion + audio chunks)
            Debug.Log("Waiting for server response...");
            int audioChunksReceived = 0;

            while (websocket.State == WebSocketState.Open)
            {
                string msg = await ReceiveTextMsg();
                ServerMsg response = JsonUtility.FromJson<ServerMsg>(msg);
                Debug.Log($"Server msg type='{response.type}' | raw={msg}");

                if (response.type == "error")
                {
                    Debug.LogError("Server error: " + response.message);
                    UpdateUI("I didn't catch that. Try again!", Color.red);
                    break;
                }

                if (response.type == "reply_text_done")
                {
                    Debug.Log($"Reply: \"{response.text}\" | emotion: {response.emotion}");

                    // start a text-guided utterance: build the viseme sequence and reset
                    // the streaming cursor BEFORE the audio chunks arrive
                    string replyText = response.text;
                    lock (mainThreadActions)
                        mainThreadActions.Enqueue(() => customLipSyncContext?.BeginUtterance(replyText));

                    if (!string.IsNullOrEmpty(response.emotion))
                    {
                        string emotion = response.emotion;
                        lock (mainThreadActions)
                            mainThreadActions.Enqueue(() => emotionController?.PlayEmotion(emotion));
                    }
                    continue;
                }

                if (response.type == "tts_done")
                {
                    Debug.Log($"TTS done. Chunks received: {audioChunksReceived}.");
                    UpdateUI("Hello!", Color.white);
                    // clear the sequence so stray audio before the next reply falls back to raw MFCC
                    // instead of parking on this reply's leftover visemes
                    lock (mainThreadActions)
                        mainThreadActions.Enqueue(() => customLipSyncContext?.EndUtterance());
                    break; // keep the socket open so the context is kept for next turn
                }

                if (response.type == "tts_audio_chunk")
                {
                    audioChunksReceived++;
                    if (string.IsNullOrEmpty(response.audio))
                    {
                        Debug.LogWarning($"tts_audio_chunk #{response.chunk_index} has empty audio!");
                        continue;
                    }

                    byte[] audioBytes = Convert.FromBase64String(response.audio);
                    Debug.Log($"Audio chunk #{response.chunk_index} | {audioBytes.Length} bytes, queuing...");
                    UpdateUI("Speaking...", Color.green);

                    lock (mainThreadActions)
                        mainThreadActions.Enqueue(() => ProcessReceivedWav(audioBytes));
                }
                else
                {
                    Debug.Log($"Skipping msg type='{response.type}'");
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError("WebSocket Error: " + e.Message);
            Debug.LogError("Stack: " + e.StackTrace);
            UpdateUI("Something went wrong. Point at me & try again!", Color.red);

            // full cleanup so the next trigger press reconnects cleanly
            _sessionActive = false;
            isRecording = false;                          // so one press starts fresh (not toggles off)
            try { Microphone.End(micDevice); } catch { }  // free the mic from the dead session
            try { websocket?.Abort(); } catch { }         // kill the faulted socket
            try { websocket?.Dispose(); } catch { }
            websocket = null;                             // forces a reconnect next time
        }
    }

    // turn one received wav chunk into an AudioClip, build its visemes, and queue it
    private void ProcessReceivedWav(byte[] wavBytes)
    {
        float[] samples = Convert16BitWavToFloats(wavBytes);

        // hardcode the backend format: mono, 44100 Hz.
        AudioClip clip = AudioClip.Create("AI_Chunk", samples.Length, 1, 44100, false);
        clip.SetData(samples, 0);

        // build the viseme timeline BEFORE queuing so it is ready when the clip plays.
        customLipSyncContext?.FeedAudioClip(clip);

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
        // float (-1..1) -> 16 bit pcm bytes
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

        // find the "data" marker so we skip the wav header safely
        for (int i = 0; i < wavBytes.Length - 4; i++)
        {
            if (wavBytes[i] == 'd' && wavBytes[i + 1] == 'a' && wavBytes[i + 2] == 't' && wavBytes[i + 3] == 'a')
            {
                dataIndex = i + 8;
                break;
            }
        }

        // if no "data" found but it starts with RIFF, assume a normal 44 byte header.
        // otherwise treat it as a raw chunk (dataIndex = 0)
        if (dataIndex == 0 && wavBytes.Length >= 4 && wavBytes[0] == 'R' && wavBytes[1] == 'I' && wavBytes[2] == 'F' && wavBytes[3] == 'F')
            dataIndex = 44;

        int sampleCount = (wavBytes.Length - dataIndex) / 2;
        float[] samples = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
            samples[i] = BitConverter.ToInt16(wavBytes, dataIndex + i * 2) / 32768f;  // back to unity float

        return samples;
    }

    private async Task SendTextMsg(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await websocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
    }

    private async Task<string> ReceiveTextMsg()
    {
        // 256KB buffer, the base64 audio strings are big so 8KB wasn't enough
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

    // status text is updated from the network thread, so push it to the main thread
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

// JSON message classes for the websocket
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
    public string type;        // tts_audio_chunk, tts_done, error, reply_text_done ...
    public string audio;       // present on tts_audio_chunk
    public int chunk_index;
    public string text;        // present on transcripts / reply
    public string message;     // present on error/ack
    public string emotion;     // present on reply_text_done: happy, sad, angry, etc.
}
