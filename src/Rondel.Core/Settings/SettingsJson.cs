using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Rondel.Core.Agents;
using Rondel.Core.Menus;
using Rondel.Core.Sessions;
using Rondel.Core.Themes;

namespace Rondel.Core.Settings;

/// <summary>
/// How settings.json is read and written. The serializer code is generated at build time
/// (<see cref="SettingsJsonContext"/>), so it needs no reflection and works in a NativeAOT build.
/// </summary>
public static class SettingsJson
{
    static readonly SettingsJsonContext Context = new(new JsonSerializerOptions
    {
        // Lenient enough for hand-edited files, and keeps Turkish characters in paths readable.
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // Enums as their names; a value this version does not know reads as the default (see LenientEnumConverter).
        Converters =
        {
            new LenientEnumConverter<TerminalKind>(),
            new LenientEnumConverter<ShellKind>(),
            new LenientEnumConverter<ColorMode>(),
            new LenientEnumConverter<ActionInput>(),
            new LenientEnumConverter<ActionSource>(),
            new LenientEnumConverter<AgentCapability>(),
            new LenientEnumConverter<SessionHint>(),
        },
    });

    /// <summary>
    /// The options settings.json is read and written with. Their type information is the generated one, which
    /// knows only the settings types, so they are not for anything else.
    /// </summary>
    internal static JsonSerializerOptions Options => Context.Options;

    static JsonTypeInfo<AppSettings> TypeInfo => Context.AppSettings;

    public static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, TypeInfo);

    /// <exception cref="JsonException">The text is not valid settings JSON.</exception>
    public static AppSettings? Deserialize(string json) => JsonSerializer.Deserialize(json, TypeInfo);
}

[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>
/// Writes an enum as its name and reads names (any case) or numbers. A value this version does not know,
/// written by a newer Rondel or by hand, reads as the enum's default instead of making the whole file
/// unreadable, so an older Rondel keeps working with a newer settings.json. Every enum in the settings
/// has its default value first.
/// </summary>
internal sealed class LenientEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                string? name = reader.GetString();
                if (Enum.TryParse(name, ignoreCase: true, out T value) && Enum.IsDefined(value))
                    return value;
                Logging.Log.Warn($"settings.json: unknown {typeof(T).Name} '{name}', using {default(T)}");
                return default;
            case JsonTokenType.Number when reader.TryGetInt32(out int number) && Enum.IsDefined((T)Enum.ToObject(typeof(T), number)):
                return (T)Enum.ToObject(typeof(T), number);
            default:
                Logging.Log.Warn($"settings.json: unexpected {reader.TokenType} for {typeof(T).Name}, using {default(T)}");
                reader.Skip();
                return default;
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
