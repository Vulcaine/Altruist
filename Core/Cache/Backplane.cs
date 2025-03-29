using System.Security.Cryptography.X509Certificates;

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
