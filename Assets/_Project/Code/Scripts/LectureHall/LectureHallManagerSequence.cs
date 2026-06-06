using System.Collections;
using UnityEngine;

public partial class LectureHallManager
{
    public void PlayDetectionAudio(AudioClip clip)
    {
        if (chairDetectionAudioSource == null || clip == null)
        {
            Debug.Log($"Lecture Hall: Chair detection audio is not assigned");
            return;
        }
        chairDetectionAudioSource.clip = clip;
        chairDetectionAudioSource.loop = true;
        chairDetectionAudioSource.Play();
        Debug.Log($"Lecture Hall: Chair detection audio started");
    }

    private IEnumerator RunLectureSequence(ExperienceConfig config)
    {
        if (chairDetectionAudioSource != null && chairDetectionAudioSource.isPlaying)
        {
            chairDetectionAudioSource.Stop();
            Debug.Log("Lecture Hall: chairDetection audio stopped");
        }
        yield return null;
        if (_mainMuradInstance != null && config?.muradSittingClip != null)
        {
            _sittingDriver = _mainMuradInstance.AddComponent<MuradSittingPoseDriver>();
            _sittingDriver.clip = config.muradSittingClip;

            var muradHNPC_cfg = _mainMuradInstance.GetComponent<HistoricalNPCController>();
            if (muradHNPC_cfg != null && muradHNPC_cfg.sittingClipVariants != null && muradHNPC_cfg.sittingClipVariants.Length > 0)
            {
                _sittingDriver.variantClips = muradHNPC_cfg.sittingClipVariants;
                _sittingDriver.switchInterval = muradHNPC_cfg.sittingSwitchInterval;
                _sittingDriver.StartSittingVariation();
            }

            foreach (var npc in _spawnedNPCs)
            {
                if (npc == null)
                    continue;
                var ctrl = npc.GetComponent<HistoricalNPCController>();
                if (ctrl != null && ctrl.Role == NPCRole.Doctor)
                {
                    _sittingDriver.headLookTarget = npc.transform.position + Vector3.up * 1.5f;
                    _sittingDriver.hasHeadLookTarget = true;
                    break;
                }
            }
            if (!_sittingDriver.hasHeadLookTarget)
                Debug.LogWarning("Lecture Hall:  doctor not found so murad can not activate look at logic");
        }
        else if (config?.muradSittingClip == null)
        {
            Debug.LogError("Lecture Hall: Murad sitting clip is null");
        }

        yield return new WaitForSeconds(1.0f);

        // Doctor switching to talking animation
        NotifyDoctorLecturing(true);

        if (config.lectureAudioClip != null)
        {
            if (lectureAudioSource != null)
            {
                lectureAudioSource.outputAudioMixerGroup = null;
                lectureAudioSource.spatialBlend = 0f;
                lectureAudioSource.volume = 1f;
                lectureAudioSource.mute = false;
                lectureAudioSource.loop = false;
                lectureAudioSource.clip = config.lectureAudioClip;
                lectureAudioSource.Play();
            }
            else
            {
                Debug.LogError("Lecture Hall: lectureAudioSource is NULL");
            }
            GameObject doctorNPC = null;
            AudioSource doctorLipSyncAudio = null;
            LipSync.CustomLipSyncContext doctorLipSyncCtx = null;

            foreach (var npc in _spawnedNPCs)
            {
                if (npc == null)
                    continue;
                var npcCtrl = npc.GetComponent<HistoricalNPCController>();
                if (npcCtrl != null && npcCtrl.Role == NPCRole.Doctor)
                {
                    doctorNPC = npc;
                    break;
                }
            }

            if (doctorNPC != null)
            {
                doctorLipSyncCtx = doctorNPC.GetComponentInChildren<LipSync.CustomLipSyncContext>(includeInactive: true);
                if (doctorLipSyncCtx != null)
                {
                    doctorLipSyncCtx.enabled = true;
                    doctorLipSyncCtx.FeedAudioClip(config.lectureAudioClip, config.lectureAudioTranscript);
                    doctorLipSyncAudio = doctorLipSyncCtx.GetComponent<AudioSource>();
                    if (doctorLipSyncAudio != null)
                    {
                        doctorLipSyncAudio.clip = config.lectureAudioClip;
                        doctorLipSyncAudio.loop = false;
                        doctorLipSyncAudio.mute = false;
                        doctorLipSyncAudio.volume = 0f;
                        doctorLipSyncAudio.spatialBlend = 0f;
                        doctorLipSyncAudio.Play();
                        Debug.Log("Lecture Hall:  Doctor CustomLipSyncContext started");
                    }
                    else
                    {
                        Debug.LogWarning("Lecture Hall:  No audio source so lipsync is not working");
                    }
                }
                else
                {
                    Debug.LogWarning("Lecture Hall:  CustomLipSyncContext is not assigned to doctor");
                }
            }
            else
            {
                Debug.LogWarning("Lecture Hall:  Doctor NPC not found");
            }
            if (lectureAudioSource != null)
                yield return new WaitForSeconds(config.lectureAudioClip.length);
            if (doctorLipSyncAudio != null)
                doctorLipSyncAudio.Stop();
            // Reset CustomLipSyncContext for doctor to close his mouth after finishing lecture
            if (doctorLipSyncCtx != null)
            {
                doctorLipSyncCtx.ResetVisemes();
                doctorLipSyncCtx.enabled = false;
                Debug.Log("Lecture Hall:  Reset CustomLipSyncContext for doctor");
            }
        }
        else
        {
            Debug.LogError("Lecture Hall: lecture audio is not assigned");
        }

        // standing animation assigned to the doctor
        NotifyDoctorLecturing(false);

        // trigger murad to stand and walk touser
        if (_mainMuradInstance != null)
        {
            StartCoroutine(MainMuradApproachUser());
        }
        else
        {
            if (ExperienceManager.Instance != null)
            {
                Debug.Log("Lecture Hall:  Lecture finished, handing control to ExperienceManager if murad is not found");
            }
        }
    }

