using System.Collections;
using UnityEngine;
using Meta.XR;
using Meta.XR.MRUtilityKit;

/// <summary>
/// Manages the static Mourad scene:
///   1. Spawns the exact Mourad prefab in front of the AR camera.
///   2. Snaps his feet precisely to the real floor (invisible during settle, then revealed).
///   3. Plays the greeting audio through his OVRLipSync sources (lips move + audio heard).
///   4. Leaves him standing ready for voice Q&A when the audio finishes.
///
/// Setup in Unity:
///   - Create a new scene with your standard OVRCameraRig + OVRPassthroughLayer setup.
///   - Add an empty GameObject, attach this script.
///   - Assign mouradPrefab  → the same Mourad prefab used in the lecture hall.
///   - Assign greetingAudioClip → basic_audio_murad.mp3.
/// </summary>
public class StaticMuradManager : MonoBehaviour
{
    [Header("Mourad")]
    [Tooltip("The exact same Mourad prefab used in the lecture hall scene.")]
    public GameObject mouradPrefab;

    [Tooltip("How far in front of the camera Mourad spawns (metres).")]
    public float spawnDistance = 1.5f;

    [Header("Audio")]
    [Tooltip("The greeting audio clip (basic_audio_murad.mp3).")]
    public AudioClip greetingAudioClip;

    // ── Private ───────────────────────────────────────────────────
    private GameObject _mouradInstance;
    private EnvironmentRaycastManager _envRaycast;
    private float _detectedFloorY;   // saved so SnapFeetToFloor can use it

    private void Awake()
    {
        _envRaycast = FindAnyObjectByType<EnvironmentRaycastManager>();
    }

    private void Start()
    {
        // MRUK's World Lock shifts the tracking space (often ~0.4 m) when the room
        // loads. If we spawn before that shift, Murad ends up underground.
        // Solution: listen for RoomCreatedEvent — it fires AFTER the world lock is
        // applied, so our floor Y calculation is already in the correct space.

        if (MRUK.Instance != null)
        {
            MRUKRoom room = MRUK.Instance.GetCurrentRoom();
            if (room != null)
            {
                // Room already loaded (e.g. scene was reloaded) — spawn immediately.
                Debug.Log("[StaticMurad] MRUK room already loaded — spawning now.");
                StartCoroutine(SpawnThenPlay());
            }
            else
            {
                // Wait for MRUK to finish loading the room + applying world lock.
                Debug.Log("[StaticMurad] Waiting for MRUK RoomCreatedEvent...");
                MRUK.Instance.RoomCreatedEvent.AddListener(OnRoomCreated);
            }
        }
        else
        {
            // No MRUK in scene — spawn immediately with fallback floor detection.
            Debug.LogWarning("[StaticMurad] MRUK not found — spawning with fallback floor Y.");
            StartCoroutine(SpawnThenPlay());
        }
    }

    private void OnRoomCreated(MRUKRoom room)
    {
        MRUK.Instance.RoomCreatedEvent.RemoveListener(OnRoomCreated);
        Debug.Log("[StaticMurad] MRUK RoomCreated — waiting for world lock to settle...");
        StartCoroutine(SpawnThenPlay());
    }

    private IEnumerator SpawnThenPlay()
    {
        // World lock is applied ~500-700 ms AFTER RoomCreatedEvent fires.
        // Spawning before that puts Mourad at the pre-lock floor Y, then the
        // world shifts up making him float. Wait 1.2 s to be safe.
        yield return new WaitForSeconds(1.2f);

        Debug.Log("[StaticMurad] World lock settled — spawning Mourad.");
        SpawnMourad();

        if (_mouradInstance != null)
        {
            // Wait for animation to settle, then snap feet precisely to the floor.
            yield return StartCoroutine(SnapFeetToFloor());

            yield return StartCoroutine(PlayGreetingAudio());
        }

        // Mourad is now standing — VoiceAPIController on his prefab handles Q&A.
        Debug.Log("[StaticMurad] Ready for Q&A.");
    }

    private void OnDestroy()
    {
        // Clean up listener if scene is destroyed before room loads.
        if (MRUK.Instance != null)
            MRUK.Instance.RoomCreatedEvent.RemoveListener(OnRoomCreated);
    }

