using Microsoft.Extensions.Options;

namespace InferHub.Tests;

internal static class TestOptions
{
    public static IOptionsMonitor<T> Monitor<T>(T value) => new StaticMonitor<T>(value);

    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
