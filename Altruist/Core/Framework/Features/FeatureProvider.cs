// Features/IAltruistFeatureProvider.cs
namespace Altruist.Features
{
    /// <summary>
    /// Module-provided feature hookup. Takes the current builder 'stage' and returns the next stage.
    /// Examples:
    ///  - AltruistIntermediateBuilder --(SetupGameEngine)--> AltruistConnectionBuilder
    ///  - AltruistConnectionBuilder --(WithWebsocket)-----> IAfterConnectionBuilder
    /// </summary>
    /// <remarks>
    /// Implementations need a public parameterless constructor to be picked up by
    /// <see cref="FeatureRegistry.AutoDiscover"/>, or can be added with <see cref="FeatureRegistry.Register"/>.
    /// Note: no framework code currently calls <see cref="FeatureRegistry"/> or <see cref="Configure"/>; this is an
    /// extension point without a built-in caller. To add services, prefer the normal DI attributes
    /// (<c>[Service]</c>, modules) over this interface.
    /// </remarks>
    public interface IAltruistFeatureProvider
    {
        /// <summary>Unique feature id used as the registry key; a later registration with the same id replaces the earlier one.</summary>
        string FeatureId { get; }
        /// <summary>Applies the feature to a builder stage and returns the next stage.</summary>
        /// <param name="stage">The current builder stage object (the caller decides the concrete type).</param>
        /// <param name="provider">Service provider available to the feature.</param>
        /// <returns>The builder stage to continue with (may be <paramref name="stage"/> itself).</returns>
        object Configure(object stage, IServiceProvider provider);
    }
}
