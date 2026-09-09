using System;
using AspectInjector.Broker;
using Serilog.Events;

namespace ParityProof.Core.Logging;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = true)]
[Injection(typeof(LogMethodAspect))]
public sealed class LogMethodAttribute : Attribute
{
    public LogEventLevel Level { get; set; } = LogEventLevel.Debug;

    public LogMethodAttribute()
    {
    }

    public LogMethodAttribute(LogEventLevel level)
    {
        Level = level;
    }
}
