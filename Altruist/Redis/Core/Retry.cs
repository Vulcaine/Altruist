/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using Microsoft.Extensions.Logging;

using StackExchange.Redis;

namespace Altruist.Redis;

/// <summary>
/// StackExchange.Redis reconnect policy that never gives up: it allows a new reconnect attempt
/// whenever more than 5 seconds (5000 ms) have passed since the previous one. Used by
/// <see cref="RedisConnectionFactory"/>.
/// </summary>
public sealed class InfiniteReconnectRetryPolicy : IReconnectRetryPolicy
{
    private readonly ILogger _logger;
    /// <summary>Creates the policy.</summary>
    /// <param name="logger">Logger kept for diagnostics (currently not written to).</param>
    public InfiniteReconnectRetryPolicy(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Returns <c>true</c> when more than 5000 ms have elapsed since the last retry; the retry count is ignored.</summary>
    /// <param name="currentRetryCount">Number of attempts so far (ignored).</param>
    /// <param name="timeElapsedMillisecondsSinceLastRetry">Milliseconds since the previous attempt.</param>
    public bool ShouldRetry(long currentRetryCount, int timeElapsedMillisecondsSinceLastRetry)
    {
        var shouldRetry = timeElapsedMillisecondsSinceLastRetry > 5000;
        return shouldRetry;
    }
}
