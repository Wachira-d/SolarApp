using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SolarApp.Models;

namespace SolarApp.Data.Interceptors;

public class AuditableEntityInterceptor : SaveChangesInterceptor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AuditableEntityInterceptor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        if (context == null) return base.SavingChangesAsync(eventData, result, cancellationToken);

        var userId = _httpContextAccessor.HttpContext?.User?.FindFirst(
            System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var userName = _httpContextAccessor.HttpContext?.User?.Identity?.Name;
        var ipAddress = _httpContextAccessor.HttpContext?.Connection?.RemoteIpAddress?.ToString();

        var auditEntries = new List<AuditLog>();

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is AuditLog) continue;

            var entityType = entry.Entity.GetType().Name;
            string? entityId = null;

            var idProp = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "Id");
            if (idProp != null)
                entityId = idProp.CurrentValue?.ToString();

            switch (entry.State)
            {
                case EntityState.Added:
                    auditEntries.Add(new AuditLog
                    {
                        UserId = userId,
                        UserName = userName,
                        Action = "Create",
                        EntityType = entityType,
                        EntityId = entityId,
                        NewValues = SerializeProperties(entry.Properties
                            .Where(p => p.CurrentValue != null)
                            .ToDictionary(p => p.Metadata.Name, p => p.CurrentValue)),
                        IpAddress = ipAddress,
                        Timestamp = DateTime.UtcNow
                    });
                    break;

                case EntityState.Modified:
                    var oldValues = new Dictionary<string, object?>();
                    var newValues = new Dictionary<string, object?>();

                    foreach (var prop in entry.Properties.Where(p => p.IsModified))
                    {
                        oldValues[prop.Metadata.Name] = prop.OriginalValue;
                        newValues[prop.Metadata.Name] = prop.CurrentValue;
                    }

                    if (oldValues.Count > 0)
                    {
                        auditEntries.Add(new AuditLog
                        {
                            UserId = userId,
                            UserName = userName,
                            Action = "Update",
                            EntityType = entityType,
                            EntityId = entityId,
                            OldValues = SerializeProperties(oldValues),
                            NewValues = SerializeProperties(newValues),
                            IpAddress = ipAddress,
                            Timestamp = DateTime.UtcNow
                        });
                    }
                    break;

                case EntityState.Deleted:
                    auditEntries.Add(new AuditLog
                    {
                        UserId = userId,
                        UserName = userName,
                        Action = "Delete",
                        EntityType = entityType,
                        EntityId = entityId,
                        OldValues = SerializeProperties(entry.Properties
                            .ToDictionary(p => p.Metadata.Name, p => p.OriginalValue)),
                        IpAddress = ipAddress,
                        Timestamp = DateTime.UtcNow
                    });
                    break;
            }
        }

        if (auditEntries.Count > 0)
        {
            context.Set<AuditLog>().AddRange(auditEntries);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static string? SerializeProperties(Dictionary<string, object?> properties)
    {
        if (properties.Count == 0) return null;
        return JsonSerializer.Serialize(properties, new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }
}
