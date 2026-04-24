/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// Typed helpers for the generic state-machine metadata channel.
/// Keeps consumers from scattering raw <c>object</c> casts when they attach
/// per-state data blobs via <see cref="StateMachineBuilder{TContext}.StateConfig.Data(object)"/>.
/// </summary>
public static class StateMachineDataExtensions
{
    /// <summary>Returns the active state's data blob cast to <typeparamref name="TData"/>, or null.</summary>
    public static TData? GetCurrentStateData<TData>(this IStateContext context)
        where TData : class
        => context.CurrentStateData as TData;

    /// <summary>Try-get wrapper around <see cref="GetCurrentStateData{TData}(IStateContext)"/>.</summary>
    public static bool TryGetCurrentStateData<TData>(this IStateContext context, out TData? data)
        where TData : class
    {
        data = context.CurrentStateData as TData;
        return data != null;
    }

    /// <summary>Returns the configured data blob for a named state cast to <typeparamref name="TData"/>, or null.</summary>
    public static TData? GetStateData<TContext, TData>(this StateMachineDef<TContext> def, string stateName)
        where TContext : class, IStateContext
        where TData : class
        => def.GetData(stateName) as TData;

    /// <summary>Try-get wrapper around <see cref="GetStateData{TContext, TData}(StateMachineDef{TContext}, string)"/>.</summary>
    public static bool TryGetStateData<TContext, TData>(this StateMachineDef<TContext> def, string stateName, out TData? data)
        where TContext : class, IStateContext
        where TData : class
    {
        data = def.GetData(stateName) as TData;
        return data != null;
    }

    /// <summary>Returns the active state's locomotion throttle, or <paramref name="fallback"/> if none is attached.</summary>
    public static float GetCurrentMovementThrottle(this IStateContext context, float fallback = 1f)
        => Math.Clamp(context.CurrentStateMotion?.MovementThrottle ?? fallback, 0f, 1f);

    /// <summary>Samples the active state's motion profile at the context's normalized progress.</summary>
    public static System.Numerics.Vector3 EvaluateCurrentMotion(this IStateContext context)
        => context.CurrentStateMotion?.Evaluate(context.Progress) ?? System.Numerics.Vector3.Zero;
}
