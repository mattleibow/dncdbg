namespace DncDbg.Engine;

public sealed partial class DebugEngine
{
    private readonly Dictionary<CodeLocation, int> _initializationSites = [];
    private readonly HashSet<(ModuleKey Module, int Type)> _completedInitializers = [];

    private void WatchInitializers(LoadedModule module)
    {
        if (module.Metadata?.HasSymbols != true)
            return;
        foreach (var constructor in module.Metadata.GetStaticConstructorReturns())
        {
            try
            {
                CodeLocation location = AcquireNativeBreakpoint(module, constructor.Method, constructor.Offset);
                _initializationSites[location] = constructor.Type;
            }
            catch (Exception e)
            {
                Log?.Invoke("Cannot observe static initialization: " + e.Message);
            }
        }
    }
}
