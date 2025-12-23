namespace NArk.Abstractions.Time;

public interface IChainTimeProvider
{
    Task<TimeHeight> GetChainTime();
}