using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;

public class ExperienceManager : MonoBehaviour
{
    public static ExperienceManager Instance { get; private set; }

    [Header("Experience")]
    public ExperienceConfig experience;        // lecture hall script

    [Header("Lecture Hall")]
    public LectureHallManager lectureHallManager;       //lecture hall gameobject 

    [Header("Chair Detection")]
    public float mrukWaitTimeout = 10f;
    public float mrukFloorWaitTimeout = 5f;
    public ExperienceConfig ActiveConfig { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void Start()
    {

        if (experience == null)
        {
            Debug.LogError("[ExperienceManager] ExperienceConfig isn't assigned");
            return;
        }

        if (lectureHallManager == null)
        {
            Debug.LogError("[ExperienceManager] LectureHallManager isn't assigned!");
            return;
        }

        StartCoroutine(BeginLectureHallSequence(experience));
    }

    // Steps of lecture hall scene
    private IEnumerator BeginLectureHallSequence(ExperienceConfig config)
    {
        ActiveConfig = config;
        AudioSource src = lectureHallManager.lectureAudioSource;
        if (config.introAudioClip != null)
        {
            if (src != null)
            {
                src.loop = false;
                src.Stop();
                src.clip = config.introAudioClip;
                src.Play();
                Debug.Log($"[ExperienceManager] Starting introduction audio ");
                yield return new WaitForSeconds(config.introAudioClip.length);   // This is responsible for pausing the process until the intro audio finishes 
                src.Stop();
                Debug.Log("[ExperienceManager] Audio of introduction is finished");
            }
            else
            {
                Debug.LogWarning("[ExperienceManager] introAudioClip is assigned but the lectureAudioSource is null");
            }
        }

        // Show Message 1 and wait for it to finish before detection starts
        yield return StartCoroutine(lectureHallManager.ShowChairScanGuidanceAndWait());

        lectureHallManager.PlayDetectionAudio(config.chairDetectionAudioClip);
        Debug.Log($"[ExperienceManager] Waiting for MRUK to detect floor anchor");

        float floorY = float.MinValue;
        float waited = 0f;
        while (waited < mrukFloorWaitTimeout)
        {
            if (MRUK.Instance != null && MRUK.Instance.GetCurrentRoom() != null)
            {
                MRUKRoom room = MRUK.Instance.GetCurrentRoom();
                foreach (MRUKAnchor anchor in room.Anchors)
                {
                    if (anchor.HasLabel("FLOOR"))
                    {
                        floorY = anchor.transform.position.y;
                        Debug.Log($"[ExperienceManager] MRUK floor anchor found at Y={floorY:F2}.");
                        break;
                    }
                }
                if (floorY > float.MinValue) break;
            }
            yield return new WaitForSeconds(0.5f);
            waited = waited + 0.5f;
        }

        if (floorY <= float.MinValue)
        {
            floorY = Camera.main != null ? Camera.main.transform.position.y - 1.7f : 0f;
            Debug.LogWarning($"[ExperienceManager] No MRUK floor anchor");
        }

        Debug.Log("[ExperienceManager] Start scanning room for the chairs");
        List<Vector3> chairPositions = lectureHallManager.FindChairsByEnvironmentScan(floorY);

        if (chairPositions.Count == 0)
        {
            Debug.LogWarning("[ExperienceManager] No chairs detected");
            lectureHallManager.StartLecture(config, LectureCompleted);
        }
        else
        {
            Debug.Log($"[ExperienceManager] Number of chairs found: {chairPositions.Count}");
            lectureHallManager.StartLectureWithChairs(chairPositions, config, LectureCompleted);
        }
    }

    private void LectureCompleted()
    {
        Debug.Log("[ExperienceManager] Lecture complete.");
    }

    public void ReturnToMenu()
    {
        // Stop any in-progress BeginLectureHallSequence before starting a fresh one
        StopAllCoroutines();

        if (lectureHallManager != null)
        {
            lectureHallManager.ClearScene();
        }

        if (experience != null)
        {
            StartCoroutine(BeginLectureHallSequence(experience));
        }
    }
}