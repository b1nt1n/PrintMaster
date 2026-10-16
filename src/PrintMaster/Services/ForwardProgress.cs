namespace PrintMaster.Services;

// A coordinator forwards reports synchronously; the final UI consumer owns marshaling.
// An extra Progress<T> here would queue callbacks and could reorder pass completion.
internal sealed class ForwardProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value)=>report(value);
}
