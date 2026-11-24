using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace SupportDesk.Data;

/// <summary>Stores DateTime as-is and returns it flagged as UTC.</summary>
internal sealed class UtcDateTimeConverter()
    : ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
