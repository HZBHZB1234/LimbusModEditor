using LimbusModEditor.Formats.Abstractions;

namespace LimbusModEditor.Application.Formats;

public sealed class FormatRegistry(IEnumerable<IModFormatHandler> handlers)
{
    private readonly IReadOnlyList<IModFormatHandler> _handlers = handlers.ToArray();
    public IReadOnlyList<IModFormatHandler> Handlers => _handlers;

    public async Task<FormatProbeResult?> ProbeAsync(Stream input, string? fileName = null, CancellationToken cancellationToken = default)
    {
        var results = new List<FormatProbeResult>();
        foreach (var handler in _handlers)
        {
            if (input.CanSeek) input.Position = 0;
            var result = await handler.ProbeAsync(input, fileName, cancellationToken);
            if (result.IsMatch) results.Add(result);
        }
        return results.OrderByDescending(x => x.Confidence).FirstOrDefault();
    }
}
