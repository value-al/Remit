using System.Globalization;
using Remit.BuildingBlocks;

namespace Remit.Reconciliation.Matching;

/// <summary>
/// The statement format the simulated providers produce and real adapters translate to:
/// <c>reference,kind,amount,currency,settled_at</c>, header row required, RFC 3339 timestamps.
/// Strict on purpose — a statement that does not parse is an exception, not a guess.
/// </summary>
public static class StatementCsv
{
    public static IReadOnlyList<StatementLine> Parse(string csv)
    {
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || !string.Equals(lines[0], "reference,kind,amount,currency,settled_at", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Statement must start with the header: reference,kind,amount,currency,settled_at");
        }

        var result = new List<StatementLine>(lines.Length - 1);
        for (var i = 1; i < lines.Length; i++)
        {
            var cells = lines[i].Split(',');
            if (cells.Length != 5)
            {
                throw new FormatException($"Line {i + 1}: expected 5 columns, found {cells.Length}.");
            }

            if (!Enum.TryParse<MovementKind>(cells[1], ignoreCase: true, out var kind))
            {
                throw new FormatException($"Line {i + 1}: unknown kind '{cells[1]}'.");
            }

            if (!decimal.TryParse(cells[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            {
                throw new FormatException($"Line {i + 1}: amount '{cells[2]}' is not a number.");
            }

            if (!DateTimeOffset.TryParse(cells[4], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var settledAt))
            {
                throw new FormatException($"Line {i + 1}: settled_at '{cells[4]}' is not a timestamp.");
            }

            result.Add(new StatementLine(cells[0].Trim(), kind, Money.Of(amount, cells[3].Trim()), settledAt));
        }

        return result;
    }
}
