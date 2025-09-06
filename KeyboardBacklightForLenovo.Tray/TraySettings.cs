using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeyboardBacklightForLenovo
{
  public enum OperatingMode
  {
    TimeBased = 0,
    NightLight = 1
  }

  /// <summary>
  /// Settings used by the tray app (and optionally by the service later).
  /// - Day/Night levels: 0=Off, 1=Low, 2=High
  /// - Mode: TimeBased or NightLight
  /// - Time-based: DayStart -> DayEnd; Night is the 24h complement [DayEnd -> DayStart]
  /// - AutoEnabled: when true, persistence is driven ONLY by day/night transitions.
  /// </summary>
  public sealed class TraySettings
  {
    public int DayLevel { get; set; } = 1;   // Default: Low
    public int NightLevel { get; set; } = 2; // Default: High
    public OperatingMode Mode { get; set; } = OperatingMode.TimeBased;

    [JsonConverter(typeof(TimeSpanJsonConverter))]
    public TimeSpan DayStart { get; set; } = TimeSpan.FromHours(8);   // 08:00

    [JsonConverter(typeof(TimeSpanJsonConverter))]
    public TimeSpan DayEnd { get; set; } = TimeSpan.FromHours(20);    // 20:00

    public bool AutoEnabled { get; set; } = false;

    public (TimeSpan NightStart, TimeSpan NightEnd) GetNightInterval()
        => (DayEnd, DayStart);
  }

  /// <summary>
  /// JSON converter for TimeSpan written as "HH:mm" and tolerant reading.
  /// </summary>
  public sealed class TimeSpanJsonConverter : JsonConverter<TimeSpan>
  {
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
      var s = reader.GetString();
      if (string.IsNullOrWhiteSpace(s)) return TimeSpan.Zero;

      // Accept "HH:mm", "H:mm", or general TimeSpan formats
      if (TimeSpan.TryParse(s, out var ts)) return ts;

      // Fallback: parse as DateTime, keep time-of-day
      if (DateTime.TryParse(s, out var dt)) return dt.TimeOfDay;

      throw new FormatException("Invalid time format. Expected HH:mm.");
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
      // Always write as HH:mm
      writer.WriteStringValue(new DateTime(value.Ticks).ToString("HH:mm"));
    }
  }

  /// <summary>
  /// File-backed persistence for TraySettings.
  /// </summary>
  public static class TraySettingsStore
  {
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     "KeyboardBacklightForLenovo");

    private static readonly string FilePath = Path.Combine(Dir, "TraySettings.json");

    /// <summary>Full JSON settings path (for diagnostics/UI).</summary>
    public static string StorePath => FilePath;

    public static TraySettings LoadOrDefaults()
    {
      try
      {
        if (File.Exists(FilePath))
        {
          var json = File.ReadAllText(FilePath);
          var obj = JsonSerializer.Deserialize<TraySettings>(json, JsonOpts());
          if (obj != null) return Clamp(obj);
        }
      }
      catch
      {
        // ignore -> fall back to defaults
      }
      return new TraySettings();
    }

    public static void Save(TraySettings settings)
    {
      var sanitized = Clamp(settings);
      Directory.CreateDirectory(Dir);
      var json = JsonSerializer.Serialize(sanitized, JsonOptsIndented());
      File.WriteAllText(FilePath, json);
    }

    private static TraySettings Clamp(TraySettings s)
    {
      // Clamp levels to 0..2 to keep consumers simple
      s.DayLevel = Math.Clamp(s.DayLevel, 0, 2);
      s.NightLevel = Math.Clamp(s.NightLevel, 0, 2);
      return s;
    }

    private static JsonSerializerOptions JsonOpts()
    {
      var o = new JsonSerializerOptions
      {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
      };
      o.Converters.Add(new TimeSpanJsonConverter());
      return o;
    }

    private static JsonSerializerOptions JsonOptsIndented()
    {
      var o = JsonOpts();
      o.WriteIndented = true;
      return o;
    }
  }
}
