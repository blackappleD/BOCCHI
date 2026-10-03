using BOCCHI.Common.Config;
using ECommons.Throttlers;
using Ocelot.Services.Logger;

namespace BOCCHI.Automator.Services;

/// <summary>Accepts a pending raise after the configured delay, without letting the offer run out.</summary>
public class RaiseAcceptor(AutomatorConfig config, ILogger<RaiseAcceptor> logger)
{
    private const int RaiseOfferExpiryMarginSeconds = 5;

    private DateTime? raiseOfferSeenUtc;

    private string raiseOfferCaster = string.Empty;

    public void Tick()
    {
        if (!config.AutoAcceptRaise)
        {
            return;
        }

        if (!RaisePrompt.TryGetPending(out string caster, out int timeLeft))
        {
            Reset();
            return;
        }

        if (raiseOfferSeenUtc == null || raiseOfferCaster != caster)
        {
            raiseOfferSeenUtc = DateTime.UtcNow;
            raiseOfferCaster = caster;
            logger.Info(
                "Raise offered by {Caster} (timer {TimeLeft}), accepting in {Delay}s",
                caster,
                timeLeft,
                config.AutoAcceptRaiseDelaySeconds);
        }

        bool delayElapsed = DateTime.UtcNow - raiseOfferSeenUtc >= TimeSpan.FromSeconds(config.AutoAcceptRaiseDelaySeconds);
        // Never let the delay run the offer out. Assumes the agent timer counts seconds (the log
        // above prints it raw); if it is finer-grained this only fires at the very end — no harm.
        bool offerExpiring = timeLeft <= RaiseOfferExpiryMarginSeconds;
        if ((!delayElapsed && !offerExpiring) || !EzThrottler.Throttle("Dead::AcceptRaise", 1000))
        {
            return;
        }

        if (RaisePrompt.TryAccept(out caster))
        {
            logger.Info("Accepted raise from {Caster}", caster);
            Reset();
        }
    }

    public void Reset()
    {
        raiseOfferSeenUtc = null;
        raiseOfferCaster = string.Empty;
    }
}
