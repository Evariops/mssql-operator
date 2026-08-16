using MsSqlOperator.Entities.V1Alpha1;

namespace MsSqlOperator.Configuration;

/// <summary>
/// How a spec becomes a <see cref="SqlImageSelection"/>. One place knows which fields feed the
/// resolver, so the next knob (another OS base, a channel) is added here and not hunted down across
/// the builder, both controllers and both validators — which is exactly how they drifted apart before.
/// </summary>
public static class SqlImageSelectionExtensions
{
    public static SqlImageSelection ImageSelection(this SqlInstanceSpec spec) =>
        new(spec.Version, spec.Edition, spec.CumulativeUpdate, spec.BaseOs, spec.Image, spec.ImageTag);

    /// <summary>The AG carries the edition; its instanceTemplate carries everything else.</summary>
    public static SqlImageSelection ImageSelection(this AgInstanceTemplateSpec template, string edition) =>
        new(template.Version, edition, template.CumulativeUpdate, template.BaseOs, template.Image, template.ImageTag);
}
