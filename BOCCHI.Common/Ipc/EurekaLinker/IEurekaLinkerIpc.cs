namespace BOCCHI.Common.Ipc.EurekaLinker;

public readonly record struct EurekaLinkerPotTimer(uint FateId, long SpawnUnix, bool Alive);

public interface IEurekaLinkerIpc
{
    bool IsAvailable { get; }

    bool TryGetPotTimers(out EurekaLinkerPotTimer[] timers);
}
