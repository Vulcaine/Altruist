// OptimisticConcurrencyException.cs
/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0
*/

namespace Altruist.Persistence;

/// <summary>
/// Thrown when an optimistic concurrency (Version) check fails.
/// </summary>
/// <remarks>
/// Raised by vault saves when the stored row's version no longer equals the entity's version (someone else saved
/// it in between). Typical handling: reload the entity, re-apply the change, save again. Batch saves also wrap
/// any other failure of the batch statement in this exception (see <see cref="Exception.InnerException"/>).
/// </remarks>
public sealed class OptimisticConcurrencyException : Exception
{
    /// <summary>The vault model type being saved.</summary>
    public Type ModelType { get; }
    /// <summary>StorageId of the conflicting entity; null for batch saves.</summary>
    public string? StorageId { get; }
    /// <summary>Rows the operation expected to write (batch saves), if known.</summary>
    public int? ExpectedAffected { get; }
    /// <summary>Rows actually written (batch saves), if known.</summary>
    public int? ActualAffected { get; }

    /// <summary>Creates the exception.</summary>
    /// <param name="modelType">The vault model type.</param>
    /// <param name="storageId">StorageId of the entity, when a single entity failed.</param>
    /// <param name="message">Error message.</param>
    /// <param name="inner">Underlying exception, if any.</param>
    /// <param name="expectedAffected">Expected written rows, if known.</param>
    /// <param name="actualAffected">Actually written rows, if known.</param>
    /// <exception cref="ArgumentNullException"><paramref name="modelType"/> is null.</exception>
    public OptimisticConcurrencyException(
        Type modelType,
        string? storageId,
        string message,
        Exception? inner = null,
        int? expectedAffected = null,
        int? actualAffected = null)
        : base(message, inner)
    {
        ModelType = modelType ?? throw new ArgumentNullException(nameof(modelType));
        StorageId = storageId;
        ExpectedAffected = expectedAffected;
        ActualAffected = actualAffected;
    }
}
