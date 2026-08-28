using Microsoft.Accordant;

namespace ResonateConformance;

public static partial class ResonateSpec
{
    public static Spec<ServerState> Build()
    {
        var spec = Spec.For<ServerState>().WithJsonPrinters();

        RegisterExternalSteps(spec);
        RegisterInternalSteps(spec);

        return spec;
    }
}
