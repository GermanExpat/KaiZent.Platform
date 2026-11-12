using System.Data;
using Dapper;

namespace KaiZent.Platform.Data;

/// <summary>
/// Einmalige Dapper-Einstellungen: snake_case-Spalten auf PascalCase-Eigenschaften, timestamptz als DateTimeOffset (UTC), text[] als string[],
/// date als DateOnly, time als TimeOnly. Npgsql liefert beide als DateTime; ohne die Handler passen Records mit diesen Typen nicht.
/// </summary>
public static class DapperSetup
{
    private static int _done;

    public static void Ensure()
    {
        if (Interlocked.Exchange(ref _done, 1) == 1) return;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
        SqlMapper.AddTypeHandler(new NullableDateTimeOffsetHandler());
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyHandler());
        SqlMapper.AddTypeHandler(new TimeOnlyHandler());
        SqlMapper.AddTypeHandler(new NullableTimeOnlyHandler());
        SqlMapper.AddTypeHandler(new StringArrayHandler());
    }

    /// <summary>text[] als string[]: Npgsql meldet den Feldtyp als System.Array, ohne Handler scheitert die Record-Zuordnung.</summary>
    private sealed class StringArrayHandler : SqlMapper.TypeHandler<string[]>
    {
        public override string[] Parse(object value) => value is string[] a ? a : value is null or DBNull ? [] : ((Array)value).Cast<object>().Select(o => o.ToString()!).ToArray();
        public override void SetValue(IDbDataParameter parameter, string[]? value) => parameter.Value = value ?? [];
    }

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override DateOnly Parse(object value) => ToDate(value);
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value;
        }
    }

    private sealed class NullableDateOnlyHandler : SqlMapper.TypeHandler<DateOnly?>
    {
        public override DateOnly? Parse(object value) => value is null or DBNull ? null : ToDate(value);
        public override void SetValue(IDbDataParameter parameter, DateOnly? value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value is { } v ? v : DBNull.Value;
        }
    }

    private sealed class TimeOnlyHandler : SqlMapper.TypeHandler<TimeOnly>
    {
        public override TimeOnly Parse(object value) => ToTime(value);
        public override void SetValue(IDbDataParameter parameter, TimeOnly value)
        {
            parameter.DbType = DbType.Time;
            parameter.Value = value;
        }
    }

    private sealed class NullableTimeOnlyHandler : SqlMapper.TypeHandler<TimeOnly?>
    {
        public override TimeOnly? Parse(object value) => value is null or DBNull ? null : ToTime(value);
        public override void SetValue(IDbDataParameter parameter, TimeOnly? value)
        {
            parameter.DbType = DbType.Time;
            parameter.Value = value is { } v ? v : DBNull.Value;
        }
    }

    private static TimeOnly ToTime(object value) => value switch
    {
        TimeOnly t => t,
        TimeSpan ts => TimeOnly.FromTimeSpan(ts),
        DateTime dt => TimeOnly.FromDateTime(dt),
        _ => TimeOnly.Parse(value.ToString()!, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static DateOnly ToDate(object value) => value switch
    {
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        _ => DateOnly.Parse(value.ToString()!, System.Globalization.CultureInfo.InvariantCulture),
    };

    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override DateTimeOffset Parse(object value) => ToOffset(value);
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value) => parameter.Value = value.UtcDateTime;
    }

    private sealed class NullableDateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset?>
    {
        public override DateTimeOffset? Parse(object value) => value is null or DBNull ? null : ToOffset(value);
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset? value) =>
            parameter.Value = value is { } v ? v.UtcDateTime : DBNull.Value;
    }

    private static DateTimeOffset ToOffset(object value) => value switch
    {
        DateTimeOffset d => d,
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
        _ => DateTimeOffset.Parse(value.ToString()!, System.Globalization.CultureInfo.InvariantCulture),
    };
}
