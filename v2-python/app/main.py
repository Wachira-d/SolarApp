"""
SolarApp v2 — Main Entry Point
NiceGUI + FastAPI unified application.
"""

from nicegui import ui, app
from app.core.config import settings

# ── Import all UI pages (each registers its own @ui.page route) ──
import app.ui.cleaning_upload  # noqa: F401


# ── Landing / Dashboard page ──
@ui.page("/")
def index():
    ui.dark_mode(False)

    with ui.header().classes("bg-primary items-center px-4"):
        ui.button(icon="menu", on_click=lambda: drawer.toggle()).props("flat color=white")
        ui.label("SolarApp").classes("text-h6 text-white ml-2")
        ui.space()
        ui.button(icon="notifications").props("flat color=white")
        ui.button(icon="person").props("flat color=white")

    with ui.left_drawer().classes("bg-dark") as drawer:
        ui.label("เมนู").classes("text-h6 text-white mt-4 ml-4")
        for icon, label, href in [
            ("home", "หน้าหลัก", "/"),
            ("engineering", "ไซต์งาน", "/installations"),
            ("assignment", "ใบสั่งงาน", "/workorders"),
            ("build", "การบำรุงรักษา", "/maintenance"),
            ("cleaning_services", "ล้างแผง", "/cleaning"),
            ("map", "แผนที่", "/map"),
            ("bar_chart", "รายงาน", "/reports"),
            ("eco", "คาร์บอนเครดิต", "/esg"),
        ]:
            ui.button(label, icon=icon, on_click=lambda u=href: ui.navigate.to(u)
                      ).props("flat color=white align=left").classes("w-full justify-start")

        ui.separator().classes("bg-grey-8 my-2")
        ui.label("ผู้ดูแลระบบ").classes("text-caption text-grey-5 ml-4")
        for icon, label, href in [
            ("people", "จัดการผู้ใช้", "/admin/users"),
            ("tune", "ค่ามาตรฐาน WO", "/admin/templates"),
            ("schedule", "ตั้งเวลา Sync", "/admin/schedule"),
            ("chat", "ตั้งค่า LINE", "/admin/line"),
        ]:
            ui.button(label, icon=icon, on_click=lambda u=href: ui.navigate.to(u)
                      ).props("flat color=white align=left").classes("w-full justify-start text-caption")

    # ── Dashboard content ──
    with ui.column().classes("w-full max-w-7xl mx-auto p-4 gap-4"):
        ui.label("หน้าหลัก — ภาพรวมระบบ").classes("text-h4 text-bold")

        # KPI row
        with ui.row().classes("w-full gap-4 flex-wrap"):
            for icon, value, label, color in [
                ("solar_power", "6", "ไซต์ทั้งหมด", "blue"),
                ("check_circle", "5", "ใช้งานปกติ", "green"),
                ("warning", "1", "ต้องดูแล", "orange"),
                ("bolt", "42.5 kWp", "กำลังรวม", "purple"),
            ]:
                with ui.card().classes(f"flex-1 min-w-[200px] text-center"):
                    ui.icon(icon, color=color, size="xl")
                    ui.label(value).classes("text-h3 text-bold")
                    ui.label(label).classes("text-caption text-grey-7")

        # Quick actions
        ui.label("ดำเนินการด่วน").classes("text-h5 text-weight-medium")
        with ui.row().classes("w-full gap-2"):
            ui.button("สร้างใบสั่งงาน", icon="add", color="warning",
                      on_click=lambda: ui.navigate.to("/workorders/create")).classes("text-bold")
            ui.button("ดูแจ้งเตือน", icon="notifications_active", color="red",
                      on_click=lambda: ui.navigate.to("/alerts"))
            ui.button("ดูแผนที่", icon="map", color="blue",
                      on_click=lambda: ui.navigate.to("/map"))

        # Recent alerts placeholder
        with ui.card().classes("w-full"):
            ui.label("แจ้งเตือนล่าสุด").classes("text-h6")
            with ui.list().props("bordered separator"):
                for severity, title, site, time in [
                    ("error", "Station Faulty: Grid Loss", "QTC Amorphous Factory", "10 นาทีที่แล้ว"),
                    ("warning", "ครบกำหนดล้างแผง", "โรงงาน ABC", "2 ชั่วโมงที่แล้ว"),
                    ("info", "ใบสั่งงาน WO-0042 เสร็จแล้ว", "บ้านคุณสมชาย", "วันนี้ 14:30"),
                ]:
                    with ui.item():
                        with ui.item_section().props("avatar"):
                            ui.icon(
                                "error" if severity == "error" else
                                "warning" if severity == "warning" else "info",
                                color="red" if severity == "error" else
                                "orange" if severity == "warning" else "blue"
                            )
                        with ui.item_section():
                            ui.item_label(title)
                            ui.item_label(f"{site} · {time}").props("caption")


# ── Start the app ──
ui.run(
    title="SolarApp v2 — Solar Management",
    favicon="☀️",
    host="0.0.0.0",
    port=8080,
    reload=settings.debug,
    storage_secret=settings.secret_key,
)
