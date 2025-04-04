namespace Altruist;

public static class OutgressEP
{
    public const string NotifyPlayerJoinedRoom = "room-joined";
    public const string NotifyPlayerLeftRoom = "player-left";
    public const string NotifyGameStarted = "game-started";
    public const string NotifyFailed = "failed";
    public const string NotifySync = "sync";
}

public static class IngressEP
{
    public const string Handshake = "handshake";
    public const string JoinGame = "join-game";
    public const string LeaveGame = "leave-game";
    public const string Shoot = "SHOOT";
}
