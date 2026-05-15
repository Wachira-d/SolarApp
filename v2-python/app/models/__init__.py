"""SQLAlchemy ORM models — mirrors the SQL schema."""

from sqlalchemy import (
    Column, Integer, String, Boolean, DateTime, Date, Time, Float,
    Numeric, Text, ForeignKey, BigInteger, Index, func
)
from sqlalchemy.orm import DeclarativeBase, relationship


class Base(DeclarativeBase):
    pass


# ─── Users / Partners / Customers ───────────────────────────

class Partner(Base):
    __tablename__ = "partners"
    id = Column(Integer, primary_key=True, autoincrement=True)
    name = Column(String(200), nullable=False)
    is_active = Column(Boolean, default=True)
    created_at = Column(DateTime, server_default=func.now())


class User(Base):
    __tablename__ = "users"
    id = Column(Integer, primary_key=True, autoincrement=True)
    email = Column(String(200), unique=True, nullable=False)
    password_hash = Column(String(500), nullable=False)
    full_name = Column(String(200), nullable=False)
    phone = Column(String(50))
    role = Column(String(20), default="technician")
    partner_id = Column(Integer, ForeignKey("partners.id"))
    line_user_id = Column(String(100))
    is_active = Column(Boolean, default=True)
    created_at = Column(DateTime, server_default=func.now())


class Customer(Base):
    __tablename__ = "customers"
    id = Column(Integer, primary_key=True, autoincrement=True)
    user_id = Column(Integer, ForeignKey("users.id"))
    partner_id = Column(Integer, ForeignKey("partners.id"))
    first_name = Column(String(100), nullable=False)
    last_name = Column(String(100), nullable=False)
    phone = Column(String(50))
    email = Column(String(200))
    line_user_id = Column(String(100))
    is_active = Column(Boolean, default=True)
    created_at = Column(DateTime, server_default=func.now())


# ─── Installation ────────────────────────────────────────────

class Installation(Base):
    __tablename__ = "installations"
    id = Column(Integer, primary_key=True, autoincrement=True)
    site_code = Column(String(30), unique=True, nullable=False)
    site_name = Column(String(200), nullable=False)
    address = Column(String(500))
    latitude = Column(Float)
    longitude = Column(Float)
    customer_id = Column(Integer, ForeignKey("customers.id"), nullable=False)
    partner_id = Column(Integer, ForeignKey("partners.id"))
    system_capacity_kw = Column(Numeric(10, 3), default=0)
    inverter_brand = Column(String(50), nullable=False)
    status = Column(Integer, default=1)
    health_score = Column(Numeric(5, 2))
    installation_date = Column(Date, nullable=False)
    warranty_expiry = Column(Date, nullable=False)
    electricity_rate = Column(Numeric(8, 4), default=4.15)
    next_cleaning_due = Column(Date)
    last_online_at = Column(DateTime)
    plant_id = Column(String(100))
    cloud_username = Column(String(500))
    cloud_password = Column(String(500))
    created_at = Column(DateTime, server_default=func.now())
    updated_at = Column(DateTime, server_default=func.now(), onupdate=func.now())

    customer = relationship("Customer")
    equipment = relationship("Equipment", back_populates="installation")


class Equipment(Base):
    __tablename__ = "equipment"
    id = Column(Integer, primary_key=True, autoincrement=True)
    installation_id = Column(Integer, ForeignKey("installations.id", ondelete="CASCADE"))
    device_type = Column(String(50), nullable=False)
    brand = Column(String(100))
    model = Column(String(100))
    serial_number = Column(String(100))
    rating_kw = Column(Numeric(10, 3))
    quantity = Column(Integer, default=1)
    is_active = Column(Boolean, default=True)
    created_at = Column(DateTime, server_default=func.now())

    installation = relationship("Installation", back_populates="equipment")


# ─── Jobs ────────────────────────────────────────────────────

class Job(Base):
    __tablename__ = "jobs"
    id = Column(Integer, primary_key=True, autoincrement=True)
    job_number = Column(String(20), unique=True, nullable=False)
    job_type = Column(Integer, nullable=False)
    status = Column(Integer, default=0)
    installation_id = Column(Integer, ForeignKey("installations.id"))
    customer_id = Column(Integer, ForeignKey("customers.id"))
    title = Column(String(200), nullable=False)
    description = Column(Text)
    scheduled_date = Column(Date, nullable=False)
    scheduled_time = Column(Time)
    assigned_to_user_id = Column(Integer, ForeignKey("users.id"))
    created_by_user_id = Column(Integer, ForeignKey("users.id"))
    yield_before = Column(Numeric(10, 2))
    yield_after = Column(Numeric(10, 2))
    yield_improvement_pct = Column(Numeric(5, 2))
    ppe_check_passed = Column(Boolean, default=False)
    created_at = Column(DateTime, server_default=func.now())
    updated_at = Column(DateTime, server_default=func.now(), onupdate=func.now())

    installation = relationship("Installation")
    checklist_items = relationship("JobChecklistItem", back_populates="job")
    photos = relationship("JobPhoto", back_populates="job")


class JobChecklistItem(Base):
    __tablename__ = "job_checklist_items"
    id = Column(Integer, primary_key=True, autoincrement=True)
    job_id = Column(Integer, ForeignKey("jobs.id", ondelete="CASCADE"))
    sort_order = Column(Integer, default=0)
    description = Column(String(300), nullable=False)
    is_completed = Column(Boolean, default=False)
    completed_at = Column(DateTime)
    check_type = Column(String(30))
    expected_value = Column(String(100))
    actual_value = Column(String(100))
    is_pass = Column(Boolean)

    job = relationship("Job", back_populates="checklist_items")


class JobPhoto(Base):
    __tablename__ = "job_photos"
    id = Column(Integer, primary_key=True, autoincrement=True)
    job_id = Column(Integer, ForeignKey("jobs.id", ondelete="CASCADE"))
    category = Column(Integer, nullable=False)
    timing = Column(Integer, default=1)
    file_path = Column(String(500), nullable=False)
    thumbnail_path = Column(String(500))
    content_type = Column(String(100))
    file_size_bytes = Column(BigInteger, default=0)
    latitude = Column(Float)
    longitude = Column(Float)
    ai_analysis_status = Column(Integer, default=0)
    ai_hotspot_detected = Column(Boolean)
    ai_ocr_text = Column(Text)
    ai_quality_score = Column(Numeric(5, 2))
    description = Column(String(500))
    auto_caption = Column(String(500))
    sort_order = Column(Integer, default=0)
    uploaded_by_user_id = Column(Integer, ForeignKey("users.id"))
    uploaded_at = Column(DateTime, server_default=func.now())

    job = relationship("Job", back_populates="photos")


# ─── Alerts ──────────────────────────────────────────────────

class Alert(Base):
    __tablename__ = "alerts"
    id = Column(Integer, primary_key=True, autoincrement=True)
    installation_id = Column(Integer, ForeignKey("installations.id"))
    alert_type = Column(Integer, nullable=False)
    severity = Column(Integer, nullable=False)
    title = Column(String(200), nullable=False)
    message = Column(Text, nullable=False)
    root_cause = Column(Text)
    fault_code = Column(String(50))
    auto_ticket_job_id = Column(Integer, ForeignKey("jobs.id"))
    is_notified = Column(Boolean, default=False)
    occurred_at = Column(DateTime, server_default=func.now())
    resolved_at = Column(DateTime)

    installation = relationship("Installation")
