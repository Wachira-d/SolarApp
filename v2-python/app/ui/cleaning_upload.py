"""
NiceGUI Page: อัปโหลดรูปภาพงานล้างแผง (Before/After & Yield Verification)

Features:
  - Side-by-side Before / After upload with live preview
  - Mandatory Yield (kWh/Amp) upload before AND after cleaning
  - Drone / wide-angle photo slot
  - Auto-caption for customer report
  - Live % improvement calculation
  - Technician guide tooltips
  - Mobile-friendly camera capture
"""

from nicegui import ui
from datetime import datetime


# ── Photo categories (match DB enum) ──
CATEGORIES = {
    "before_wide": {"label": "ก่อนล้าง — มุมกว้าง", "cat": 30, "timing": 1,
                     "tip": "ถ่ายย้อนแสงให้น้อยที่สุดเพื่อเห็นคราบสกปรกชัดเจน ถ่ายให้เห็นแผงอย่างน้อย 2-3 แถว"},
    "before_macro": {"label": "ก่อนล้าง — Macro จุดคราบ", "cat": 31, "timing": 1,
                      "tip": "ถ่ายใกล้ ให้เห็นคราบนก / ฝุ่น / มอส ที่ฝังลึก ระยะ 30-50 ซม."},
    "after_wide":  {"label": "หลังล้าง — มุมกว้าง", "cat": 32, "timing": 3,
                     "tip": "ถ่ายมุมเดียวกับรูป Before เพื่อเปรียบเทียบ"},
    "after_macro": {"label": "หลังล้าง — Macro จุดเดิม", "cat": 33, "timing": 3,
                     "tip": "ถ่ายจุดเดียวกับรูป Before Macro เพื่อเทียบ"},
    "drainage":    {"label": "ทางระบายน้ำหลังคา", "cat": 34, "timing": 3,
                     "tip": "ถ่ายรางน้ำฝนให้เห็นว่าไม่มีเศษใบไม้/ขยะอุดตัน"},
    "drone":       {"label": "ภาพถ่ายจากโดรน (ถ้ามี)", "cat": 37, "timing": 3,
                     "tip": "มุมท็อปดาวน์ สูง 20-30 เมตร เห็นแผงทั้งระบบ"},
    "yield_before": {"label": "ค่ากำลังผลิต ก่อนล้าง ★", "cat": 35, "timing": 1,
                      "tip": "ถ่ายจอ Inverter/App ให้เห็นค่า Yield หรือ Amp ชัด ★ บังคับอัปโหลด",
                      "required": True},
    "yield_after":  {"label": "ค่ากำลังผลิต หลังล้าง ★", "cat": 36, "timing": 3,
                      "tip": "ถ่ายจอ Inverter/App ให้เห็นค่า Yield หลังล้างเสร็จ ★ บังคับอัปโหลด",
                      "required": True},
}


