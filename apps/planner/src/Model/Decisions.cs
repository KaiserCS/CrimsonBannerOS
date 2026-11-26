// SPDX-License-Identifier: GPL-3.0-or-later
namespace CrimsonPlanner.Model;

/// <summary>
/// Councils decide; the plan only advises. A quota becomes the period's only once a council ratifies it, amends it or
/// rejects it (the last enacted quota then stands). A major shift, a change beyond the plan's threshold from the last
/// quota enacted, also needs a quorum of councils' votes in favour: the human-in-the-loop override.
/// </summary>
public static class Decisions
{
    /// <summary>Records a decision; returns why it can't be recorded, or null.</summary>
    public static string? Decide(Plan plan, int period, ProductResult r, DecisionStatus status, double? amendedTo,
                                 string decidedBy, int votesFor = 0, int votesAgainst = 0, string note = "")
    {
        if (string.IsNullOrWhiteSpace(decidedBy)) return "Say which council decides.";
        if (status == DecisionStatus.Proposed) return "A decision ratifies, amends or rejects.";
        double? value = status switch
        {
            DecisionStatus.Ratified => r.Proposed,
            DecisionStatus.Amended => amendedTo,
            _ => r.LastEnacted,
        };
        if (status == DecisionStatus.Amended && (value is not double v || v < 0 || double.IsNaN(v) || double.IsInfinity(v)))
            return "An amended quota has to be a number, 0 or more.";
        // is what's enacted a major shift? (the proposal's own flag, or the amended figure's change)
        bool major = status switch
        {
            DecisionStatus.Ratified => r.Major,
            DecisionStatus.Amended => IsMajor(plan, value!.Value, r.LastEnacted),
            _ => false,
        };
        int quorum = plan.Settings.Quorum;
        if (major && (votesFor < quorum || votesFor <= votesAgainst))
            return $"This is a major shift ({Describe(value!.Value, r.LastEnacted)}): it needs a quorum, {quorum} councils' votes in favour and more for than against (now {votesFor} for, {votesAgainst} against).";
        plan.Decisions.RemoveAll(d => d.Period == period && d.Product == r.Product.Id);
        plan.Decisions.Add(new QuotaDecision
        {
            Period = period, Product = r.Product.Id, Proposed = r.Proposed, Status = status, Value = value,
            DecidedBy = decidedBy.Trim(), Major = major, VotesFor = votesFor, VotesAgainst = votesAgainst, Quorum = quorum,
            Note = note, When = DateTime.UtcNow,
        });
        return null;
    }

    public static bool IsMajor(Plan plan, double value, double? last) =>
        last is not double l || l <= 0 || Math.Abs((value - l) / l) > plan.Settings.MajorShift;

    public static string Describe(double value, double? last) =>
        last is double l && l > 0 ? $"{(value - l) / l:+0.#%;-0.#%;0%} on the last quota" : "no quota enacted before";

    public static QuotaDecision? For(Plan plan, int period, string productId) =>
        plan.Decisions.Where(d => d.Period == period && d.Product == productId).OrderByDescending(d => d.When).FirstOrDefault();
}
