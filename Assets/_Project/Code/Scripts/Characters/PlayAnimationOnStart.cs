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
    public bool useFloorLevelOrigin = true;
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
        float floor = useFloorLevelOrigin ? floorY : FindFloorY(transform.position, Camera.main != null ? Camera.main.transform.position.y : 1.7f);
        float feetGap = transform.position.y - GetRenderersBottomY();

        Vector3 pos = transform.position;
        pos.y = floor + feetGap;
        transform.position = pos;

        Debug.Log($"[PlayAnimationOnStart] {gameObject.name} snapped — floor={floor:F2}, feetGap={feetGap:F2}");
    }

    private float GetRenderersBottomY()
    {
        var rends = GetComponentsInChildren<Renderer>();
        float minY = float.MaxValue;

        foreach (var r in rends)
        {
            if (r != null)
            {
                minY = Mathf.Min(minY, r.bounds.min.y);
            }
        }

        return (minY == float.MaxValue) ? transform.position.y : minY;
    }

    private float FindFloorY(Vector3 xzPos, float cameraY)
    {
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

        if (_envRaycast != null)
        {
            float estimatedFloor = cameraY - 1.7f;
            Vector3 origin = new Vector3(xzPos.x, estimatedFloor + 0.3f, xzPos.z);
            if (_envRaycast.Raycast(new Ray(origin, Vector3.down), out var hit, 0.8f) && Vector3.Dot(hit.normal, Vector3.up) > 0.7f)
            {
                Debug.Log($"[PlayAnimationOnStart] Floor Y from env raycast: {hit.point.y:F3}");
                return hit.point.y;
            }
        }

        float fallback = cameraY - 1.7f;
        Debug.Log($"[PlayAnimationOnStart] Floor Y fallback (cam-1.7m): {fallback:F3}");
        return fallback;
    }

    void Update()
    {
        if (_playable.IsValid() && clip != null)
        {
            if (_playable.GetTime() >= (double)clip.length)
            {
                _playable.SetTime(0.0);
            }
        }
    }

    void OnDestroy()
    {
        if (_graph.IsValid())
        {
            _graph.Destroy();
        }
    }
}
