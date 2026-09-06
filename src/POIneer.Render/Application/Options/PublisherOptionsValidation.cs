using POIneer.Render.Application.Contracts;

namespace POIneer.Render.Application.Options;

public static class PublisherOptionsValidation
{
    public const string RequiredDestinationDirMessage = "Publisher:DestinationDir must be set when Publisher:Target is Local";
    public const string DefinedTargetMessage = "Publisher:Target must be a defined publisher target";

    public static bool HasRequiredDestinationDir(PublisherOptions options)
        => options.Target != DatasetPublisherTarget.Local || !string.IsNullOrWhiteSpace(options.DestinationDir);

    public static bool HasDefinedTarget(PublisherOptions options)
        => Enum.IsDefined(options.Target);

    public static bool IsDefinedTargetName(string? target)
        => string.IsNullOrWhiteSpace(target)
           || Enum.TryParse<DatasetPublisherTarget>(target, ignoreCase: false, out var parsed)
           && Enum.IsDefined(parsed);
}
