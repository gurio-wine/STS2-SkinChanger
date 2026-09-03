namespace STS2SkinChanger.Core;

internal sealed record ActionFailure(string Stage, Exception Exception);

internal static class FailureIsolatedActionRunner
{
    internal static IReadOnlyList<ActionFailure> Run(
        IEnumerable<(string Stage, Action Action)> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        var failures = new List<ActionFailure>();
        foreach (var (stage, action) in stages)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failures.Add(new ActionFailure(stage, exception));
            }
        }

        return failures;
    }
}
