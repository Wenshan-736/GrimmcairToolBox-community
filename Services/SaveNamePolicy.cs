namespace Grimmcair.Desktop.Services;

/// <summary>Only display names are case-insensitive. Raw character identity stays exact.</summary>
public static class SaveNamePolicy
{
    public const int MaximumApiNameLength = 160;
    public const int MaximumGeneratedNameLength = 100;

    public static string NewNameBase(string preferredName)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var name = new string(preferredName.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)
            && c != '-' && !invalid.Contains(c)).ToArray()).Trim('.');
        if (string.IsNullOrWhiteSpace(name)) name = "存档";
        return Clip(name, MaximumGeneratedNameLength).TrimEnd('.');
    }

    public static string ValidateManualName(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) throw new InvalidOperationException("存档名称不能为空。");
        try { GameChatCommands.Save(trimmed); }
        catch (System.IO.InvalidDataException error) { throw new InvalidOperationException(error.Message, error); }
        return trimmed;
    }

    public static string WithSuffix(string preferredName, string suffix) =>
        Clip(NewNameBase(preferredName), MaximumGeneratedNameLength - suffix.Length) + suffix;

    public static string UniqueName(IEnumerable<CloudSave> saves, string ownerId, string gameAccount,
        string profession, string preferredName, bool isMain, string? exceptSaveId = null)
    {
        var used = saves.Where(x => x.OwnerId == ownerId && x.GameAccount == gameAccount
                && x.Profession == profession && x.Id != exceptSaveId)
            .Select(x => x.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Unique(used, preferredName.Trim(), isMain);
    }

    public static bool IsAvailable(IEnumerable<CloudSave> saves, CloudSave selected, string preferredName) =>
        !saves.Any(x => x.Id != selected.Id && x.OwnerId == selected.OwnerId && x.GameAccount == selected.GameAccount
            && x.Profession == selected.Profession && string.Equals(x.Name.Trim(), preferredName.Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool AreUnique(IEnumerable<CloudSave> saves) => saves
        .GroupBy(x => (x.OwnerId, x.GameAccount, x.Profession))
        .All(group => group.Select(x => x.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == group.Count());

    /// <summary>Keep the main's name and every already distinct name; only colliding copies gain a suffix.</summary>
    public static IReadOnlyDictionary<string, string> MigrationNames(IEnumerable<CloudSave> saves)
    {
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var role in saves.GroupBy(x => (x.OwnerId, x.GameAccount, x.Profession)))
        {
            var names = role.GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(x => x.IsMain).ThenBy(x => x.Id, StringComparer.Ordinal).ToList()).ToList();
            // Reserve all existing distinct names before allocating any duplicate.
            var used = names.Select(group => group[0].Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var group in names)
            {
                foreach (var duplicate in group.Skip(1))
                {
                    var name = Unique(used, duplicate.Name.Trim(), duplicate.IsMain);
                    used.Add(name); changes.Add(duplicate.Id, name);
                }
            }
        }
        return changes;
    }

    private static string Unique(HashSet<string> used, string preferredName, bool isMain)
    {
        if (string.IsNullOrWhiteSpace(preferredName)) throw new InvalidOperationException("存档名称不能为空。");
        if (preferredName.Length <= MaximumApiNameLength && !used.Contains(preferredName)) return preferredName;
        var name = NewNameBase(preferredName);
        if (!used.Contains(name)) return name;
        var suffix = isMain ? "_主档" : "_副档";
        var marker = name.EndsWith("_云端取回", StringComparison.Ordinal) ? "_云端取回" : suffix;
        var prefix = name.EndsWith(marker, StringComparison.Ordinal) ? name[..^marker.Length] : name;
        var stem = Clip(prefix, MaximumGeneratedNameLength - marker.Length) + marker;
        if (!used.Contains(stem)) return stem;
        // A set of n names cannot occupy all n+1 proposed numbered names.
        for (var number = 2; number <= used.Count + 2; number++)
        {
            var ordinal = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var candidate = Clip(prefix, MaximumGeneratedNameLength - marker.Length - ordinal.Length) + marker + ordinal;
            if (!used.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException("暂时无法生成可区分的存档名称。");
    }

    private static string Clip(string value, int length)
    {
        if (value.Length <= length) return value;
        var end = char.IsHighSurrogate(value[length - 1]) ? length - 1 : length;
        return value[..end];
    }
}
