namespace InferHub.Shared.Vector.Storage;

public enum DistanceMetric
{
    Cosine,
    Dot,
    L2
}

public static class DistanceMetricExtensions
{
    public const string Cosine = "cosine";
    public const string Dot = "dot";
    public const string L2 = "l2";

    public static bool TryParse(string? value, out DistanceMetric metric)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case Cosine:
                metric = DistanceMetric.Cosine;
                return true;
            case Dot:
                metric = DistanceMetric.Dot;
                return true;
            case L2:
                metric = DistanceMetric.L2;
                return true;
            default:
                metric = DistanceMetric.Cosine;
                return false;
        }
    }

    public static string ToWireString(this DistanceMetric metric) => metric switch
    {
        DistanceMetric.Cosine => Cosine,
        DistanceMetric.Dot => Dot,
        DistanceMetric.L2 => L2,
        _ => Cosine
    };
}
