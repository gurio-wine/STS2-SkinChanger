namespace STS2SkinChanger.Catalog;

internal sealed partial class SkinCatalog
{
    // Local IDs only gain ::source: when duplicate manifests are installed. Always qualify
    // Workshop sources on the wire, so installing an unrelated duplicate cannot change identity.
    public IReadOnlyList<string> GetMultiplayerSourceIds(string groupId, string optionId)
    {
        var options = GetRawCharacterOptions(groupId).ToDictionary(option => option.Id, StringComparer.OrdinalIgnoreCase);
        return GetCompositionSourceOptionIds(groupId, optionId)
            .Where(options.ContainsKey)
            .Select(id => GetMultiplayerSourceId(options[id]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> ResolveMultiplayerSourceIds(string groupId, IEnumerable<string> sourceIds)
    {
        var options = GetRawCharacterOptions(groupId)
            .Where(option => option.Id != BaseOptionId).ToArray();
        var result = new List<string>();
        foreach (var sourceId in sourceIds.Where(id => !string.IsNullOrWhiteSpace(id))
                     .Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var matches = options.Where(option => GetMultiplayerSourceId(option)
                .Equals(sourceId, StringComparison.OrdinalIgnoreCase)).ToArray();
            // Protocol 9 peers from older releases advertise their local ID. Exact local IDs
            // remain readable, but never strip a foreign source token or guess among variants.
            if (matches.Length == 0)
                matches = options.Where(option => option.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1 && !result.Contains(matches[0].Id, StringComparer.OrdinalIgnoreCase))
                result.Add(matches[0].Id);
        }
        return result;
    }

    private string GetMultiplayerSourceId(SkinOption option)
    {
        var provider = option.EffectiveProviderId;
        if (_workshopSourceIds == null || !_workshopSourceIds.TryGetValue(provider, out var workshopId) || workshopId == 0)
            return option.Id;
        var identity = _providerInstanceIdentities?.FirstOrDefault(candidate =>
            candidate.InstanceId.Equals(provider, StringComparison.OrdinalIgnoreCase));
        var manifestId = identity?.ManifestId ?? provider;
        return ProviderInstanceIdentityPolicy.ScopeOptionId(provider,
            manifestId + "::source:" + workshopId.ToString(System.Globalization.CultureInfo.InvariantCulture), option.Id);
    }
}
