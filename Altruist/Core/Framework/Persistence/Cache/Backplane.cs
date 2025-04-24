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

namespace Altruist
{
    public enum SupportedBackplaneType
    {
        None = 0,
        Redis = 1
    }

    public class SupportedBackplane
    {
        public SupportedBackplaneType Type { get; set; }
        public ServerInfo ServerInfo { get; set; }

        public SupportedBackplane(SupportedBackplaneType type, ServerInfo serverInfo)
        {
            Type = type;
            ServerInfo = serverInfo;
        }

        public override string ToString()
        {
            if (Type == SupportedBackplaneType.None) return "";
            return $"📡 Backplane[{TypeToString(Type).ToUpper()}] - {ServerInfo.Protocol}://{ServerInfo.Host}:{ServerInfo.Port}";
        }

        private string TypeToString(SupportedBackplaneType type) =>
            type switch
            {
                SupportedBackplaneType.None => "none",
                SupportedBackplaneType.Redis => "redis",
                _ => "none"
            };
    }
}
