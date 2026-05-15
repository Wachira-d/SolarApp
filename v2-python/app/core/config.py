"""Application configuration loaded from environment / .env file."""

from pydantic_settings import BaseSettings


class Settings(BaseSettings):
    app_name: str = "SolarApp v2"
    debug: bool = False
    secret_key: str = "CHANGE-ME-IN-PRODUCTION"

    # SQL Server
    db_server: str = "localhost"
    db_name: str = "SolarAppV2"
    db_user: str = "sa"
    db_password: str = ""
    db_driver: str = "ODBC Driver 18 for SQL Server"

    # Redis
    redis_url: str = "redis://localhost:6379/0"

    # File storage
    upload_dir: str = "./uploads"
    max_upload_mb: int = 15

    # LINE Messaging
    line_channel_token: str = ""
    line_alert_group_id: str = ""

    # Inverter API global settings
    huawei_min_spacing_sec: int = 65
    sync_interval_minutes: int = 15

    # Weather API
    weather_api_url: str = "https://api.open-meteo.com/v1/forecast"

    @property
    def database_url(self) -> str:
        return (
            f"mssql+aioodbc://{self.db_user}:{self.db_password}"
            f"@{self.db_server}/{self.db_name}"
            f"?driver={self.db_driver}&TrustServerCertificate=yes"
        )

    class Config:
        env_file = ".env"
        env_file_encoding = "utf-8"


settings = Settings()
