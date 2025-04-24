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

using Cassandra;

namespace Altruist.ScyllaDB;


public class AltruistScyllaDefaultRetryPolicy : IExtendedRetryPolicy, IRetryPolicy
{
    private DefaultRetryPolicy _defaultRetryPolicy = new DefaultRetryPolicy();
    private IScyllaDbProvider _scyllaDbProvider;

    public AltruistScyllaDefaultRetryPolicy(IScyllaDbProvider scyllaDbProvider)
    {
        _scyllaDbProvider = scyllaDbProvider;
    }

    public RetryDecision OnReadTimeout(IStatement query, ConsistencyLevel cl, int requiredResponses, int receivedResponses, bool dataRetrieved, int nbRetry)
    {
        try
        {
            RaiseOnFailed(nbRetry);
            return _defaultRetryPolicy.OnReadTimeout(query, cl, requiredResponses, receivedResponses, dataRetrieved, nbRetry);
        }
        catch (Exception nex)
        {
            _scyllaDbProvider.RaiseOnRetryExhaustedEvent(nex);
            return RetryDecision.Rethrow();
        }
    }

    public RetryDecision OnRequestError(IStatement statement, Configuration config, Exception ex, int nbRetry)
    {
        try
        {
            RaiseOnFailed(nbRetry);
            return _defaultRetryPolicy.OnRequestError(statement, config, ex, nbRetry);
        }
        catch (Exception nex)
        {
            _scyllaDbProvider.RaiseOnRetryExhaustedEvent(nex);
            return RetryDecision.Rethrow();
        }
    }

    public RetryDecision OnUnavailable(IStatement query, ConsistencyLevel cl, int requiredReplica, int aliveReplica, int nbRetry)
    {
        try
        {
            RaiseOnFailed(nbRetry);
            return _defaultRetryPolicy.OnUnavailable(query, cl, requiredReplica, aliveReplica, nbRetry);
        }
        catch (Exception nex)
        {
            _scyllaDbProvider.RaiseOnRetryExhaustedEvent(nex);
            return RetryDecision.Rethrow();
        }
    }

    public RetryDecision OnWriteTimeout(IStatement query, ConsistencyLevel cl, string writeType, int requiredAcks, int receivedAcks, int nbRetry)
    {
        try
        {
            RaiseOnFailed(nbRetry);
            return _defaultRetryPolicy.OnWriteTimeout(query, cl, writeType, requiredAcks, receivedAcks, nbRetry);
        }
        catch (Exception nex)
        {
            _scyllaDbProvider.RaiseOnRetryExhaustedEvent(nex);
            return RetryDecision.Rethrow();
        }
    }

    private void RaiseOnFailed(int nbRetry, Exception? ex = null)
    {
        if (nbRetry == 1)
        {
            _scyllaDbProvider.RaiseFailedEvent(ex ?? new Exception("Connection failed."));
        }
    }
}
