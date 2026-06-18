using System.Collections.Generic;
using UnityEngine;

public partial class LectureHallManager
{
    private void SpawnStudentsAtChairs(List<Vector3> chairPositions, Vector3 lookTarget)
    {
        bool mainMuradSpawned = false;
        float camY = Camera.main != null ? Camera.main.transform.position.y : 1.7f;

        foreach (Vector3 pos in chairPositions)
        {
            Vector3 chairForward = EstimateChairForward(pos);
            Quaternion rot = Quaternion.LookRotation(chairForward);

            Vector3 spawnPos = pos;
            spawnPos.y = spawnAtSeatSurface ? pos.y + sittingYOffset : FindFloorY(pos, camY) + sittingYOffset;
            Vector3 perChairLookTarget = spawnPos + chairForward * 2f;

            GameObject prefabToSpawn;
            AnimationClip variantClip = null;
            bool isMainMurad = false;
            //First spawn murad becauase he is the main character
            if (!mainMuradSpawned && currentConfig.mainMuradPrefab != null)
            {
                prefabToSpawn = currentConfig.mainMuradPrefab;
                mainMuradSpawned = true;
                isMainMurad = true;
            }
            else
            {
                StudentVariant variant = PickStudentVariant(currentConfig);
                if (variant == null)
                    continue;
                prefabToSpawn = variant.prefab;
                variantClip = variant.sittingClip;
            }

            if (prefabToSpawn == null)
                continue;

            Quaternion spawnRot = (isMainMurad && flipMuradFacing) ? rot * Quaternion.Euler(0f, 180f, 0f) : rot;
            Vector3 finalPos = isMainMurad ? spawnPos + spawnRot * muradSeatOffset : spawnPos;
            GameObject spawned = Instantiate(prefabToSpawn, finalPos, spawnRot);
            EnsureBlockerCollider(spawned);
            _spawnedNPCs.Add(spawned);

            if (isMainMurad)
            {
                _mainMuradInstance = spawned;

                MuradController muradAI = spawned.GetComponent<MuradController>();
                if (muradAI != null)
                    muradAI.enabled = false;

                var batchVoiceCtrl = spawned.GetComponent<VoiceAPIController>();
                if (batchVoiceCtrl != null)
                {
                    batchVoiceCtrl.enabled = false;
                    Debug.Log("Lecture Hall:  VoiceAPIController disabled in the lecture and will be enabled after it finishes");
                }

                var batchLipSync = spawned.GetComponentInChildren<LipSync.CustomLipSyncContext>(includeInactive: true);
                if (batchLipSync != null)
                {
                    batchLipSync.enabled = false;
                    Debug.Log("Lecture Hall:  CustomLipSyncContext disabled during lecture and will be enabled after it finishes");
                }

                foreach (var tmp in spawned.GetComponentsInChildren<TMPro.TMP_Text>(includeInactive: true))
                    tmp.text = "";
                foreach (Canvas c in spawned.GetComponentsInChildren<Canvas>(includeInactive: true))
                    c.enabled = false;

                Animator anim = spawned.GetComponentInChildren<Animator>();
                if (anim != null)
                {
                    anim.applyRootMotion = false;
                    anim.SetBool("IsStanding", false);
                    anim.SetBool("IsWalking", false);
                    anim.SetBool("IsSitting", true);
                }
            }
            else
            {
                InitSeatedStudent(spawned, currentConfig, perChairLookTarget, variantClip);
            }
        }
    }

    private void InitSeatedStudent(GameObject npc, ExperienceConfig config, Vector3 lookTarget, AnimationClip overrideClip = null)
    {
        if (npc == null)
            return;

        HistoricalNPCController ctrl = npc.GetComponent<HistoricalNPCController>();
        if (ctrl == null)
            ctrl = npc.AddComponent<HistoricalNPCController>();

        if (overrideClip != null)
            ctrl.idleClip = overrideClip;
        else if (ctrl.idleClip == null && config != null && config.studentSittingClip != null)
            ctrl.idleClip = config.studentSittingClip;

        if (ctrl.idleClip == null)
            Debug.LogWarning($"Lecture Hall:  no idle clip so murad will be T pose ");

        ctrl.Init(NPCRole.Student, lookTarget);
        var studentSMRs = npc.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true);
        Bounds expandedBounds = new Bounds(Vector3.zero, Vector3.one * 4f);
        foreach (var smr in studentSMRs)
        {
            smr.updateWhenOffscreen = true;
            smr.localBounds = expandedBounds;
        }
        if (studentSMRs.Length == 0)
            Debug.LogWarning($"Lecture Hall:  character has no skinned mesh renderer");

