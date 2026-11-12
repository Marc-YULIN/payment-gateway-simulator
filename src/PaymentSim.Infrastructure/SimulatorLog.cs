using Microsoft.Extensions.Logging;
using PaymentSim.Core;

namespace PaymentSim.Infrastructure;

internal static partial class SimulatorLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "{Worker} poll failed with {ErrorType}")]
    public static partial void PollFailed(ILogger logger, string worker, string errorType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Callback {OutboxId} attempt {AttemptNo} ended as {Outcome}")]
    public static partial void CallbackResult(ILogger logger, long outboxId, int attemptNo, AttemptOutcome outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo merchant private key path: {KeyPath}")]
    public static partial void KeyPath(ILogger logger, string keyPath);
}
