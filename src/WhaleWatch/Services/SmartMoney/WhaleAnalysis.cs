using System.Collections.Generic;
using System.Linq;

namespace WhaleWatch.Services.SmartMoney;

// Collects the lines of the analysis block of one whale alert. Notes always come before warnings,
// whatever order they were added in.
public sealed class WhaleAnalysis
{
    private readonly List<string> _notes = [];
    private readonly List<string> _warnings = [];

    public bool IsEmpty => _notes.Count == 0 && _warnings.Count == 0;

    public IReadOnlyList<string> Lines => _notes.Concat(_warnings).ToList();

    public void AddNote(string line) => _notes.Add(line);

    public void AddWarning(string line) => _warnings.Add(line);
}
