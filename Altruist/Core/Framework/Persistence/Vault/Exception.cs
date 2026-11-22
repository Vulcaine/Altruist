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
/// it in between). Typical handling: reload the entity, re-apply the change, save again. Other failures (connection,
/// constraint violations) are never reported as this exception.
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

/// <summary>
/// A vault model's definition is invalid (for example a missing <c>Type</c> property or a foreign key to a column that
/// is neither a primary key nor unique). Thrown by <see cref="VaultDocument.From(Type)"/> with every problem found.
/// </summary>
/// <remarks>These are programming errors in the model classes: fix the model; do not catch this to continue.</remarks>
public sealed class InvalidVaultModelException : InvalidOperationException
{
    /// <summary>The invalid model type.</summary>
    public Type ModelType { get; }

    /// <summary>Every problem found, one message each.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>Creates the exception.</summary>
    /// <param name="modelType">The invalid model type.</param>
    /// <param name="problems">The problems found (at least one).</param>
    public InvalidVaultModelException(Type modelType, IReadOnlyList<string> problems)
        : base($"Invalid vault model {modelType.FullName}: {string.Join(" ", problems)}")
    {
        ModelType = modelType;
        Problems = problems;
    }
}
