using System.Collections;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Meta.XR;
using Meta.XR.MRUtilityKit;

[RequireComponent(typeof(Animator))]
public class PlayAnimationOnStart : MonoBehaviour
{
    public AnimationClip clip;

    [Header("Floor")]
    [Tooltip("Trust the FloorLevel tracking origin: stand the character on the floor at 'Floor Y' " +
             "(0 with FloorLevel tracking + rig at world origin) instead of the MRUK/depth/cam-1.7 " +
             "guessing that was sinking everyone to mid-height.")]
    public bool useFloorLevelOrigin = true;
    [Tooltip("World Y of the real floor. Leave 0 for FloorLevel tracking; nudge if your rig isn't at world origin.")]
    public float floorY = 0f;

    private PlayableGraph _graph;
    private AnimationClipPlayable _playable;
    private EnvironmentRaycastManager _envRaycast;

    void Awake()
    {
        _envRaycast = FindAnyObjectByType<EnvironmentRaycastManager>();
    }

    void Start()
    {
        // Start the animation IMMEDIATELY so the character never shows its imported
        // T-pose / bind pose. The floor snap needs head-tracking / MRUK to be ready,
        // so it stays deferred — but it must NOT hold up the animation.
        PlayClip();
        StartCoroutine(SnapToFloorDelayed());
    }

    private void PlayClip()
    {
        if (clip == null)
        {
            Debug.LogWarning($"[PlayAnimationOnStart] No clip assigned on {gameObject.name}.");
            return;
        }

        _graph = PlayableGraph.Create(gameObject.name + "_AnimGraph");
        _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);

        var output = AnimationPlayableOutput.Create(_graph, "Animation", GetComponent<Animator>());
        _playable = AnimationClipPlayable.Create(_graph, clip);
        output.SetSourcePlayable(_playable);

        _graph.Play();
    }

    private IEnumerator SnapToFloorDelayed()
    {
        yield return new WaitForSeconds(1.2f);
        SnapToFloor();
    }

    private void SnapToFloor()
    {
        // FloorLevel tracking puts the real floor at a known plane (floorY, = 0), so trust
        // that instead of the MRUK/depth/cam-1.7 chain that was sinking everyone to mid-height.
        float floor = useFloorLevelOrigin
            ? floorY
            : FindFloorY(transform.position, Camera.main != null ? Camera.main.transform.position.y : 1.7f);

        // Place the character's FEET on the floor, not its pivot. CC4 pivots often sit at
        // the waist/centre, so snapping the pivot straight to the floor buries the lower
        // half. feetGap is how far the pivot sits above the lowest point of the mesh.
        float feetGap = transform.position.y - GetRenderersBottomY();

        Vector3 pos = transform.position;
        pos.y = floor + feetGap;
        transform.position = pos;

        Debug.Log($"[PlayAnimationOnStart] {gameObject.name} snapped — floor={floor:F2}, feetGap={feetGap:F2}");
    }

    // World-space Y of the lowest point of the character's meshes (≈ the feet).
    private float GetRenderersBottomY()
    {
        var rends = GetComponentsInChildren<Renderer>();
        float minY = float.MaxValue;
        foreach (var r in rends)
            if (r != null) minY = Mathf.Min(minY, r.bounds.min.y);
        return (minY == float.MaxValue) ? transform.position.y : minY;
    }

    private float FindFloorY(Vector3 xzPos, float cameraY)
    {
        // 1. MRUK FLOOR anchor — most accurate after world lock
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
                            Debug.Log($"[PlayAnimationOnStart] Floor Y from MRUK anchor: {y:F3}");
                            return y;
                        }
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[PlayAnimationOnStart] MRUK floor lookup failed: {ex.Message}");
        }

        // 2. EnvironmentRaycastManager — cast downward from just above estimated floor
        if (_envRaycast != null)
        {
            float estimatedFloor = cameraY - 1.7f;
            Vector3 origin = new Vector3(xzPos.x, estimatedFloor + 0.3f, xzPos.z);
            if (_envRaycast.Raycast(new Ray(origin, Vector3.down), out var hit, 0.8f) &&
                Vector3.Dot(hit.normal, Vector3.up) > 0.7f)
            {
                Debug.Log($"[PlayAnimationOnStart] Floor Y from env raycast: {hit.point.y:F3}");
                return hit.point.y;
            }
        }

        // 3. Fallback — camera height minus assumed eye height
        float fallback = cameraY - 1.7f;
        Debug.Log($"[PlayAnimationOnStart] Floor Y fallback (cam-1.7m): {fallback:F3}");
        return fallback;
    }

    void Update()
    {
        if (_playable.IsValid() && clip != null)
        {
            if (_playable.GetTime() >= (double)clip.length)
                _playable.SetTime(0.0);
        }
    }

    void OnDestroy()
    {
        if (_graph.IsValid())
            _graph.Destroy();
    }
}