    // ── Spawn ─────────────────────────────────────────────────────

    private void SpawnMourad()
    {
        Transform cam = Camera.main?.transform;
        if (cam == null)
        {
            Debug.LogError("[StaticMurad] No Camera.main found. Make sure OVRCameraRig is in the scene.");
            return;
        }

        if (mouradPrefab == null)
        {
            Debug.LogError("[StaticMurad] mouradPrefab is not assigned in the Inspector.");
            return;
        }

        // ── Position: in front of camera at floor level ───────────
        Vector3 camFwd = cam.forward;
        camFwd.y = 0f;
        if (camFwd.sqrMagnitude < 0.001f) camFwd = Vector3.forward;
        camFwd.Normalize();

        Vector3 spawnXZ = cam.position + camFwd * spawnDistance;
        _detectedFloorY = FindFloorY(spawnXZ, cam.position.y);

        // Root at detected floor Y — SnapFeetToFloor fine-tunes after animation settles.
        Vector3 spawnPos = new Vector3(spawnXZ.x, _detectedFloorY, spawnXZ.z);

        // ── Rotation: Mourad faces the user ───────────────────────
        // The Mourad prefab's visible face is +Z (standard Reallusion).
        // LookRotation(-camFwd) makes +Z point back toward the camera.
        Quaternion spawnRot = Quaternion.LookRotation(-camFwd);

        _mouradInstance = Instantiate(mouradPrefab, spawnPos, spawnRot);
        _mouradInstance.name = "StaticMourad";

        // ── Force SkinnedMeshRenderers to always update bounds ────────
        // Without this, Unity returns stale T-pose bounds when Mourad is
        // outside the camera frustum and SnapFeetToFloor gets a wrong meshMinY.
        foreach (var smr in _mouradInstance.GetComponentsInChildren<SkinnedMeshRenderer>())
            smr.updateWhenOffscreen = true;

        // NOTE: Mourad is spawned VISIBLE (no SetMouradVisible(false)) because
        // Unity does not update SkinnedMeshRenderer.bounds for disabled renderers.
        // The SnapFeetToFloor coroutine applies a small (~2-5 cm) correction
        // after 15 frames — this is imperceptible at normal viewing distance.

        // ── Disable wandering AI ──────────────────────────────────
        MuradController muradAI = _mouradInstance.GetComponent<MuradController>();
        if (muradAI != null) muradAI.enabled = false;

        // ── Disable CharacterController (prevents physics from fighting Y) ──
        CharacterController cc = _mouradInstance.GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;

        // ── Set standing pose via Animator ────────────────────────
        Animator anim = _mouradInstance.GetComponentInChildren<Animator>();
        if (anim != null)
        {
            anim.applyRootMotion = false;
            anim.SetBool("IsTalking",  false);
            anim.SetBool("IsSitting",  false);
            anim.SetBool("IsWalking",  false);
            anim.SetBool("IsStanding", true);
            // Jump directly to Standing Idle so the pose is ready for bounds measurement.
            anim.Play("Standing Idle", 0, 0f);
            Debug.Log($"[StaticMurad] Animator: {anim.gameObject.name} — forced to Standing Idle.");
        }
        else
        {
            Debug.LogWarning("[StaticMurad] No Animator found on Mourad prefab!");
        }

        Debug.Log($"[StaticMurad] Mourad spawned (hidden) at Y={spawnPos.y:F3}  floor={_detectedFloorY:F3}  facing user.");
    }

    // ── Feet-to-floor snap ────────────────────────────────────────

