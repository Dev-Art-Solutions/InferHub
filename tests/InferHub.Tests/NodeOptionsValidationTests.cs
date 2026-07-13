using InferHub.Coordinator.Services;
using InferHub.Node.Configuration;

namespace InferHub.Tests;

public class NodeOptionsValidationTests
{
    [Fact]
    public void CoordinatorOptionsValidatorPassesForDefaults()
    {
        var result = new CoordinatorOptionsValidator().Validate(null, new CoordinatorOptions());

        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? Array.Empty<string>()));
    }

    [Fact]
    public void CoordinatorOptionsValidatorRejectsInvalidUrl()
    {
        var options = new CoordinatorOptions { Url = "not-a-url" };

        var result = new CoordinatorOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, message => message.Contains(nameof(CoordinatorOptions.Url)));
    }

    [Fact]
    public void CoordinatorOptionsValidatorRejectsEmptyUrl()
    {
        var options = new CoordinatorOptions { Url = "" };

        var result = new CoordinatorOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, message => message.Contains(nameof(CoordinatorOptions.Url)));
    }

    [Fact]
    public void CoordinatorOptionsValidatorRejectsNegativeHeartbeatInterval()
    {
        var options = new CoordinatorOptions { HeartbeatInterval = TimeSpan.FromSeconds(-1) };

        var result = new CoordinatorOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, message => message.Contains(nameof(CoordinatorOptions.HeartbeatInterval)));
    }

    [Fact]
    public void CoordinatorOptionsValidatorRejectsZeroModelRefreshInterval()
    {
        var options = new CoordinatorOptions { ModelRefreshInterval = TimeSpan.Zero };

        var result = new CoordinatorOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, message => message.Contains(nameof(CoordinatorOptions.ModelRefreshInterval)));
    }

    [Fact]
    public void CoordinatorOptionsValidatorRejectsNegativeRetryDelay()
    {
        var options = new CoordinatorOptions { RetryDelay = TimeSpan.FromSeconds(-2) };

        var result = new CoordinatorOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, message => message.Contains(nameof(CoordinatorOptions.RetryDelay)));
    }

    [Fact]
    public void NodeOptionsValidatorPassesForDefaults()
    {
        var result = new NodeOptionsValidator().Validate(null, new NodeOptions());

        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? Array.Empty<string>()));
    }

    [Fact]
    public void NodeOptionsValidatorRejectsBlankName()
    {
        var options = new NodeOptions { Name = "   " };

        var result = new NodeOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, message => message.Contains(nameof(NodeOptions.Name)));
    }

    [Fact]
    public void NodeOptionsValidatorRejectsZeroMaxConcurrency()
    {
        var options = new NodeOptions { MaxConcurrency = 0 };

        var result = new NodeOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, message => message.Contains(nameof(NodeOptions.MaxConcurrency)));
    }

    [Fact]
    public void NodeOptionsValidatorAcceptsNullMaxConcurrency()
    {
        var options = new NodeOptions { MaxConcurrency = null };

        var result = new NodeOptionsValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void OllamaOptionsValidatorPassesForDefaults()
    {
        var result = new OllamaOptionsValidator().Validate(null, new OllamaOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void OllamaOptionsValidatorRejectsRelativeEndpoint()
    {
        var options = new OllamaOptions { Endpoint = "/v1/" };

        var result = new OllamaOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, message => message.Contains(nameof(OllamaOptions.Endpoint)));
    }

    [Fact]
    public void OllamaRequestTimeoutOutlastsTheCoordinatorsDispatcherTimeout()
    {
        // HttpClient's own default is 100s. Left unset, the node would abandon a slow cold
        // load three minutes before the coordinator (Dispatcher:TimeoutSeconds = 300) was
        // willing to, and the caller would see a 502 that looked like a node failure.
        var defaultDispatcherTimeout = TimeSpan.FromSeconds(new DispatcherOptions().TimeoutSeconds);

        Assert.True(new OllamaOptions().RequestTimeout >= defaultDispatcherTimeout);
    }

    [Fact]
    public void OllamaOptionsValidatorRejectsNonPositiveRequestTimeout()
    {
        var options = new OllamaOptions { RequestTimeout = TimeSpan.Zero };

        var result = new OllamaOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, message => message.Contains(nameof(OllamaOptions.RequestTimeout)));
    }
}