def create_cleaning_upload_page(job_id: int = 0):
    """Build the cleaning photo upload page."""

    uploaded = {}
    yield_before_val = {"v": None}
    yield_after_val = {"v": None}
    improvement_label = None

    def calc_improvement():
        b, a = yield_before_val["v"], yield_after_val["v"]
        if b and a and b > 0:
            pct = ((a - b) / b) * 100
            improvement_label.text = f"ผลิตเพิ่มขึ้น {pct:.1f}%"
            improvement_label.classes(replace="text-positive text-bold text-h5" if pct > 0
                                      else "text-negative text-bold text-h5")
        else:
            improvement_label.text = "กรอกค่า Yield ทั้งก่อนและหลัง"

    # ── PAGE LAYOUT ──
    with ui.column().classes("w-full max-w-5xl mx-auto p-4 gap-4"):

        # Header
        with ui.row().classes("w-full items-center justify-between"):
            ui.label("อัปโหลดรูปงานล้างแผง").classes("text-h4 text-bold")
            ui.badge(f"WO-{job_id:04d}", color="primary").classes("text-subtitle1")

        ui.separator()

        # ── Section 1: Before / After comparison ──
        ui.label("1. ภาพเปรียบเทียบ ก่อน-หลัง ล้างแผง").classes("text-h5 text-weight-medium")

        with ui.row().classes("w-full gap-4 flex-wrap"):
            for side in ["before", "after"]:
                for zoom in ["wide", "macro"]:
                    key = f"{side}_{zoom}"
                    meta = CATEGORIES[key]
                    with ui.card().classes("flex-1 min-w-[280px]"):
                        with ui.row().classes("items-center gap-2"):
                            ui.icon("photo_camera" if side == "before" else "compare",
                                    color="orange" if side == "before" else "green")
                            ui.label(meta["label"]).classes("text-subtitle1 text-bold")
                        ui.label(meta["tip"]).classes("text-caption text-grey-7 italic")

                        preview = ui.image().classes("w-full h-48 object-cover rounded").style(
                            "background: #f5f5f5; display: none")

                        async def on_upload(e, k=key, p=preview):
                            if e.content:
                                # In production: save to disk, strip EXIF, create thumbnail
                                p.source = f"data:{e.type};base64,{__import__('base64').b64encode(e.content.read()).decode()}"
                                p.style(replace="display: block")
                                uploaded[k] = True
                                ui.notify(f"อัปโหลด {CATEGORIES[k]['label']} สำเร็จ", type="positive")

                        ui.upload(
                            label="เลือกไฟล์ / ถ่ายจากกล้อง",
                            on_upload=on_upload,
                            auto_upload=True,
                            max_file_size=15_000_000,
                        ).props('accept="image/*" capture="environment"').classes("w-full")

        # Drainage check
        with ui.card().classes("w-full"):
            meta = CATEGORIES["drainage"]
            with ui.row().classes("items-center gap-2"):
                ui.icon("water_drop", color="blue")
                ui.label(meta["label"]).classes("text-subtitle1 text-bold")
            ui.label(meta["tip"]).classes("text-caption text-grey-7 italic")
            ui.upload(label="อัปโหลด", auto_upload=True, max_file_size=15_000_000
                      ).props('accept="image/*"').classes("w-full")

        # Drone (optional)
        with ui.expansion("ภาพจากโดรน (ไม่บังคับ)", icon="flight").classes("w-full"):
            meta = CATEGORIES["drone"]
            ui.label(meta["tip"]).classes("text-caption text-grey-7 italic")
            ui.upload(label="อัปโหลดภาพโดรน", auto_upload=True, max_file_size=15_000_000
                      ).props('accept="image/*"').classes("w-full")

        ui.separator()

        # ── Section 2: Yield Verification (MANDATORY) ──
        ui.label("2. พิสูจน์ผลลัพธ์: ค่ากำลังผลิต ก่อน-หลัง ★ บังคับ").classes(
            "text-h5 text-weight-medium text-negative")

        with ui.card().classes("w-full bg-yellow-1"):
            ui.label(
                "ถ่ายหน้าจอ Inverter หรือ App (FusionSolar / SmartPV / FoxCloud) "
                "ให้เห็นตัวเลข Yield / Amp ชัดเจน — เป็นหลักฐาน ROI ให้ลูกค้า"
            ).classes("text-body2 text-grey-8")

            with ui.row().classes("w-full gap-4 flex-wrap"):
                for ykey in ["yield_before", "yield_after"]:
                    meta = CATEGORIES[ykey]
                    is_before = ykey == "yield_before"
                    with ui.card().classes("flex-1 min-w-[280px]" + (" bg-orange-1" if is_before else " bg-green-1")):
                        with ui.row().classes("items-center gap-2"):
                            ui.icon("speed" if is_before else "trending_up",
                                    color="orange" if is_before else "green")
                            ui.label(meta["label"]).classes("text-subtitle1 text-bold")

                        ui.label(meta["tip"]).classes("text-caption text-grey-7 italic")

                        ui.upload(
                            label="★ บังคับ — ถ่ายจอ Inverter / App",
                            auto_upload=True,
                            max_file_size=15_000_000,
                        ).props('accept="image/*" capture="environment"').classes("w-full")

                        val_input = ui.number(
                            label="กรอกค่าที่อ่านได้ (kWh หรือ Amp)",
                            min=0, step=0.1,
                            on_change=lambda e, k=ykey: (
                                yield_before_val.__setitem__("v", e.value) if k == "yield_before"
                                else yield_after_val.__setitem__("v", e.value),
                                calc_improvement()
                            )
                        ).classes("w-full")

            # Live improvement calc
            with ui.row().classes("w-full justify-center py-4"):
                with ui.card().classes("text-center px-8 py-4 bg-blue-1"):
                    ui.label("ผลลัพธ์การล้าง").classes("text-subtitle2 text-grey-7")
                    improvement_label = ui.label("กรอกค่า Yield ทั้งก่อนและหลัง").classes(
                        "text-h5 text-bold text-grey-5")

        ui.separator()

        # ── Section 3: Summary + Submit ──
        ui.label("3. บันทึกข้อมูล").classes("text-h5 text-weight-medium")

        notes = ui.textarea(label="บันทึกเพิ่มเติมจากช่าง (ถ้ามี)", placeholder=(
            "เช่น: พบคราบนกเป็นจุด ทำความสะอาดด้วยน้ำเปล่า ไม่ใช้สารเคมี. "
            "รางน้ำบนหลังคาอุดตันเล็กน้อย ได้ทำความสะอาดแล้ว."
        )).classes("w-full")

        with ui.row().classes("w-full justify-end gap-2"):
            ui.button("ยกเลิก", icon="close", color="grey").on(
                "click", lambda: ui.navigate.to("/workorders"))

            async def submit():
                missing = []
                if "yield_before" not in uploaded:
                    missing.append("รูปค่ากำลังผลิต ก่อนล้าง")
                if "yield_after" not in uploaded:
                    missing.append("รูปค่ากำลังผลิต หลังล้าง")
                if not yield_before_val["v"]:
                    missing.append("ค่า Yield ก่อนล้าง")
                if not yield_after_val["v"]:
                    missing.append("ค่า Yield หลังล้าง")
                if missing:
                    ui.notify(f"กรุณากรอกข้อมูลที่บังคับ:\n• " + "\n• ".join(missing),
                              type="negative", multi_line=True, timeout=5000)
                    return

                ui.notify("กำลังบันทึก...", type="ongoing")
                # In production: save photos to disk/blob, update job.yield_before/after,
                # compute yield_improvement_pct, trigger AI analysis, generate customer report.
                await __import__("asyncio").sleep(1)
                ui.notify("บันทึกสำเร็จ — ส่งรายงานให้ลูกค้าแล้ว", type="positive")
                # ui.navigate.to(f"/workorders/{job_id}")

            ui.button("บันทึกและส่งรายงานลูกค้า", icon="save", color="warning"
                      ).on("click", submit).classes("text-bold")

        # ── Auto-caption preview ──
        with ui.expansion("ตัวอย่าง: ข้อความที่ลูกค้าจะเห็นในรายงาน", icon="visibility").classes("w-full"):
            ui.markdown("""
**รายงานผลการล้างแผงโซลาร์**

| รายการ | ผลลัพธ์ |
|---|---|
| วันที่ล้าง | {date} |
| กำลังผลิตก่อนล้าง | — kWh |
| กำลังผลิตหลังล้าง | — kWh |
| **ประสิทธิภาพเพิ่มขึ้น** | **—%** |

> ล้างด้วยน้ำสะอาดตามมาตรฐาน IEC 62446 ปลอดภัยต่อแผงและสารเคลือบ

*รูปภาพ Before / After แนบด้านล่าง*
""".format(date=datetime.now().strftime("%d/%m/%Y")))