    /// <summary>
    /// Waits 15 frames for the Standing Idle animation to fully evaluate,
    /// then reads the lowest mesh point (feet) and shifts the root so feet
    /// land exactly on the detected floor.
    ///
    /// IMPORTANT: Mourad is visible the whole time (not hidden) because Unity
    /// does NOT update SkinnedMeshRenderer.bounds for disabled renderers —
    /// hiding him would cause stale T-pose bounds and a zero correction.
    /// updateWhenOffscreen = true (set in SpawnMourad) ensures bounds update
    /// even if he is briefly outside the camera frustum.
    /// </summary>
    private IEnumerator SnapFeetToFloor()
    {
        if (_mouradInstance == null) yield break;

        // Give the Animator time to evaluate the first Standing Idle frames.
        // 15 frames at 72 fps ≈ 210 ms — enough for any blend/transition.
        for (int i = 0; i < 15; i++) yield return null;

        if (_mouradInstance == null) yield break;

        // ── Method A: SkinnedMeshRenderer bounds (most accurate) ─────
        // Bounds include the full shoe/foot mesh, not just the ankle bone.
        float meshMinY = float.MaxValue;
        foreach (var smr in _mouradInstance.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (smr.enabled && smr.bounds.min.y < meshMinY)
                meshMinY = smr.bounds.min.y;
        }

        // ── Method B: Foot bone positions (fallback if bounds are stale) ─
        // Bounds are considered stale when they equal the root Y (T-pose default).
        float rootY = _mouradInstance.transform.position.y;
        bool boundsStale = (meshMinY == float.MaxValue ||
                            Mathf.Abs(meshMinY - rootY) < 0.01f);

        if (boundsStale)
        {
            Debug.LogWarning("[StaticMurad] Bounds appear stale — falling back to foot bones.");
            Animator anim = _mouradInstance.GetComponentInChildren<Animator>();
            if (anim != null && anim.isHuman)
            {
                float boneMinY = float.MaxValue;
                HumanBodyBones[] footBones =
                {
                    HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
                    HumanBodyBones.LeftToes, HumanBodyBones.RightToes
                };
                foreach (var bone in footBones)
                {
                    Transform t = anim.GetBoneTransform(bone);
                    if (t != null) boneMinY = Mathf.Min(boneMinY, t.position.y);
                }
                if (boneMinY < float.MaxValue)
                {
                    meshMinY = boneMinY;
                    Debug.Log($"[StaticMurad] Foot bone Y used: {meshMinY:F3}");
                }
            }
        }

        // ── Apply correction ──────────────────────────────────────────
        if (meshMinY < float.MaxValue)
        {
            float correction = _detectedFloorY - meshMinY;
            Vector3 pos = _mouradInstance.transform.position;
            pos.y += correction;
            _mouradInstance.transform.position = pos;
            Debug.Log($"[StaticMurad] Feet snap: feetY={meshMinY:F3}  floorY={_detectedFloorY:F3}  " +
                      $"correction={correction:+0.000;-0.000}m  newRootY={pos.y:F3}");
        }
        else
        {
            Debug.LogWarning("[StaticMurad] Could not determine feet Y — Mourad may float.");
        }

        Debug.Log("[StaticMurad] Mourad on floor — ready for greeting.");
    }

    private void SetMouradVisible(bool visible)
    {
        if (_mouradInstance == null) return;
        foreach (var r in _mouradInstance.GetComponentsInChildren<Renderer>())
            r.enabled = visible;
    }

    // ── Audio ─────────────────────────────────────────────────────