    private static Quaternion FaceTowards(Vector3 fromPos, Vector3 lookAt)
    {
        Vector3 dir = lookAt - fromPos;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) return Quaternion.identity;
        return Quaternion.LookRotation(dir.normalized);
    }
    public float muradMinDistanceFromUser = 1.2f;

    private IEnumerator MainMuradApproachUser()
    {
        if (_sittingDriver != null)
        {
            Destroy(_sittingDriver);
            _sittingDriver = null;
            Debug.Log("Lecture Hall:  Stop sitting pose for murad and transfer to standing");
        }

        Animator[] allAnims = _mainMuradInstance.GetComponentsInChildren<Animator>(true);
        for (int i = 0; i < allAnims.Length; i++)
        {
            var a = allAnims[i];
        }

        Animator anim = null;
        foreach (var a in allAnims)
        {
            if (a.runtimeAnimatorController != null && a.parameterCount > 0)
            {
                anim = a;
                break;
            }
        }
        if (anim == null && allAnims.Length > 0)
            anim = allAnims[0];

        if (anim != null)
        {
            string pList = "";
            for (int i = 0; i < anim.parameterCount; i++)
                pList += anim.parameters[i].name + " ";
            Debug.Log($"Lecture Hall:  Animator parameters: [{pList.Trim()}]");
        }

        var muradHNPC = _mainMuradInstance.GetComponent<HistoricalNPCController>();
        if (muradHNPC != null)
        {
            Debug.Log($"Lecture Hall:  Found HistoricalNPCController on murad");
            muradHNPC.ClearHeadLookTarget();
            muradHNPC.StopPlayableGraph();
        }
        else
        {
            Debug.Log("Lecture Hall:  can not fond HistoricalNPCController on murad");
            var muradHNPCChild = _mainMuradInstance.GetComponentInChildren<HistoricalNPCController>(true);
            if (muradHNPCChild != null)
            {
                Debug.Log($"Lecture Hall:  Found HistoricalNPCController on child");
                muradHNPCChild.ClearHeadLookTarget();
                muradHNPCChild.StopPlayableGraph();
            }
            else
            {
                Debug.Log("Lecture Hall:  can not find  HistoricalNPCController ");
            }
        }
        //waiting for two frames
        yield return null;
        yield return null;

        if (anim != null)
        {
            anim.enabled = true;
            anim.applyRootMotion = false;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var before = anim.GetCurrentAnimatorStateInfo(0);
            anim.Play("Standing Idle", 0, 0f);
            anim.SetBool("IsSitting", false);
            anim.SetBool("IsStanding", true);
            anim.SetBool("IsWalking", false);
        }

        Transform camTransform = Camera.main.transform;
        float rotSpeed = 6f;

        // murad stand up
        float standTimer = 0f;
        while (standTimer < 2.0f)
        {
            standTimer += Time.deltaTime;
            if (_mainMuradInstance != null && camTransform != null)
            {
                Quaternion target = FaceTowards(_mainMuradInstance.transform.position, camTransform.position);
                _mainMuradInstance.transform.rotation = Quaternion.Slerp(_mainMuradInstance.transform.rotation, target, rotSpeed * Time.deltaTime);
            }
            yield return null;
        }

        // murad start walking
        if (anim != null)
        {
            var mid = anim.GetCurrentAnimatorStateInfo(0);
            anim.SetBool("IsStanding", false);
            anim.SetBool("IsWalking", true);
            anim.Play("Amin_Motion_Imported_Walk Relaxed_2Loop", 0, 0f);

            yield return null;  //wait one frame
            if (anim != null)
            {
                var wi = anim.GetCurrentAnimatorStateInfo(0);
                int expectedWalkHash = Animator.StringToHash("Amin_Motion_Imported_Walk Relaxed_2Loop");
                bool isWalkByHash = wi.shortNameHash == expectedWalkHash;
                bool isWalkByName = wi.IsName("Base Layer.Amin_Motion_Imported_Walk Relaxed_2Loop");
            }
        }

        // checking floor first
        float camY = camTransform != null ? camTransform.position.y : 1.7f;
        float floorY = FindFloorY(_mainMuradInstance.transform.position, camY);
        Vector3 startPos = _mainMuradInstance.transform.position;
        startPos.y = floorY;
        _mainMuradInstance.transform.position = startPos;

        // stoping in front of user
        Vector3 camFwdInit = camTransform.forward;
        camFwdInit.y = 0f;
        if (camFwdInit.sqrMagnitude > 0.001f)
            camFwdInit.Normalize();
        else
            camFwdInit = Vector3.forward;
        Vector3 targetPos = camTransform.position + camFwdInit * Mathf.Max(0.6f, muradMinDistanceFromUser);
        targetPos.y = floorY;

        // disable sitting 
        var sittingCol = _mainMuradInstance.GetComponent<CapsuleCollider>();
        if (sittingCol != null)
            sittingCol.enabled = false;

        CharacterController cc = _mainMuradInstance.GetComponent<CharacterController>();
        if (cc == null)
        {
            cc = _mainMuradInstance.AddComponent<CharacterController>();
            cc.center = new Vector3(0f, 0.9f, 0f);
            cc.radius = 0.28f;
            cc.height = 1.75f;
            cc.skinWidth = 0.04f;
            cc.stepOffset = 0.25f;
        }

        float walkSpeed = 1.2f;

        const float ARRIVE_TOLERANCE = 0.2f;
        while (true)
        {
            Vector3 camFwd = camTransform.forward;
            camFwd.y = 0f;
            camFwd = camFwd.sqrMagnitude > 0.001f ? camFwd.normalized : Vector3.forward;
            targetPos = camTransform.position + camFwd * muradMinDistanceFromUser;
            targetPos.y = floorY;
            float dx = _mainMuradInstance.transform.position.x - targetPos.x;
            float dz = _mainMuradInstance.transform.position.z - targetPos.z;
            float distToTarget = Mathf.Sqrt(dx * dx + dz * dz);

            if (distToTarget <= ARRIVE_TOLERANCE)
                break;

            Vector3 dir = new Vector3(-dx, 0f, -dz) / distToTarget;
            _mainMuradInstance.transform.rotation = Quaternion.Slerp(_mainMuradInstance.transform.rotation, Quaternion.LookRotation(dir), rotSpeed * Time.deltaTime);

            float step = Mathf.Min(walkSpeed * Time.deltaTime, distToTarget);
            Vector3 newPos = _mainMuradInstance.transform.position + dir * step;
            newPos.y = floorY;
            _mainMuradInstance.transform.position = newPos;

            yield return null;
        }

        // murad now standing infront of user, transfer to standing animation
        if (anim != null)
            anim.SetBool("IsWalking", false);

        float faceTimer = 0f;
        while (faceTimer < 1f)
        {
            faceTimer += Time.deltaTime;
            Quaternion target = FaceTowards(_mainMuradInstance.transform.position, camTransform.position);
            _mainMuradInstance.transform.rotation = Quaternion.Slerp(_mainMuradInstance.transform.rotation, target, rotSpeed * Time.deltaTime);
            yield return null;
        }

        // starting Q&A phase;
        MuradController muradQAAI = _mainMuradInstance?.GetComponent<MuradController>();
        if (muradQAAI != null)
            muradQAAI.enabled = true;
        if (_mainMuradInstance != null)
        {
            var qaLipSync = _mainMuradInstance.GetComponentInChildren<LipSync.CustomLipSyncContext>(includeInactive: true);
            if (qaLipSync != null)
            {
                if (!qaLipSync.gameObject.activeSelf)
                {
                    qaLipSync.gameObject.SetActive(true);
                    Debug.Log("Lecture Hall:  CustomLipSyncContext is now active for Q&A");
                }
                qaLipSync.enabled = true;
            }
            else
            {
                Debug.LogWarning("Lecture Hall:  CustomLipSyncContext not assigned to murad");
            }

            VoiceAPIController qaVoice = _mainMuradInstance.GetComponent<VoiceAPIController>();
            if (qaVoice != null)
            {
                foreach (Canvas c in _mainMuradInstance.GetComponentsInChildren<Canvas>(includeInactive: true))
                    c.enabled = true;

                qaVoice.enabled = true;
                Debug.Log("Lecture Hall:  VoiceAPIController is now enables for Q&A");
            }
        }

        if (greetingAudioClip != null && _mainMuradInstance != null)
        {
            VoiceAPIController voiceCtrl = _mainMuradInstance.GetComponent<VoiceAPIController>();
            if (voiceCtrl != null)
            {

                var lipCtx = voiceCtrl.customLipSyncContext ?? _mainMuradInstance.GetComponentInChildren<LipSync.CustomLipSyncContext>(includeInactive: true);
                if (lipCtx != null)
                {
                    lipCtx.FeedAudioClip(greetingAudioClip, greetingAudioTranscript);
                    Debug.Log("Lecture Hall:  Playing greeting audio Greeting");
                }
                else
                {
                    Debug.LogWarning("Lecture Hall:  No CustomLipSyncContext found so can not play greeting audio");
                }

                if (voiceCtrl.audioSource != null)
                {
                    voiceCtrl.audioSource.Stop();
                    voiceCtrl.audioSource.clip = greetingAudioClip;
                    voiceCtrl.audioSource.loop = false;
                    voiceCtrl.audioSource.mute = false;
                    voiceCtrl.audioSource.volume = 1f;
                    voiceCtrl.audioSource.spatialBlend = 1f;
                    voiceCtrl.audioSource.Play();
                }

                if (voiceCtrl.speakerSource != null)
                {
                    voiceCtrl.speakerSource.Stop();
                    voiceCtrl.speakerSource.clip = greetingAudioClip;
                    voiceCtrl.speakerSource.loop = false;
                    voiceCtrl.speakerSource.mute = false;
                    voiceCtrl.speakerSource.volume = 1f;
                    voiceCtrl.speakerSource.spatialBlend = 1f;
                    voiceCtrl.speakerSource.Play();
                }
                else
                {
                    if (voiceCtrl.audioSource != null)
                        voiceCtrl.audioSource.mute = false;
                }
            }
            else
            {
                AudioSource muradAudio = _mainMuradInstance.GetComponent<AudioSource>();
                if (muradAudio == null)
                    muradAudio = _mainMuradInstance.AddComponent<AudioSource>();

                muradAudio.spatialBlend = 1f;
                muradAudio.loop = false;
                muradAudio.mute = false;
                muradAudio.volume = 1f;
                muradAudio.clip = greetingAudioClip;
                muradAudio.Play();
            }
        }

        if (ExperienceManager.Instance != null)
        {
            Debug.Log("[LectureHallManager] Handing over to ExperienceManager.");
        }
    }

    private void NotifyDoctorLecturing(bool lecturing)
    {
        foreach (var npc in _spawnedNPCs)
        {
            if (npc == null)
                continue;
            var ctrl = npc.GetComponent<HistoricalNPCController>();
            if (ctrl != null && ctrl.Role == NPCRole.Doctor)
                ctrl.SetLecturing(lecturing);
        }
    }
}
