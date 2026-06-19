using InferHub.Shared.Contracts;

namespace InferHub.Node.Configuration;

public static class ModelFilter
{
    public static IReadOnlyList<ModelInfo> Apply(
        IReadOnlyList<ModelInfo> models,
        ModelFilterOptions filter)
    {
        if (models.Count == 0)
        {
            return models;
        }

        var include = Normalize(filter.Include);
        var exclude = Normalize(filter.Exclude);

        if (include.Count == 0 && exclude.Count == 0)
        {
            return models;
        }

        return models
            .Where(model =>
            {
                var name = model.Name?.Trim() ?? string.Empty;

                if (include.Count > 0 && !include.Contains(name))
                {
                    return false;
                }

                if (exclude.Count > 0 && exclude.Contains(name))
                {
                    return false;
                }

                return true;
            })
            .ToArray();
    }

    private static HashSet<string> Normalize(IEnumerable<string>? values)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (values is null)
        {
            return set;
        }

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            set.Add(value.Trim());
        }

        return set;
    }
}