    private IEnumerator PlayGreetingAudio()
    {
        if (greetingAudioClip == null)
        {
            Debug.LogWarning("[StaticMurad] greetingAudioClip not assigned — skipping audio.");
            yield break;
        }

        // ── Start talking animation ───────────────────────────────
        Animator anim = _mouradInstance.GetComponentInChildren<Animator>();
        if (anim != null)
        {
            anim.SetBool("IsTalking",  true);
            anim.SetBool("IsStanding", false);
        }

        // Mourad's prefab has VoiceAPIController with TWO AudioSources:
        //   audioSource   → lip-sync (OVRLipSyncContext watches this → lips move)
        //   speakerSource → child source that actually reaches the headset
        // Play on both so lips animate AND audio is heard.
        VoiceAPIController voiceCtrl = _mouradInstance.GetComponent<VoiceAPIController>();

        if (voiceCtrl != null)
        {
            // --- Lip source ---
            if (voiceCtrl.audioSource != null)
            {
                voiceCtrl.audioSource.Stop();
                voiceCtrl.audioSource.clip         = greetingAudioClip;
                voiceCtrl.audioSource.loop         = false;
                voiceCtrl.audioSource.mute         = false;
                voiceCtrl.audioSource.volume       = 1f;
                voiceCtrl.audioSource.spatialBlend = 1f;
                voiceCtrl.audioSource.Play();
            }

            // --- Speaker source ---
            if (voiceCtrl.speakerSource != null)
            {
                voiceCtrl.speakerSource.Stop();
                voiceCtrl.speakerSource.clip         = greetingAudioClip;
                voiceCtrl.speakerSource.loop         = false;
                voiceCtrl.speakerSource.mute         = false;
                voiceCtrl.speakerSource.volume       = 1f;
                voiceCtrl.speakerSource.spatialBlend = 1f;
                voiceCtrl.speakerSource.Play();
            }
            else if (voiceCtrl.audioSource != null)
            {
                voiceCtrl.audioSource.mute = false;
            }

            Debug.Log("[StaticMurad] Playing greeting audio via VoiceAPIController sources.");
        }
        else
        {
            // Fallback: no VoiceAPIController
            AudioSource src = _mouradInstance.GetComponent<AudioSource>()
                           ?? _mouradInstance.AddComponent<AudioSource>();
            src.clip         = greetingAudioClip;
            src.spatialBlend = 1f;
            src.loop         = false;
            src.mute         = false;
            src.volume       = 1f;
            src.Play();

            Debug.Log("[StaticMurad] Playing greeting audio via fallback AudioSource.");
        }

        // Wait for greeting to finish
        yield return new WaitForSeconds(greetingAudioClip.length);

        // ── Return to idle — StreamVoiceAPIController takes over IsTalking from here
        if (anim != null)
        {
            anim.SetBool("IsTalking",  false);
            anim.SetBool("IsStanding", true);
        }

        Debug.Log("[StaticMurad] Greeting audio finished — Mourad ready for Q&A.");
    }

    // ── Floor detection ───────────────────────────────────────────

    /// <summary>
    /// Returns the real floor Y under xzPos — same priority order as LectureHallManager
    /// (which correctly places the doctor on the floor):
    ///   1. MRUK FLOOR anchor — authoritative after world lock has been applied.
    ///   2. EnvironmentRaycastManager cast from just above estimated floor
    ///      (avoids hitting furniture or chair seats).
    ///   3. Camera height − 1.7 m — hard fallback.
    /// NOTE: The SnapFeetToFloor coroutine corrects any remaining centimetre-level
    /// error after the animation pose is evaluated, so this only needs to be close.
    /// </summary>
    private float FindFloorY(Vector3 xzPos, float cameraY)
    {
        // ── 1. MRUK FLOOR anchor ──────────────────────────────────
        try
        {
            if (MRUK.Instance != null)
            {
                MRUKRoom room = MRUK.Instance.GetCurrentRoom();
                if (room != null)
                {
                    foreach (MRUKAnchor anchor in room.Anchors)
                    {
                        if (anchor.HasLabel("FLOOR"))
                        {
                            float y = anchor.transform.position.y;
                            Debug.Log($"[StaticMurad] Floor Y from MRUK anchor: {y:F3}");
                            return y;
                        }
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[StaticMurad] MRUK floor lookup failed: {ex.Message}");
        }

        // ── 2. Env raycast from just above estimated floor ────────
        // Cast from 0.3 m above the estimated floor so we don't accidentally
        // hit a table or chair seat from above.
        if (_envRaycast != null)
        {
            float estimatedFloor = cameraY - 1.7f;
            Vector3 origin = new Vector3(xzPos.x, estimatedFloor + 0.3f, xzPos.z);
            if (_envRaycast.Raycast(new Ray(origin, Vector3.down), out var hit, 0.8f) &&
                Vector3.Dot(hit.normal, Vector3.up) > 0.7f)
            {
                Debug.Log($"[StaticMurad] Floor Y from env raycast: {hit.point.y:F3}");
                return hit.point.y;
            }
        }

        // ── 3. Hard fallback ──────────────────────────────────────
        float fallback = cameraY - 1.7f;
        Debug.Log($"[StaticMurad] Floor Y fallback (cam−1.7m): {fallback:F3}");
        return fallback;
    }

}
