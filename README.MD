# IMMERSA — Augmented Reality Historical Experience

## Project Overview

IMMERSA is an Augmented Reality (AR) application developed for the Meta Quest 3S. The purpose of the application is to allow users to virtually travel back in time and explore the history of a particular location in an immersive and interactive manner. The system can be deployed in various environments, including historical monuments, museums, and universities.
The application works by detecting specific real-world objects within the user's surroundings. Once a target object is recognized, a complete historical scene is generated, including virtual characters, a time portal, and other relevant visual elements. A story-based narration is then initiated to guide the user through the historical experience. In addition to passive storytelling, IMMERSA provides interactive Non-Player Characters (NPCs) that users can communicate with. Users can ask questions related to the historical scene and receive contextual responses, creating a more engaging and personalized learning experience.

---

## Tech Stack

- **Engine:** Unity 6000.4.0f1
- **Target Device:** Meta Quest 3S
- **Backend:** Python (FastAPI) — deployed on Google Cloud
- **Render Pipeline:** Universal Render Pipeline (URP)
- **XR Framework:** Meta XR SDK 85.0.0
- **ML Inference:** Unity AI Inference Engine (ONNX)
- **Character Rigging:** CC3/CC4 (Character Creator)

---

## Project Structure

```
scene/
├── Assets/
│   └── _Project/
│       ├── Code/
│       │   └── Scripts/
│       │       ├── ImageProcessing/   # Experience management & passthrough
│       │       ├── LectureHall/       # Lecture hall scene logic & NPCs
│       │       └── Panel/             # Panel detection & narration logic
│       ├── AiModels/                  # ONNX models
│       │   ├── obelisk_detector.onnx  # Obelisk detection model
│       │   ├── panel_best.onnx        # Panel detection YOLO model
│       │   └── viseme_model_energy.onnx # Lip sync model
│       └── Shaders/                   # URP shaders
├── Packages/
│   └── manifest.json                  # Package dependencies
└── ProjectSettings/
```

---

## Experiences

### 1. Lecture Hall Experience

- Scans the real environment using Meta's Mixed Reality Utility Kit (MRUK) to detect chairs, desks, and screens
- Spawns historical NPC students and a doctor/lecturer on detected furniture
- Doctor delivers a pre-recorded lecture with synchronized lip-sync
- Main character (Murad) stands up after the lecture, walks toward the user, and enters an interactive Q&A session via voice API

### 2. Panel Experience

- Uses a YOLO object detection model running on the device camera to recognize up to 9 exhibition panels
- Spawns a corresponding historical character beside each detected panel
- User interacts using controller ray-casting to trigger narration with lip-sync
- User can replay the story, move to the next panel, or restart the full experience

---

## Non Player Character Features

- Smooth animation crossfading via PlayableGraph (no Animator Controller required)
- Realistic eye blinking via blendshapes
- Breathing simulation on spine bone
- Head and eye look-at tracking toward the user
- Audio-driven lip sync via viseme ONNX model

---

## ML Models

| Model                      | Purpose                                                                                                                                                                                                      |
| -------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `obelisk_detector.onnx`    | A yolo based object detection model trained to recognize the obelisk in the camera feed, used to trigger the obelisk AR experience when the user points their headset at it                                  |
| `panel_best.onnx`          | A yolo based object detection model trained to identify and classify up to 9 different exhibition panels in the camera feed, used to determine which historical character to spawn beside the detected panel |
| `viseme_model_energy.onnx` | Exports the trained PyTorch checkpoint to ONNX so Unity can run the model.                                                                                                                                   |

---

## Prerequisites

- Unity **6000.4.0f1** (must be this exact version)
- Unity Hub with **Android Build Support** module (Android SDK & NDK included)
- **Meta Quest** headset with Developer Mode enabled
- **CC3 Unity Tools v2.2.6** — download from the CC/iC website

---

## Setup & Running

### Fix Package Path

Open `Packages/manifest.json` and update line 4 to point to where you extracted CC3 Unity Tools on your machine:

```json
"com.soupday.cc3_unity_tools": "file:YOUR_LOCAL_PATH/CCiC-Unity-Tools-2.2.6"
```

### Open the Project

1. Open Unity Hub
2. Click **Add → Add project from disk**
3. Select the `Object-Detection` folder
4. Ensure Unity version **6000.4.0f1** is selected

### Build Settings

1. Go to **File → Build Settings**
2. Set platform to **Android** → click **Switch Platform**
3. Go to **Edit → Project Settings → XR Plug-in Management → Android tab**
4. Enable **Meta XR**

### Deploy to Device

1. Connect Meta Quest via USB
2. Accept **Allow USB Debugging** on the headset
3. In Build Settings, click **Refresh** and select your headset
4. Click **Build and Run**

### Backend

- Already deployed on **Google Cloud**
- To run locally:

```bash
uvicorn app.main:app --reload
```

---
