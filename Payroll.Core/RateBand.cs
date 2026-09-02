namespace Payroll.Core;

/// <summary>A single tax/USC band as reported on an RPN: a rate applying up to a yearly cumulative cut-off (null = top/unbounded band).</summary>
public sealed record RateBand(int Index, decimal RatePercent, decimal? YearlyCutOff);
