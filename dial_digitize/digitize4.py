# -*- coding: utf-8 -*-
"""Round 4: precise center via circle least-squares fits, scale via multi-peak
consensus regression, then pattern analysis (30-degree grid snapping)."""
import sys
import json
import numpy as np
import cv2

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

BASE = r"D:/zhiyouproject/hummingbird/dial_digitize"
IMG3 = BASE + "/img3.png"
# radial peak (px) <-> known diameter (0.01mm) assignments from consensus
MATCHES = [(547.0, 3420), (530.0, 3320), (456.0, 2860), (414.0, 2590), (395.0, 2480)]
CX0, CY0 = 609.4, 614.5


def red_mask(bgr):
    b, g, r = cv2.split(bgr.astype(np.int16))
    return (((r > 130) & (r - g > 60) & (r - b > 60))).astype(np.uint8) * 255


def get_holes(img):
    mask = red_mask(img)
    n, labels, stats, cents = cv2.connectedComponentsWithStats(mask, 8)
    holes = []
    for i in range(1, n):
        x, y, w, h, area = stats[i]
        if area < 40 or area > 60000:
            continue
        if w < 70 and h < 70:
            holes.append((float(cents[i][0]), float(cents[i][1])))
        else:
            x0, y0 = max(0, x-10), max(0, y-10)
            x1 = min(mask.shape[1], x+w+10)
            y1 = min(mask.shape[0], y+h+10)
            roi = mask[y0:y1, x0:x1]
            found = cv2.HoughCircles(roi, cv2.HOUGH_GRADIENT, dp=1, minDist=30,
                                     param1=100, param2=15, minRadius=18, maxRadius=30)
            if found is not None:
                for c in found[0]:
                    holes.append((c[0]+x0, c[1]+y0))
    return holes


def fit_circle(xs, ys):
    """algebraic least squares circle fit -> (cx, cy, r)"""
    A = np.c_[2*xs, 2*ys, np.ones(len(xs))]
    b = xs**2 + ys**2
    sol, *_ = np.linalg.lstsq(A, b, rcond=None)
    cx, cy = sol[0], sol[1]
    r = np.sqrt(sol[2] + cx*cx + cy*cy)
    return cx, cy, r


def main():
    img = cv2.imread(IMG3)
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    dark = gray < 120
    ys, xs = np.nonzero(dark)
    xs = xs.astype(float)
    ys = ys.astype(float)

    # --- fit each known circle precisely ---
    fits = []
    for r0, diam in MATCHES:
        d = np.sqrt((xs-CX0)**2 + (ys-CY0)**2)
        sel = np.abs(d - r0) < 3.0
        if sel.sum() < 200:
            print(f"r0={r0}: too few pixels ({sel.sum()}), skip")
            continue
        cx, cy, r = fit_circle(xs[sel], ys[sel])
        res = np.abs(np.sqrt((xs[sel]-cx)**2 + (ys[sel]-cy)**2) - r)
        fits.append((cx, cy, r, diam, float(res.mean()), int(sel.sum())))
        print(f"r0={r0:6.1f} -> center=({cx:7.2f},{cy:7.2f}) r={r:7.2f} "
              f"resid={res.mean():.2f}px n={sel.sum()} (Ø{diam})")

    cxs = np.array([f[0] for f in fits])
    cys = np.array([f[1] for f in fits])
    cx = float(np.average(cxs, weights=[f[4] and 1/f[4] for f in fits]))
    cy = float(np.average(cys, weights=[f[4] and 1/f[4] for f in fits]))
    print(f"\nfinal center = ({cx:.2f}, {cy:.2f})  "
          f"(spread x={cxs.std():.2f}, y={cys.std():.2f})")

    # --- scale regression through origin: diam = 2 * s * r ---
    rr = np.array([f[2] for f in fits])
    dd = np.array([f[3] for f in fits], dtype=float)
    scale = float((dd @ (2*rr)) / ((2*rr) @ (2*rr)))
    print(f"scale = {scale:.4f} 0.01mm/px  ({scale*100:.1f} um/px)")
    for f in fits:
        pred = 2*scale*f[2]
        print(f"  Ø{f[3]}: fitted {pred:.1f} -> diff {pred-f[3]:+.1f}")

    holes = get_holes(img)
    print(f"\nholes: {len(holes)}")

    def coords(hs):
        return np.array([((hx-cx)*scale, -(hy-cy)*scale) for (hx, hy) in hs])

    C = coords(holes)
    order = np.lexsort((C[:, 0], -C[:, 1]))
    out = []
    print("\n== hole coords (0.01mm, origin=dial center, X right, Y up=12H) ==")
    for k, idx in enumerate(order, 1):
        X, Y = C[idx]
        R = float(np.hypot(X, Y))
        A = float(np.degrees(np.arctan2(Y, X)) % 360)
        # snap angle to 7.5-degree grid and radius to 5-unit grid for design guess
        Asnap = round(A / 7.5) * 7.5 % 360
        Rsnap = round(R / 5) * 5
        out.append(dict(no=k, X=round(float(X), 1), Y=round(float(Y), 1),
                        R=round(R, 1), ang=round(A, 2),
                        ang_snap=Asnap, R_snap=Rsnap,
                        px=[round(holes[idx][0], 1), round(holes[idx][1], 1)]))
        print(f"  H{k:02d} X={X:7.1f} Y={Y:7.1f}  R={R:6.1f}->{Rsnap:6.1f}  "
              f"ang={A:6.2f}->{Asnap:5.1f}")

    # pattern: pair detection (same snapped angle)
    from collections import defaultdict
    groups = defaultdict(list)
    for h in out:
        groups[h["ang_snap"]].append(h["no"])
    print("\nsame-angle groups:", {k: v for k, v in sorted(groups.items()) if len(v) > 1})

    with open(BASE + "/holes_final.json", "w", encoding="utf-8") as f:
        json.dump(dict(center_px=[cx, cy], scale=scale, holes=out), f,
                  ensure_ascii=False, indent=1)
    vis = img.copy()
    for h in out:
        x, y = h["px"]
        cv2.circle(vis, (int(x), int(y)), 15, (0, 200, 255), 2)
        cv2.putText(vis, str(h["no"]), (int(x)+18, int(y)-12),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.65, (0, 0, 255), 2)
    cv2.drawMarker(vis, (int(cx), int(cy)), (255, 0, 0), cv2.MARKER_CROSS, 30, 2)
    cv2.imwrite(BASE + "/holes_annotated.png", vis)
    print("\nsaved holes_final.json + holes_annotated.png")

if __name__ == "__main__":
    main()
