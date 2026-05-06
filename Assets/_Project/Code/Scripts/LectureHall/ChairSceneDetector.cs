using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;

/// <summary>
/// Queries Meta Scene Understanding for chair/seating anchors via MRUK
/// (Meta Reality Utility Kit). MRUK does NOT require OVRSceneManager prefab
/// references — it reads room data directly from the device.
///
/// Prerequisites in your Unity scene:
///   • Add a GameObject with the MRUK component (from Meta XR SDK).
///   • Space Setup must have been completed on the headset.
///
/// Call RequestChairs() to receive up to maxChairs world positions.
/// onReady is always invoked — empty list triggers grid-spawn fallback.
/// </summary>
public class ChairSceneDetector : MonoBehaviour
{
    [Header("Settings")]
    [Tooltip("Maximum number of chairs to collect")]
    public int maxChairs = 8;

    [Tooltip("Seconds to wait for MRUK room data before giving up")]
    public float sceneLoadTimeout = 12f;

    [Tooltip("How often (seconds) to check whether room data is ready")]
    public float pollInterval = 0.5f;

    // ── state ─────────────────────────────────────────────────────
    private bool _resolved;
    private Action<List<Vector3>> _pendingCallback;

    // ── Unity ─────────────────────────────────────────────────────

    private void Start()
    {
        // Use Start (not Awake) so MRUK singleton has time to initialize first.
        if (MRUK.Instance != null)
        {
            MRUK.Instance.SceneLoadedEvent.AddListener(OnMRUKSceneLoaded);
            Debug.Log("[ChairDetector] Subscribed to MRUK.SceneLoadedEvent.");
        }
        else
        {
            Debug.LogWarning("[ChairDetector] MRUK.Instance is null in Start — " +
                             "make sure the MRUK GameObject is in the scene.");
        }
    }

    private void OnDestroy()
    {
        if (MRUK.Instance != null)
            MRUK.Instance.SceneLoadedEvent.RemoveListener(OnMRUKSceneLoaded);
    }

    // ── Public API ────────────────────────────────────────────────

    /// <summary>
    /// Requests chair world positions. onReady is always called.
    /// Safe to call multiple times (previous pending request is cancelled).
    /// </summary>
    public void RequestChairs(Action<List<Vector3>> onReady)
    {
        // Guard: ignore duplicate call that arrives within the same frame
        if (_pendingCallback != null && !_resolved)
        {
            Debug.LogWarning("[ChairDetector] RequestChairs called while already pending — ignoring duplicate.");
            return;
        }

        _resolved = false;
        _pendingCallback = onReady;
        StopAllCoroutines();

        // ── Immediate path: room already loaded ──────────────────
        if (MRUK.Instance != null && MRUK.Instance.GetCurrentRoom() != null)
        {
            Debug.Log("[ChairDetector] MRUK room already available — collecting now.");
            Resolve(CollectChairs());
            return;
        }

        // ── Poll path: wait for room to appear ───────────────────
        Debug.Log($"[ChairDetector] MRUK room not ready — " +
                  $"polling every {pollInterval}s (timeout {sceneLoadTimeout}s)…");
        StartCoroutine(PollCoroutine());
    }

    // ── MRUK event (catches the case where event fires after RequestChairs) ──

    private void OnMRUKSceneLoaded()
    {
        Debug.Log("[ChairDetector] MRUK SceneLoadedEvent fired.");
        if (!_resolved && _pendingCallback != null)
            Resolve(CollectChairs());
    }

    // ── Polling ───────────────────────────────────────────────────

    private IEnumerator PollCoroutine()
    {
        float elapsed = 0f;

        while (elapsed < sceneLoadTimeout)
        {
            yield return new WaitForSeconds(pollInterval);
            elapsed += pollInterval;

            if (MRUK.Instance != null && MRUK.Instance.GetCurrentRoom() != null)
            {
                Debug.Log($"[ChairDetector] MRUK room appeared after {elapsed:F1}s.");
                Resolve(CollectChairs());
                yield break;
            }

            Debug.Log($"[ChairDetector] Polling… {elapsed:F1}s — MRUK room not ready yet.");
        }

        Debug.LogWarning($"[ChairDetector] {sceneLoadTimeout}s timeout — MRUK room never became available.\n" +
                         "Check: (1) MRUK component exists in scene, " +
                         "(2) Space Setup was completed on the headset.\n" +
                         "Falling back to grid spawn.");
        Resolve(new List<Vector3>());
    }

    // ── Chair collection ──────────────────────────────────────────

    private List<Vector3> CollectChairs()
    {
        var positions = new List<Vector3>();

        MRUKRoom room = MRUK.Instance?.GetCurrentRoom();
        if (room == null)
        {
            Debug.LogWarning("[ChairDetector] CollectChairs: MRUK room is null.");
            return positions;
        }

        Debug.Log($"[ChairDetector] Scanning {room.Anchors.Count} MRUK anchor(s)…");

        // First pass: prefer COUCH (properly labelled chairs)
        foreach (MRUKAnchor anchor in room.Anchors)
        {
            Debug.Log($"[ChairDetector]   '{anchor.gameObject.name}' → " +
                      $"[{string.Join(", ", anchor.AnchorLabels)}]");

            if (anchor.HasLabel("COUCH"))
            {
                positions.Add(anchor.transform.position);
                Debug.Log($"[ChairDetector]   ✓ COUCH at {anchor.transform.position}");
                if (positions.Count >= maxChairs) break;
            }
        }

        // Second pass: if no COUCHes found, fall back to TABLE
        // (Meta Space Setup often labels lecture-hall chairs/desks as TABLE)
        if (positions.Count == 0)
        {
            Debug.LogWarning("[ChairDetector] No COUCH anchors — falling back to TABLE anchors. " +
                             "To fix permanently: redo Space Setup and label seating as 'Couch'.");
            foreach (MRUKAnchor anchor in room.Anchors)
            {
                if (anchor.HasLabel("TABLE"))
                {
                    positions.Add(anchor.transform.position);
                    Debug.Log($"[ChairDetector]   ✓ TABLE (used as seat) at {anchor.transform.position}");
                    if (positions.Count >= maxChairs) break;
                }
            }
        }

        Debug.Log(positions.Count > 0
            ? $"[ChairDetector] Collected {positions.Count} seat position(s) via MRUK."
            : "[ChairDetector] No seating anchors found — falling back to grid spawn.");

        return positions;
    }

    // ── Core ──────────────────────────────────────────────────────

    private void Resolve(List<Vector3> positions)
    {
        if (_resolved) return;
        _resolved = true;

        var cb = _pendingCallback;
        _pendingCallback = null;
        cb?.Invoke(positions);
    }
}
