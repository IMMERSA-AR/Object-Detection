using System.Collections;
using UnityEngine;
using Meta.XR;
using System;
using System.Collections.Generic;

public class ObjectDetector : MonoBehaviour
{
    [Header("Environment Sampling")]
    [SerializeField] private Unity.InferenceEngine.ModelAsset sentisModel;
    [SerializeField] private Unity.InferenceEngine.BackendType backend = Unity.InferenceEngine.BackendType.CPU;
    [SerializeField] private float inferenceInterval = 0.1f;
    [SerializeField] private int kLayersPerFrame = 20;

    private PassthroughCameraAccess _cameraAccess;
    private Unity.InferenceEngine.Model _model;
    private Unity.InferenceEngine.Worker _engine;
    private ObjectRenderer _objectRenderer;
    private Coroutine _inferenceCoroutine;
    private Texture _cameraTexture;
    private const int InputSize = 640;
    private int totalFramesProcessed = 0;

    private void Start()
    {
        _cameraAccess = GetComponent<PassthroughCameraAccess>();
        _objectRenderer = GetComponent<ObjectRenderer>();

        if (!_cameraAccess || !_objectRenderer)
        {
            Debug.LogError("[ObjectDetector] PassthroughCameraAccess or Object Renderer not found in the scene.");
            return;
        }

        LoadModel();
        _inferenceCoroutine = StartCoroutine(InferenceLoop());
    }

    private void OnDestroy()
    {
        if (_inferenceCoroutine != null)
        {
            StopCoroutine(_inferenceCoroutine);
            _inferenceCoroutine = null;
        }

        _engine?.Dispose();
    }

    private void LoadModel()
    {
        try
        {
            _model = Unity.InferenceEngine.ModelLoader.Load(sentisModel);
            _engine = new Unity.InferenceEngine.Worker(_model, backend);
            Debug.Log($"Model loaded successfully on {backend} backend.");
        }
        catch (Exception e)
        {
            Debug.LogError("[ObjectDetector] Failed to load model: " + e.Message);
        }
    }

    private IEnumerator InferenceLoop()
    {
        while (isActiveAndEnabled)
        {
            if (!TryEnsureCameraTexture())
            {
                yield return null;
                continue;
            }

            yield return new WaitForSeconds(inferenceInterval);

            yield return StartCoroutine(PerformInference(_cameraTexture));
        }
    }
    private void SaveDebugImage(RenderTexture rt, string fileName)
    {
        // 1. Create a new Texture2D with the same dimensions
        Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);

        // 2. Set the active RenderTexture and read pixels into the Texture2D
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();

        // 3. Encode to JPG
        byte[] bytes = tex.EncodeToJPG();

        // 4. Determine the path (PersistentDataPath works on Quest and PC)
        string path = System.IO.Path.Combine(Application.persistentDataPath, fileName);
        System.IO.File.WriteAllBytes(path, bytes);

        Debug.Log($"[DEBUG] Image saved to: {path}");

