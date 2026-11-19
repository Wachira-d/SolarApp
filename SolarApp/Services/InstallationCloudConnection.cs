using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SolarApp.Data;
using SolarApp.Models;
using SolarApp.Models.Enums;

namespace SolarApp.Services;

/// <summary>What the operator sees as the state of a site's vendor-cloud connection.</summary>
public enum CloudConnectionState
{
    /// <summary>Switched on, Station ID and a login saved: the sync job polls it.</summary>
    Connected,
    /// <summary>Switched on but Station ID or login missing: nothing can be polled yet.</summary>
    Incomplete,
    /// <summary>Switched off by an operator, settings kept so it can be resumed.</summary>
    Paused,
    /// <summary>Switched off and nothing saved.</summary>
    Off,
}

/// <summary>
/// One place that decides whether a site's vendor cloud is polled and
/// watched, and how an operator pauses or removes that connection. The sync
/// job, the health monitor and the pages all go through this so "switched
/// off" means the same thing everywhere: no polling, no offline / API alerts.
/// </summary>
public static class InstallationCloudConnection
{
    /// <summary>
    /// The site has a source that should keep data coming: a cloud connection
    /// that is switched on with a Station ID, or a local gateway. A site
    /// without one has nothing that could make it "offline".
    /// </summary>
    public static readonly Expression<Func<Installation, bool>> HasLiveDataSource = i =>
        i.TelemetrySource != TelemetrySource.Cloud
        || (i.CloudApiEnabled && i.PlantId != null && i.PlantId != "");

    /// <summary>The negation of <see cref="HasLiveDataSource"/>, spelled out so EF can translate it.</summary>
    public static readonly Expression<Func<Installation, bool>> HasNoLiveDataSource = i =>
        i.TelemetrySource == TelemetrySource.Cloud
        && (!i.CloudApiEnabled || i.PlantId == null || i.PlantId == "");

    public static bool HasLogin(Installation i) =>
        !string.IsNullOrEmpty(i.CloudUsername) || !string.IsNullOrEmpty(i.CloudApiKey);

    public static bool HasAnySetting(Installation i) =>
        !string.IsNullOrEmpty(i.PlantId) || !string.IsNullOrEmpty(i.CloudUsername) || !string.IsNullOrEmpty(i.CloudPassword)
        || !string.IsNullOrEmpty(i.CloudApiKey) || !string.IsNullOrEmpty(i.CloudApiSecret) || !string.IsNullOrEmpty(i.CloudApiBaseUrl);

    public static CloudConnectionState State(Installation i) =>
        !i.CloudApiEnabled
            ? (HasAnySetting(i) ? CloudConnectionState.Paused : CloudConnectionState.Off)
            : (!string.IsNullOrEmpty(i.PlantId) && HasLogin(i) ? CloudConnectionState.Connected : CloudConnectionState.Incomplete);

    /// <summary>Thai label and Bootstrap colour for the state badge.</summary>
    public static (string Label, string Color) Display(CloudConnectionState s) => s switch
    {
        CloudConnectionState.Connected => ("เชื่อมต่ออยู่", "success"),
        CloudConnectionState.Incomplete => ("ตั้งค่ายังไม่ครบ", "warning"),
        CloudConnectionState.Paused => ("หยุดดึงข้อมูลชั่วคราว", "secondary"),
        _ => ("ไม่ได้เชื่อมต่อ", "secondary"),
    };

    /// <summary>Stop polling but keep every setting, so Resume needs no retyping.</summary>
    public static void Pause(Installation i)
    {
        i.CloudApiEnabled = false;
        i.NextSyncDueAt = null;
        if (i.Status == InstallationStatus.Offline && i.TelemetrySource == TelemetrySource.Cloud)
            i.Status = InstallationStatus.Active;
    }

    public static void Resume(Installation i)
    {
        i.CloudApiEnabled = true;
        i.NextSyncDueAt = null;   // eligible on the next 5-minute sync run
    }

    /// <summary>Remove the connection: switch it off and wipe the Station ID, logins, keys and endpoint.</summary>
    public static void Disconnect(Installation i)
    {
        Pause(i);
        i.PlantId = null;
        i.CloudUsername = null;
        i.CloudPassword = null;
        i.CloudApiKey = null;
        i.CloudApiSecret = null;
        i.CloudApiBaseUrl = null;
    }

    /// <summary>
    /// Close the site's open offline / API-error alerts without telling anyone
    /// it "came back" — nothing came back, the operator switched it off.
    /// Returns how many were closed. Caller saves.
    /// </summary>
    public static async Task<int> CloseConnectivityAlertsAsync(ApplicationDbContext db, int installationId, DateTime nowUtc)
    {
        var open = await db.SystemAlerts
            .Where(a => a.InstallationId == installationId && a.ResolvedAt == null
                        && (a.AlertType == AlertType.Offline || a.AlertType == AlertType.Error))
            .ToListAsync();
        foreach (var a in open)
        {
            a.ResolvedAt = nowUtc;
            a.IsNotified = true;
            a.NotificationSuppressed = true;
        }
        return open.Count;
    }
}