        var handRest = npc.GetComponent<NPCHandRest>();
        if (handRest != null)
        {
            handRest.enabled = false;
            Debug.Log($"Lecture Hall:  NPCHandRest disabled on seated student");
        }
        ctrl.StartSittingVariation();
    }

    private StudentVariant PickStudentVariant(ExperienceConfig config)
    {
        if (config == null)
            return null;

        StudentVariant[] variants = config.studentPrefabVariants;
        var candidates = new List<StudentVariant>();
        if (variants != null)
        {
            foreach (var choosenStudent in variants)
            {
                if (choosenStudent == null || choosenStudent.prefab == null)
                    continue;
                if (config.mainMuradPrefab != null && choosenStudent.prefab == config.mainMuradPrefab)
                {
                    Debug.LogWarning($"Lecture Hall:  can not choose murad as student");
                    continue;
                }
                candidates.Add(choosenStudent);
            }
        }

        if (candidates.Count == 0)
        {
            if (config.studentPrefab == null)
                return null;
            if (config.mainMuradPrefab != null && config.studentPrefab == config.mainMuradPrefab)
            {
                return null;
            }
            return new StudentVariant { prefab = config.studentPrefab, sittingClip = null };
        }

        if (_shuffledStudentVariants == null || _shuffledStudentVariants.Count == 0)
        {
            _shuffledStudentVariants = new List<StudentVariant>(candidates);
            for (int i = _shuffledStudentVariants.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (_shuffledStudentVariants[i], _shuffledStudentVariants[j]) = (_shuffledStudentVariants[j], _shuffledStudentVariants[i]);
            }
            _variantCursor = 0;
        }

        if (_variantCursor >= _shuffledStudentVariants.Count)
        {
            if (config.mainMuradPrefab != null)
            {
                Debug.Log("Lecture Hall:  Reserve chair for Murad");
                return null;
            }

            Debug.Log("Lecture Hall:  No duplication ");
            return null;
        }

        return _shuffledStudentVariants[_variantCursor++];
    }

    // This used to prevent murad from colliding with other students or doctor
    private void EnsureBlockerCollider(GameObject npc)
    {
        if (npc == null)
            return;
        if (npc.GetComponent<CapsuleCollider>() != null) return;

        var cap = npc.AddComponent<CapsuleCollider>();
        cap.center = new Vector3(0f, 0.9f, 0f);
        cap.radius = 0.30f;
        cap.height = 1.75f;
        cap.direction = 1;
    }

    private void SpawnDoctorAt(Vector3 pos, Vector3 facingTarget, ExperienceConfig config)
    {
        if (config.doctorPrefab == null)
        {
            Debug.LogWarning("Lecture Hall:  Doctor prefab is not assigned.");
            return;
        }

        Vector3 dir = facingTarget - pos;
        dir.y = 0f;
        Quaternion rot = dir.sqrMagnitude > 0.001f ? Quaternion.LookRotation(dir) : Quaternion.identity;

        GameObject doctor = Instantiate(config.doctorPrefab, pos, rot);
        doctor.name = "Amin";

        HistoricalNPCController ctrl = doctor.GetComponent<HistoricalNPCController>();
        if (ctrl != null)
        {
            if (config.doctorIdleClip != null)
                ctrl.idleClip = config.doctorIdleClip;
            if (config.doctorTalkingClip != null)
                ctrl.talkingClip = config.doctorTalkingClip;
            if (config.doctorStandingAfterLectureClip != null)
                ctrl.standingAfterLectureClip = config.doctorStandingAfterLectureClip;
            else if (config.doctorIdleClip != null && ctrl.standingAfterLectureClip == null)
                ctrl.standingAfterLectureClip = config.doctorIdleClip;

            ctrl.Init(NPCRole.Doctor, facingTarget);
        }

        foreach (var smr in doctor.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true))
            smr.updateWhenOffscreen = true;

        EnsureBlockerCollider(doctor);
        _spawnedNPCs.Add(doctor);
        Debug.Log($"Lecture Hall:  Doctor spawned.");

        // Make students look at the doctor
        int flipped = 0;
        foreach (var npc in _spawnedNPCs)
        {
            if (npc == null || npc == doctor)
                continue;
            var studentCtrl = npc.GetComponent<HistoricalNPCController>();
            if (studentCtrl == null || studentCtrl.Role != NPCRole.Student)
                continue;

            Vector3 toDoc = pos - npc.transform.position; toDoc.y = 0f;
            if (toDoc.sqrMagnitude > 0.001f)
            {
                Vector3 fwd = npc.transform.forward; fwd.y = 0f;
                if (Vector3.Dot(fwd.normalized, toDoc.normalized) < 0f)
                {
                    npc.transform.rotation *= Quaternion.Euler(0f, 180f, 0f);
                    flipped++;
                    Debug.Log($"Lecture Hall:  Orientation fixed");
                }
            }
            studentCtrl.SetHeadLookTarget(pos);
        }
    }

    //Fall back used in start lecture function
    private void SpawnStudents(Vector3 anchor, Vector3 forward, ExperienceConfig config, Vector3 lookAtTarget)
    {
        int rows = Mathf.Max(1, config.studentRows);
        int cols = Mathf.Max(1, config.studentsPerRow);
        Vector3 right = Vector3.Cross(Vector3.up, forward);
        float halfX = (cols - 1) * seatSpacingX * 0.5f;
        float halfZ = (rows - 1) * seatSpacingZ * 0.5f;

        bool mainMuradSpawned = false;

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                Vector3 offset = right * (-halfX + c * seatSpacingX) + forward * (-halfZ + r * seatSpacingZ);
                Vector3 pos = new Vector3(anchor.x + offset.x, anchor.y + sittingYOffset, anchor.z + offset.z);
                if (!mainMuradSpawned && config.mainMuradPrefab != null)
                {
                    Quaternion muradRot = flipMuradFacing ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
                    Vector3 muradPos = pos + muradRot * muradSeatOffset;

                    GameObject murad = Instantiate(config.mainMuradPrefab, muradPos, muradRot);
                    murad.name = "Murad";
                    mainMuradSpawned = true;
                    _mainMuradInstance = murad;
                    var muradAI = murad.GetComponent<MuradController>();
                    if (muradAI != null)
                        muradAI.enabled = false;

                    var voiceCtrl = murad.GetComponent<VoiceAPIController>();
                    if (voiceCtrl != null)
                        voiceCtrl.enabled = false;

                    var lipSync = murad.GetComponentInChildren<LipSync.CustomLipSyncContext>(true);
                    if (lipSync != null)
                    {
                        lipSync.enabled = false;
                        Debug.Log("Lecture Hall:  CustomLipSyncContext disabled and will be enabled after lecture finish");
                    }
                    Animator muradAnim = murad.GetComponentInChildren<Animator>();
                    if (muradAnim != null)
                    {
                        muradAnim.SetBool("IsStanding", false);
                        muradAnim.SetBool("IsWalking", false);
                        muradAnim.SetBool("IsSitting", true);
                    }

                    foreach (var smr in murad.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        smr.updateWhenOffscreen = true;

                    EnsureBlockerCollider(murad);
                    _spawnedNPCs.Add(murad);
                    continue;
                }
                StudentVariant variant = PickStudentVariant(config);
                if (variant == null || variant.prefab == null)
                {
                    Debug.Log($"Lecture Hall:  No duplicates.");
                    continue;
                }

                GameObject npc = Instantiate(variant.prefab, pos, Quaternion.identity);
                npc.name = $"Student_{r}_{c}";
                InitSeatedStudent(npc, config, lookAtTarget, variant.sittingClip);
                _spawnedNPCs.Add(npc);
            }
        }
    }
}