        // Clean up
        Destroy(tex);
        RenderTexture.active = null;
    }
    private bool _hasSavedDebug = false;
    private IEnumerator PerformInference(Texture texture)
    {
        // --- NEW FIX: SQUASH THE IMAGE TO 640x640 ---
        // 1. Create a temporary 640x640 texture
        RenderTexture resizedRT = RenderTexture.GetTemporary(InputSize, InputSize, 0, RenderTextureFormat.ARGB32);

        // 2. Squash the Quest's 1280x960 camera feed into the 640x640 box
        Graphics.Blit(texture, resizedRT);
        if (!_hasSavedDebug)
        {
            SaveDebugImage(resizedRT, "AI_Input_Squashed.jpg");
            _hasSavedDebug = true;
        }
        totalFramesProcessed++;
        Debug.Log($"[SYSTEM] AI Frame Count: {totalFramesProcessed} | Time: {Time.time}");


        var tensorShape = new Unity.InferenceEngine.TensorShape(1, 3, InputSize, InputSize);
        var inputTensor = new Unity.InferenceEngine.Tensor<float>(tensorShape);

        // 3. Convert the SQUASHED texture into the Tensor (instead of the raw 'texture')
        Unity.InferenceEngine.TextureConverter.ToTensor(resizedRT, inputTensor);

        // 4. Clean up the temporary texture immediately to prevent memory leaks!
        RenderTexture.ReleaseTemporary(resizedRT);
        // --------------------------------------------

        var schedule = _engine.ScheduleIterable(inputTensor);
        if (schedule == null)
        {
            Debug.LogWarning("[ObjectDetector] ScheduleIterable returned null; falling back to synchronous scheduling.");
            _engine.Schedule(inputTensor);
        }
        // ... previous code above stays the same ...
        else
        {
            var it = 0;
            while (schedule.MoveNext())
            {
                if (++it % kLayersPerFrame == 0)
                    yield return null;
            }
        }

        // --- UPDATED FIX: NO 'ToReadOnlyArray' NEEDED ---

        // 1. Grab the single output tensor
        Unity.InferenceEngine.Tensor<float> outputTensor = _engine.PeekOutput(0) as Unity.InferenceEngine.Tensor<float>;

        // 2. Wait for Sentis to finish reading the data
        outputTensor.ReadbackRequest();
        while (!outputTensor.IsReadbackRequestDone())
        {
            yield return null;
        }

        // 3. Clone it to a CPU-readable tensor (just like your original script did!)
        Unity.InferenceEngine.Tensor<float> cpuTensor = outputTensor.ReadbackAndClone() as Unity.InferenceEngine.Tensor<float>;

        int elements = 8400; // Number of AI guesses
        int classes = 80;    // Number of YOLO categories

        List<float> filteredCoords = new List<float>();
        List<int> filteredLabels = new List<int>();
        List<float> filteredConfs = new List<float>();

        // 4. Loop through all 8400 guesses using the CPU tensor's built-in indexer!
        for (int i = 0; i < elements; i++)
        {
            float maxConf = 0;
            int classId = -1;

            // Find the highest confidence class for this specific guess (Rows 4 to 83)
            for (int c = 0; c < classes; c++)
            {
                // We use cpuTensor[...] instead of a C# array
                float conf = cpuTensor[(c + 4) * elements + i];
                if (conf > maxConf)
                {
                    maxConf = conf;
                    classId = c;
                }
            }
            // 5. If the AI is at least 15% sure, save it!
            if (maxConf > 0.3f)
            {
                // Inside your if (maxConf > 0.15f) block in ObjectDetector.cs

                float cx = cpuTensor[0 * elements + i]; // Center X
                float cy = cpuTensor[1 * elements + i]; // Center Y
                float w = cpuTensor[2 * elements + i];  // Width
                float h = cpuTensor[3 * elements + i];  // Height

                // SEND CENTER COORDS EXACTLY AS YOLO OUTPUTS THEM
                filteredCoords.Add(cx);
                filteredCoords.Add(cy);
                filteredCoords.Add(w);
                filteredCoords.Add(h);

                filteredLabels.Add(classId);
                filteredConfs.Add(maxConf);

                Debug.Log($"[YOLO] Detected a {(YOLOv9Labels)classId} ({maxConf * 100f}%) at TopLeft: {cx - (w / 2f)}, {cy - (h / 2f)}");
            }
        }
        Debug.Log($"[YOLO] Frame processed. Total valid detections sent to renderer: {filteredLabels.Count}");

        // 6. Repackage our valid data into 3 neat Tensors for your ObjectRenderer
        var finalCoords = new Unity.InferenceEngine.Tensor<float>(
            new Unity.InferenceEngine.TensorShape(filteredLabels.Count, 4), filteredCoords.ToArray());
        var finalLabels = new Unity.InferenceEngine.Tensor<int>(
            new Unity.InferenceEngine.TensorShape(filteredLabels.Count), filteredLabels.ToArray());
        var finalConfs = new Unity.InferenceEngine.Tensor<float>(
            new Unity.InferenceEngine.TensorShape(filteredLabels.Count), filteredConfs.ToArray());

        // 7. Send it to the renderer
        if (_objectRenderer)
        {
            _objectRenderer.RenderDetections(finalCoords, finalLabels, finalConfs);
        }

        // 8. Clean up memory so the Quest doesn't crash
        inputTensor.Dispose();
        outputTensor.Dispose();
        cpuTensor.Dispose();
        finalCoords.Dispose();
        finalLabels.Dispose();
        finalConfs.Dispose();
    }

    private bool TryEnsureCameraTexture()
    {
        if (!_cameraAccess || !_cameraAccess.IsPlaying)
        {
            return false;
        }

        if (_cameraTexture)
        {
            return true;
        }

        _cameraTexture = _cameraAccess.GetTexture();
        if (_cameraTexture)
        {
            var resolution = _cameraAccess.CurrentResolution;
            print($"[ObjectDetector] Passthrough texture ready: {resolution.x}x{resolution.y}");
        }
        else
        {
            Debug.LogWarning("[ObjectDetector] Passthrough texture not available yet.");
        }

        return _cameraTexture;
    }

    private Unity.InferenceEngine.Tensor<float> TryPeekConfidenceOutput()
    {
        try
        {
            return _engine.PeekOutput(2) as Unity.InferenceEngine.Tensor<float>;
        }
        catch (Exception)
        {
            return null;
        }
    }
}