# ── Register as a NiceGUI page ──
@ui.page("/cleaning-upload/{job_id}")
def cleaning_upload_page(job_id: int):
    ui.dark_mode(False)
    with ui.header().classes("bg-primary items-center px-4"):
        ui.button(icon="menu", on_click=lambda: left_drawer.toggle()).props("flat color=white")
        ui.label("SolarApp").classes("text-h6 text-white ml-2")
        ui.space()
        ui.label("ช่าง: สมชาย").classes("text-white")  # from auth context
        ui.button(icon="logout").props("flat color=white")

    with ui.left_drawer().classes("bg-dark") as left_drawer:
        ui.label("เมนู").classes("text-h6 text-white mt-4 ml-4")
        for item in [
            ("home", "หน้าหลัก", "/"),
            ("engineering", "ไซต์งาน", "/installations"),
            ("assignment", "ใบสั่งงาน", "/workorders"),
            ("build", "การบำรุงรักษา", "/maintenance"),
            ("map", "แผนที่", "/map"),
            ("bar_chart", "รายงาน", "/reports"),
        ]:
            ui.button(item[1], icon=item[0], on_click=lambda u=item[2]: ui.navigate.to(u)
                      ).props("flat color=white align=left").classes("w-full justify-start")

    create_cleaning_upload_page(job_id)
