using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace PaymentSim.Core;

public sealed class InvalidOrderTransitionException(OrderStatus from, OrderStatus to)
    : Exception($"Invalid order transition from {from} to {to}.");

public static class OrderStateMachine
{
    public static bool CanTransition(OrderStatus from, OrderStatus to) => (from, to) switch
    {
        (OrderStatus.Pending, OrderStatus.Paid or OrderStatus.Failed or OrderStatus.Expired or OrderStatus.Processing or OrderStatus.Rejected) => true,
        (OrderStatus.Processing, OrderStatus.Succeeded or OrderStatus.Failed) => true,
        _ => false
    };

    public static bool CanTransition(OrderType type, OrderStatus from, OrderStatus to) => type switch
    {
        OrderType.PayIn => from == OrderStatus.Pending && to is OrderStatus.Paid or OrderStatus.Failed or OrderStatus.Expired,
        OrderType.Payout => (from == OrderStatus.Pending && to is OrderStatus.Processing or OrderStatus.Rejected) ||
                            (from == OrderStatus.Processing && to is OrderStatus.Succeeded or OrderStatus.Failed),
        _ => false
    };

    public static void Transition(PaymentOrder order, OrderStatus to, DateTimeOffset now)
    {
        if (!CanTransition(order.Type, order.Status, to)) throw new InvalidOrderTransitionException(order.Status, to);
        order.Status = to;
        order.Version++;
        if (IsTerminal(to)) order.CompletedAt = now;
    }

    public static bool IsTerminal(OrderStatus status) => status is not (OrderStatus.Pending or OrderStatus.Processing);
}

public sealed record Scenario(int DelaySeconds = 0, int DuplicateCount = 1, bool OutOfOrder = false,
    bool BadSignature = false, bool NoCallback = false, bool Decline = false);

public static class ScenarioParser
{
    public static Scenario Parse(string? value)
    {
        var scenario = new Scenario();
        if (string.IsNullOrWhiteSpace(value)) return scenario;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in value.Split(','))
        {
            var token = part.Trim();
            var pieces = token.Split(':');
            if (!seen.Add(pieces[0])) throw new FormatException("Duplicate scenario option.");
            scenario = token switch
            {
                "normal" => scenario,
                "out-of-order" => scenario with { OutOfOrder = true },
                "bad-signature" => scenario with { BadSignature = true },
                "no-callback" => scenario with { NoCallback = true },
                "decline" => scenario with { Decline = true },
                _ when pieces.Length == 2 && pieces[0] == "delay" && int.TryParse(pieces[1], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 86400 => scenario with { DelaySeconds = n },
                _ when pieces.Length == 2 && pieces[0] == "duplicate" && int.TryParse(pieces[1], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 20 => scenario with { DuplicateCount = n },
                _ => throw new FormatException("Unknown or invalid scenario option.")
            };
        }
        return scenario;
    }
}

public sealed class RetryOptions
{
    [Range(0.001, 600)] public double BaseDelaySeconds { get; set; } = 5;
    [Range(0.001, 86400)] public double MaxDelaySeconds { get; set; } = 600;
    [Range(1, 100)] public int MaxAttempts { get; set; } = 8;
}

public static class RetryPolicy
{
    public static TimeSpan NextDelay(int attempt, RetryOptions options, Random rng)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        return TimeSpan.FromSeconds(Math.Min(options.MaxDelaySeconds,
            options.BaseDelaySeconds * Math.Pow(2, Math.Min(attempt - 1, 30)) * (0.8 + rng.NextDouble() * 0.4)));
    }
}
