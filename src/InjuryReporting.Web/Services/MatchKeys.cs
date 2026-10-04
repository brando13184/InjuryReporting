using System.Security.Cryptography;
using System.Text;

namespace InjuryReporting.Web.Services;

/// <summary>
/// Deterministic keys used to detect duplicate reports of the same incident.
/// <list type="bullet">
/// <item><b>Strict</b>: discipline + injury type + date + event + event kingdom + injured person's kingdom.
/// Equal strict keys are auto-combined into one incident.</item>
/// <item><b>Loose</b>: discipline + date + event + event kingdom. Equal loose keys with a different
/// strict key are flagged as possible duplicates for an admin to review.</item>
/// </list>
/// </summary>
public static class MatchKeys
{
    public static string NormalizeEventName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Normalize(NormalizationForm.FormKD))
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    public static string Strict(int disciplineId, int injuryTypeId, DateOnly date, string eventName, int eventKingdomId, int? injuredKingdomId) =>
        Hash($"S|{disciplineId}|{injuryTypeId}|{date:yyyyMMdd}|{NormalizeEventName(eventName)}|{eventKingdomId}|{injuredKingdomId ?? 0}");

    public static string Loose(int disciplineId, DateOnly date, string eventName, int eventKingdomId) =>
        Hash($"L|{disciplineId}|{date:yyyyMMdd}|{NormalizeEventName(eventName)}|{eventKingdomId}");

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
