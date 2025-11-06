// Features/IAltruistFeatureProvider.cs
using Microsoft.Extensions.Configuration;

namespace Altruist.Features
{
    /// <summary>
    /// Module-provided feature hookup. Takes the current builder 'stage' and returns the next stage.
    /// Examples:
    ///  - AltruistIntermediateBuilder --(SetupGameEngine)--> AltruistConnectionBuilder
    ///  - AltruistConnectionBuilder --(WithWebsocket)-----> IAfterConnectionBuilder
    /// </summary>
    public interface IAltruistFeatureProvider
    {
        string FeatureId { get; }
        object Configure(object stage, IConfiguration config);
    }
}
