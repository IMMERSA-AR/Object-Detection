import cv2
import numpy as np
import os

# ── Settings ──────────────────────────────────────────────────
INPUT_FOLDER  = "photos"
OUTPUT_FOLDER = "labels"
CLASS_ID      = 0

MIN_AREA_RATIO   = 0.005
DEBUG_FOLDER     = "debug"
STEPS_FOLDER     = "steps"   # saves one image per processing step

os.makedirs(OUTPUT_FOLDER, exist_ok=True)
os.makedirs(DEBUG_FOLDER,  exist_ok=True)
os.makedirs(STEPS_FOLDER,  exist_ok=True)

# ── Process each image ─────────────────────────────────────────
image_files = [f for f in os.listdir(INPUT_FOLDER)
               if f.lower().endswith(('.jpg', '.jpeg', '.png'))]

print(f"Found {len(image_files)} images.")

for filename in image_files:
    img_path = os.path.join(INPUT_FOLDER, filename)
    img      = cv2.imread(img_path)
    if img is None:
        print(f"  SKIP (cannot read): {filename}")
        continue

    h, w = img.shape[:2]
    name = os.path.splitext(filename)[0]

    # Create a subfolder per image so steps don't mix
    img_steps_folder = os.path.join(STEPS_FOLDER, name)
    os.makedirs(img_steps_folder, exist_ok=True)

    print(f"\nProcessing: {filename}  ({w}x{h})")

    # ── Step 0: Save original ──────────────────────────────────
    cv2.imwrite(os.path.join(img_steps_folder, "step0_original.jpg"), img)
    print(f"  Step 0 saved: original color image")

    # ── Step 1: Grayscale ──────────────────────────────────────
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    cv2.imwrite(os.path.join(img_steps_folder, "step1_grayscale.jpg"), gray)
    print(f"  Step 1 saved: grayscale")

    # ── Step 2: Gaussian Blur ──────────────────────────────────
    blurred = cv2.GaussianBlur(gray, (5, 5), 0)
    cv2.imwrite(os.path.join(img_steps_folder, "step2_blurred.jpg"), blurred)
    print(f"  Step 2 saved: gaussian blur")

    # ── Step 3: Canny Edge Detection ───────────────────────────
    edges = cv2.Canny(blurred, threshold1=50, threshold2=150)
    cv2.imwrite(os.path.join(img_steps_folder, "step3_edges_canny.jpg"), edges)
    print(f"  Step 3 saved: canny edges")

    # ── Step 4: Dilation ───────────────────────────────────────
    kernel  = np.ones((3, 3), np.uint8)
    dilated = cv2.dilate(edges, kernel, iterations=2)
    cv2.imwrite(os.path.join(img_steps_folder, "step4_dilated.jpg"), dilated)
    print(f"  Step 4 saved: dilated edges")

    # ── Step 5: Find Contours + draw ALL of them ───────────────
    contours, _ = cv2.findContours(dilated,
                                   cv2.RETR_EXTERNAL,
                                   cv2.CHAIN_APPROX_SIMPLE)

    all_contours_img = img.copy()
    cv2.drawContours(all_contours_img, contours, -1, (0, 0, 255), 2)  # red = all contours
    cv2.imwrite(os.path.join(img_steps_folder, "step5_all_contours.jpg"), all_contours_img)
    print(f"  Step 5 saved: all contours ({len(contours)} found) — shown in RED")

    # ── Step 6: Filter by shape + draw surviving contours ──────
    filtered_img = img.copy()
    best_box     = None
    best_area    = 0
    kept_count   = 0

    for cnt in contours:
        x, y, cw, ch = cv2.boundingRect(cnt)
        area         = cw * ch
        area_ratio   = area / (w * h)

        if area_ratio < MIN_AREA_RATIO:
            continue

        # Draw each surviving contour in blue
        cv2.rectangle(filtered_img,
                      (x, y), (x + cw, y + ch),
                      (255, 0, 0), 2)   # blue = passed filter
        kept_count += 1

        if area > best_area:
            best_area = area
            best_box  = (x, y, cw, ch)

    cv2.imwrite(os.path.join(img_steps_folder, "step6_filtered_contours.jpg"), filtered_img)
    print(f"  Step 6 saved: filtered contours ({kept_count} passed) — shown in BLUE")

    # ── Step 7: Final result — best box only ───────────────────
    debug_img = img.copy()

    label_name = name + ".txt"
    label_path = os.path.join(OUTPUT_FOLDER, label_name)

    if best_box is not None:
        x, y, bw, bh = best_box

        x_center = (x + bw / 2) / w
        y_center = (y + bh / 2) / h
        norm_w   = bw / w
        norm_h   = bh / h

        with open(label_path, "w") as f:
            f.write(f"{CLASS_ID} {x_center:.6f} {y_center:.6f} "
                    f"{norm_w:.6f} {norm_h:.6f}\n")

        # Green box = final detection
        cv2.rectangle(debug_img,
                      (x, y), (x + bw, y + bh),
                      (0, 255, 0), 3)
        cv2.putText(debug_img, f"obelisk  ar={bh/max(bw,1):.1f}",
                    (x, max(y - 10, 20)),
                    cv2.FONT_HERSHEY_SIMPLEX, 1.0, (0, 255, 0), 2)

        cv2.imwrite(os.path.join(img_steps_folder, "step7_final_result.jpg"), debug_img)
        cv2.imwrite(os.path.join(DEBUG_FOLDER, filename), debug_img)
        print(f"  Step 7 saved: FINAL RESULT — obelisk detected ✅")
        print(f"    Box: x={x} y={y} w={bw} h={bh}")
        print(f"    Area ratio:   {best_area/(w*h):.4f} (need > {MIN_AREA_RATIO})")
        print(f"    YOLO label:   {CLASS_ID} {x_center:.4f} {y_center:.4f} {norm_w:.4f} {norm_h:.4f}")

    else:
        open(label_path, "w").close()
        cv2.imwrite(os.path.join(img_steps_folder, "step7_final_result.jpg"), debug_img)
        cv2.imwrite(os.path.join(DEBUG_FOLDER, filename), debug_img)
        print(f"  Step 7 saved: NO DETECTION ❌ — label file is empty")

print("\n" + "="*50)
print("DONE.")
print(f"  Labels saved in:      '{OUTPUT_FOLDER}/'")
print(f"  Final results in:     '{DEBUG_FOLDER}/'")
print(f"  Step-by-step images:  '{STEPS_FOLDER}/<image_name>/'")
print("="*50)
