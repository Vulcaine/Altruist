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

namespace Altruist;

public class InterceptContext
{
    public string EventName { get; }

    public InterceptContext(string eventName)
    {
        EventName = eventName;
    }
}


public interface IInterceptor
{
    Task Intercept(InterceptContext context, IPacket eventData);
}


public class RelayInterceptor : IInterceptor
{
    private readonly IRelayService _relayService;

    public RelayInterceptor(IRelayService relayService)
    {
        _relayService = relayService;
    }

    public async Task Intercept(InterceptContext context, IPacket eventData)
    {
        if (context.EventName == _relayService.RelayEvent)
        {
            await _relayService.Relay(eventData);
        }
    }
}
