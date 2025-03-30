namespace Altruist;

public static class OutgressEP
{
    public const string NOTIFY_PLAYER_JOINED_ROOM = "room-joined";
    public const string NOTIFY_PLAYER_LEFT_ROOM = "player-left";
    public const string NOTIFY_GAME_STARTED = "game-started";
    public const string NOTIFY_FAILED = "failed";
    public const string NOTIFY_SYNC = "sync";
}

public static class IngressEP
{
    public const string HANDSHAKE = "handshake";
    public const string JOIN_GAME = "join-game";
    public const string LEAVE_GAME = "leave-game";
    public const string SHOOT = "SHOOT";
}
