# -*- coding: utf-8 -*-
"""Final: nicer annotated image + Excel deliverable."""
import sys
import json
import numpy as np
import cv2
from openpyxl import Workbook
from openpyxl.styles import Font, Alignment, PatternFill, Border, Side

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

BASE = r"D:/zhiyouproject/hummingbird/dial_digitize"
IMG3 = BASE + "/img3.png"

with open(BASE + "/holes_final.json", encoding="utf-8") as f:
    data = json.load(f)
cx, cy = data["center_px"]
holes = data["holes"]

img = cv2.imread(IMG3)
H, W = img.shape[:2]
for h in holes:
    x, y = h["px"]
    cv2.circle(img, (int(x), int(y)), 15, (0, 200, 255), 2)
    # label left if near right edge or specific crowded spots
    tx = int(x) + 18
    if x > W * 0.72 or h["no"] in (13, 14, 18, 19, 23):
        tx = int(x) - 52
    cv2.putText(img, str(h["no"]), (tx, int(y) - 12),
                cv2.FONT_HERSHEY_SIMPLEX, 0.65, (0, 0, 255), 2)
cv2.drawMarker(img, (int(cx), int(cy)), (255, 0, 0), cv2.MARKER_CROSS, 30, 2)
cv2.imwrite(BASE + "/holes_annotated.png", img)
print("annotated image updated")

def to_clock(ang):
    """数学角(+X起逆时针) -> 时钟角(12H=正上方起顺时针)"""
    return (90.0 - ang) % 360.0

def clock_str(deg):
    h = int(deg // 30) % 12
    m = int(round((deg % 30) * 2))
    if m >= 60:
        m = 0
        h = (h + 1) % 12
    return f"{h or 12}H{m:02d}"

wb = Workbook()
ws = wb.active
ws.title = "孔位坐标"
head = ["序号", "X (0.01mm)", "Y (0.01mm)", "半径R (0.01mm)", "角度(数学制°)",
        "时钟方位(测量)", "时钟方位(参考)", "参考角度(取整)", "参考半径(取整)"]
thin = Border(*[Side(style="thin")] * 4)
fill = PatternFill("solid", fgColor="DDEBF7")
for c, t in enumerate(head, 1):
    cell = ws.cell(1, c, t)
    cell.font = Font(bold=True)
    cell.fill = fill
    cell.border = thin
    cell.alignment = Alignment(horizontal="center")
for r, h in enumerate(holes, 2):
    vals = [h["no"], h["X"], h["Y"], h["R"], h["ang"],
            clock_str(to_clock(h["ang"])), clock_str(to_clock(h["ang_snap"])),
            h["ang_snap"], h["R_snap"]]
    for c, v in enumerate(vals, 1):
        cell = ws.cell(r, c, v)
        cell.border = thin
        cell.alignment = Alignment(horizontal="center")
for col, wds in zip("ABCDEFGHI", [8, 14, 14, 16, 12, 14, 14, 14, 14]):
    ws.column_dimensions[col].width = wds
ws.cell(r + 2, 1, "坐标系：原点=表盘中心（同心圆圆心），X 向右（3H 方向），Y 向上（12H 方向）。单位 0.01mm。")
ws.cell(r + 3, 1, "时钟方位：12H=正上方（客户 2026-10-29 确认），顺时针计时。例：1H00 = 从正上方顺时针 30°；2H45 = 82.5°。")
ws.cell(r + 4, 1, "数据来源：对《表盘开孔位置（20261029）.pdf》图3 中红圈孔位的图像数字化结果（测量值，非设计原图数据）。")

ws2 = wb.create_sheet("方法与精度说明")
notes = [
    ["项", "说明"],
    ["数据来源", "《表盘开孔位置（20261029）.pdf》图3（红圈圈出的25个Ø0.3mm孔）"],
    ["数字化方法", "OpenCV 检测红圈中心（含粘连红圈二次分离）→ 以图上5个同心圆（Ø3420/Ø3320/Ø2860/Ø2590/Ø2480）最小二乘拟合确定中心与比例尺 → 换算孔位坐标"],
    ["框架精度", "5个圆的拟合中心彼此偏差 <0.15 像素（约5μm）；比例尺由4组圆径互相验证，偏差<0.1%"],
    ["孔位精度估计", "受红圈标注（人工圈画）中心偏差限制，单孔估计误差约 ±0.05~0.15mm；与图纸标注可对上的孔（120/240/1145/1360/1100/1240）误差在 ±0.03mm 内"],
    ["发现的设计规律", "以 12H-6H 轴左右镜像对称（1H↔11H、2H↔10H、3H↔9H、4H↔8H、5H↔7H）。组成：整点方向径向孔对(内+外)×9 + 2H45 径向孔对×1 + 12H 两侧对称一对(12H15/11H45，同半径1240) + 6H 两侧一对(6H15/5H45) + 10H 单个外圈孔。多数孔位于 R≈1295（Ø2590分布圆）和 R≈1455 附近，全部落在 7.5°（一刻钟）网格上"],
    ["使用建议", "1) 坐标为测量值，正式加工前请客户按原图核对确认；2) 首件加工后实测孔位做闭环验证；3) 若客户能找到原始孔位数据，以原数据为准"],
    ["生成时间", "2026-10-29"],
]
for r, row in enumerate(notes, 1):
    for c, v in enumerate(row, 1):
        cell = ws2.cell(r, c, v)
        if r == 1:
            cell.font = Font(bold=True)
            cell.fill = fill
        cell.alignment = Alignment(vertical="top", wrap_text=True)
ws2.column_dimensions["A"].width = 16
ws2.column_dimensions["B"].width = 100

out = BASE + "/表盘25孔坐标_数字化.xlsx"
wb.save(out)
print("saved:", out)
