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
public sealed class OptimisticConcurrencyException : Exception
{
    public Type ModelType { get; }
    public string? StorageId { get; }
    public int? ExpectedAffected { get; }
    public int? ActualAffected { get; }

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
