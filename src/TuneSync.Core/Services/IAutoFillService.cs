using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

public interface IAutoFillService
{
    /// <param name="options">What to search for; null means everything that's missing.</param>
    Task<List<AutoFillProposal>> BuildProposalsAsync(
        IEnumerable<LibraryFile> files,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        AutoFillOptions? options = null);

    /// <summary>Writes every proposal's non-null fields to disk and, when
    /// <paramref name="undoLogDirectory"/> is given, records each file's pre-apply tags there
    /// first. Returns one error message per file that failed to write; successes aren't reported
    /// individually since the proposal's own Status reflects it.</summary>
    List<string> ApplyProposals(IEnumerable<AutoFillProposal> proposals, string? undoLogDirectory = null);
}
