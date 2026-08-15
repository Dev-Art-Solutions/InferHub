using InferHub.Shared.Images;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Validates <c>Images:*</c> at startup (phase 56).
/// </summary>
/// <remarks>
/// <para>
/// The <em>check</em> is <see cref="ImageJobOptions.TryValidate"/> in <c>InferHub.Shared</c> and this
/// is only the host's plumbing, which is phase-38 D3's line: a plain options class cannot see
/// <c>IValidateOptions&lt;T&gt;</c> without a package, and two hand-written copies of "which values
/// are legal" is how a hub and a solo node come to disagree about the same key.
/// </para>
/// <para>
/// <b>An unrecognised value fails the host rather than falling back to <c>none</c></b>
/// (`FleetOptionsValidator`'s reasoning, third instance): a typo that quietly disables persistence
/// drops every job on the next restart, which is the exact failure the key was turned on to prevent.
/// </para>
/// </remarks>
public sealed class ImageEdgeOptionsValidator : IValidateOptions<ImageEdgeOptions>
{
    public ValidateOptionsResult Validate(string? name, ImageEdgeOptions options) =>
        options.Jobs.TryValidate(out var error)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(error);
}
