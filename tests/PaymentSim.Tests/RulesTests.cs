using PaymentSim.Core;

namespace PaymentSim.Tests;

public sealed class RulesTests
{
    public static TheoryData<OrderType, OrderStatus, OrderStatus, bool> Transitions
    {
        get
        {
            var data = new TheoryData<OrderType, OrderStatus, OrderStatus, bool>();
            (OrderType, OrderStatus, OrderStatus)[] allowed =
            [
                (OrderType.PayIn, OrderStatus.Pending, OrderStatus.Paid),
                (OrderType.PayIn, OrderStatus.Pending, OrderStatus.Failed),
                (OrderType.PayIn, OrderStatus.Pending, OrderStatus.Expired),
                (OrderType.Payout, OrderStatus.Pending, OrderStatus.Processing),
                (OrderType.Payout, OrderStatus.Pending, OrderStatus.Rejected),
                (OrderType.Payout, OrderStatus.Processing, OrderStatus.Succeeded),
                (OrderType.Payout, OrderStatus.Processing, OrderStatus.Failed)
            ];
            foreach (var type in Enum.GetValues<OrderType>())
                foreach (var from in Enum.GetValues<OrderStatus>())
                    foreach (var to in Enum.GetValues<OrderStatus>()) data.Add(type, from, to, allowed.Contains((type, from, to)));
            return data;
        }
    }

    [Theory, MemberData(nameof(Transitions))]
    public void Transition_AllStatusPairs_EnforcesTypeSpecificGraph(OrderType type, OrderStatus from, OrderStatus to, bool allowed)
    {
        var order = new PaymentOrder { Type = type, Status = from };
        var now = new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(allowed, OrderStateMachine.CanTransition(type, from, to));
        if (allowed)
        {
            Assert.True(OrderStateMachine.CanTransition(from, to));
            OrderStateMachine.Transition(order, to, now);
            Assert.Equal(to, order.Status);
            Assert.Equal(OrderStateMachine.IsTerminal(to) ? now : null, order.CompletedAt);
        }
        else Assert.Throws<InvalidOrderTransitionException>(() => OrderStateMachine.Transition(order, to, now));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("normal")]
    public void Parse_DefaultScenario_ReturnsNormal(string? text) => Assert.Equal(new Scenario(), ScenarioParser.Parse(text));

    [Theory]
    [InlineData("delay:-1")] [InlineData("duplicate:0")] [InlineData("duplicate:21")] [InlineData("delay:86401")]
    [InlineData("delay:abc")] [InlineData("unknown")] [InlineData("normal,")] [InlineData("delay:1,delay:2")]
    public void Parse_InvalidScenario_ThrowsFormatException(string text) => Assert.Throws<FormatException>(() => ScenarioParser.Parse(text));

    [Fact]
    public void Parse_CombinedScenario_EnablesEveryOption()
    {
        Assert.Equal(new Scenario(3, 2, true, true, true, true),
            ScenarioParser.Parse("delay:3,duplicate:2,out-of-order,bad-signature,no-callback,decline"));
    }

    [Fact]
    public void NextDelay_RepeatedAttempts_IncreasesWithBoundedJitterAndCap()
    {
        var options = new RetryOptions();
        var rng = new Random(123);
        var previous = 0d;
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            var baseline = 5 * Math.Pow(2, attempt - 1);
            var actual = RetryPolicy.NextDelay(attempt, options, rng).TotalSeconds;
            Assert.InRange(actual, Math.Min(600, baseline * .8), Math.Min(600, baseline * 1.2));
            Assert.True(actual >= previous);
            previous = actual;
        }
        Assert.Equal(8, options.MaxAttempts);
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.NextDelay(0, options, rng));
    }
}
